#!/usr/bin/env python3
"""Forced-install fault-soak report: failover per leader kill, whole-partition installs with their phase breakdown, the
Kommander install counters, memory peaks, unreachable spans outside fault windows, crash dumps and the harness verdicts.
  python3 tools/p3c/install-soak-report.py runs/scenarios/<scenario>-<tag> <clustername> [diagnostics/<tag>-watch.log]"""
import sys, json, csv, re, glob, os, datetime as dt
from collections import defaultdict
run, cluster = sys.argv[1], sys.argv[2]
watch = sys.argv[3] if len(sys.argv) > 3 else None
art = os.path.join(run, 'artifacts', 'run')
def P(*a): print(*a)
def ts(s):  # node-log timestamps are UTC naive
    return dt.datetime.strptime(s[:19], '%Y-%m-%d %H:%M:%S').replace(tzinfo=dt.timezone.utc)
def tz(s):
    s = s.strip().rstrip('Z')
    if '.' in s: s = s.split('.')[0]
    return dt.datetime.fromisoformat(s).replace(tzinfo=dt.timezone.utc)

# --- timeline
events = [json.loads(l) for l in open(os.path.join(run, 'timeline.jsonl'))]
start = tz(next(e['ts'] for e in events if e['kind'] == 'start'))
injects = [e for e in events if e['phase'] == 'inject']
heals = [e for e in events if e['phase'] == 'heal']
P('# run', os.path.basename(run), 'workload start', start.isoformat())

# --- node logs
logs = {}
for f in sorted(glob.glob(os.path.join(art, 'node-log-camus*.txt'))):
    n = re.search(r'node-log-(camus\d)', f).group(1)
    logs[n] = [l.rstrip('\n') for l in open(f, errors='replace') if re.match(r'\d{4}-\d\d-\d\d \d\d:\d\d:\d\d', l)]
ipmap = {}
for n, ls in logs.items():
    for l in ls:
        m = re.search(r'\[(\d+\.\d+\.\d+\.\d+):(\d+)/\d+/', l)
        if m: ipmap[m.group(1) + ':' + m.group(2)] = n; break
def node_of(ipport): return ipmap.get(ipport, ipport)

P('\n## Leader kills: failover (first "Voted to become leader" on a survivor after the SIGKILL)')
P('| at | target | down (s) | partition 0 | partition 1 | leader after |')
P('|---|---|---|---|---|---|')
for e in injects:
    if e['kind'] != 'kill': continue
    t0 = tz(e['ts']); tgt = e['target']
    h = next((x for x in heals if x['kind'] == 'kill' and x['target'] == tgt and tz(x['ts']) > t0), None)
    down = (tz(h['ts']) - t0).total_seconds() if h else float('nan')
    first = {}
    who = {}
    for n, ls in logs.items():
        if n == tgt: continue
        for l in ls:
            if 'Voted to become leader' not in l: continue
            t = ts(l)
            if t < t0 or (h and t > tz(h['ts']) + dt.timedelta(seconds=60)): continue
            part = re.search(r'/(\d+)/Candidate', l).group(1)
            d = (t - t0).total_seconds()
            if part not in first or d < first[part]: first[part] = d; who[part] = n
    P(f"| {e['offsetSeconds']/60:.0f}m | {tgt} | {down:.0f} | {first.get('0', float('nan')):.0f} s ({who.get('0','-')}) | {first.get('1', float('nan')):.0f} s ({who.get('1','-')}) | {who.get('1','-')} |")

P('\n## Whole-partition installs')
for n, ls in logs.items():
    for l in ls:
        if 'Snapshot installed on' in l:
            m = re.search(r'(\S+ \S+) .*Snapshot installed on (\S+) at index (\d+) \((\d+) chunk', l)
            P(f"- {m.group(1)}Z leader {n} installed {node_of(m.group(2))} at index {m.group(3)} ({m.group(4)} chunks)")
P('\n### follower side: install dispatch time and Kahuna install lines')
for n, ls in logs.items():
    for l in ls:
        if ('InstallSnapshot took' in l) or ('Kahuna' in l and re.search(r'nstall', l)):
            P(f"- {n}: {l[:400]}")
P('\n### restore tallies')
for n, ls in logs.items():
    for l in ls:
        if 'KeyValueRestorer' in l: P(f"- {n}: {l[:330]}")
P('\n### transfer failures, stalls, receive-limit refusals, breaker, OOM')
for n, ls in logs.items():
    c = defaultdict(int); mx = 0
    for l in ls:
        if 'Snapshot transfer to' in l and 'failed' in l: c['transfer failed'] += 1
        if 'No leader for' in l:
            c['No leader for'] += 1; mx = max(mx, int(re.search(r'No leader for (\d+)ms', l).group(1)))
        if 'exceeded this node' in l: c['receive-limit refusals'] += 1
        if re.search(r'FollowerInstallingSnapshot|refused while installing', l): c['appends refused while installing (log)'] += 1
        if re.search(r'OutOfMemory|FailFast|fail-fast', l): c['OOM/fail-fast lines'] += 1
        if re.search(r'[Bb]reaker', l): c['breaker lines'] += 1
        if re.search(r'Corrupt|quarantin', l): c['corruption/quarantine lines'] += 1
    P(f"- {n}: {dict(c)}; longest leaderless stall {mx/1000:.0f} s")

P('\n## Kommander/Kahuna install counters (node-metrics.csv; last value per node, max in brackets when a restart reset it)')
want = re.compile(r'^(raft_follower_appends_refused|raft_backfill_seeding_peer|raft_compaction_seeded_peer|raft_snapshot_install|raft_snapshot_transfer|kahuna_partition_install|raft_backfill_no_progress)')
vals = defaultdict(lambda: defaultdict(list))
with open(os.path.join(art, 'node-metrics.csv'), encoding='utf-8-sig') as f:
    for row in csv.DictReader(f):
        if want.match(row['metric']):
            try: v = float(row['value'])
            except: continue
            vals[row['metric'] + (('{' + row['labels'] + '}') if row['labels'] else '')][row['node']].append(v)
for m in sorted(vals):
    P(f"- {m}: " + ', '.join(f"{n}={vs[-1]:g} [max {max(vs):g}]" for n, vs in sorted(vals[m].items())))

P('\n## Memory (memory-samples.csv): per node peak, and peak within 8 min after each kill heal')
mem = defaultdict(list)
for row in csv.DictReader(open(os.path.join(run, 'memory-samples.csv'))):
    try: mem[row['container'].split('-')[-1]].append((tz(row['ts']), float(row['mem_mib']), float(row['limit_mib'])))
    except: pass
for n, xs in sorted(mem.items()):
    pk = max(xs, key=lambda x: x[1]); P(f"- {n}: peak {pk[1]:.0f} MiB of {pk[2]:.0f} at {pk[0].isoformat()}; limits seen {sorted(set(x[2] for x in xs))}")
for h in heals:
    if h['kind'] != 'kill': continue
    t0 = tz(h['ts']); xs = [x for x in mem[h['target']] if t0 <= x[0] <= t0 + dt.timedelta(minutes=8)]
    if xs: pk = max(xs, key=lambda x: x[1]); P(f"- after the {h['offsetSeconds']/60:.0f}m kill heal, {h['target']} peaked {pk[1]:.0f} MiB at +{(pk[0]-t0).total_seconds():.0f} s")

P('\n## Unreachable spans outside a fault window (node-health.csv) = self-deaths or stalls')
health = defaultdict(list)
for row in csv.DictReader(open(os.path.join(run, 'node-health.csv'))):
    health[row['node']].append((tz(row['ts']), row['reachable'] == 'true'))
windows = []
for e in injects:
    h = next((x for x in heals if x['kind'] == e['kind'] and x['target'] == e['target'] and tz(x['ts']) >= tz(e['ts'])), None)
    windows.append((e['target'], tz(e['ts']) - dt.timedelta(seconds=5), (tz(h['ts']) if h else tz(e['ts'])) + dt.timedelta(seconds=90)))
for n, xs in sorted(health.items()):
    span = None
    for t, ok in xs + [(xs[-1][0], True)]:
        if not ok and span is None: span = t
        if ok and span is not None:
            if not any(w[0] == n and w[1] <= span <= w[2] for w in windows):
                P(f"- {n} unreachable {span.isoformat()} .. {t.isoformat()} ({(t-span).total_seconds():.0f} s) OUTSIDE any fault window")
            span = None

P('\n## Crash dumps on the host bind mount')
d = glob.glob(os.path.expanduser(f'~/camusdb-caraxes/runs/clusters/{cluster}/dumps/*/*'))
P('- none' if not d else '\n'.join(f"- {x} ({os.path.getsize(x)//1048576} MB)" for x in sorted(d)))

if watch and os.path.exists(watch):
    P('\n## Watcher: container state changes, OOM ledger, dumps')
    for l in open(watch):
        if re.search(r' STATE | DUMP | OOMLOG |watch (start|end)', l): P('- ' + l.strip()[:260])

P('\n## Harness verdicts')
try:
    r = json.load(open(os.path.join(art, 'reconciliation.json')))
    P(f"- reconciliation Passed={r['Passed']} BalanceConserved={r['BalanceConserved']} VersionsMatch={r['VersionsMatch']} VersionExcess={r['VersionExcess']} Indeterminate={r['IndeterminateTxns']} Failures={r['Failures']}")
except Exception as ex: P('- reconciliation.json:', ex)
try:
    s = json.load(open(os.path.join(art, 'summary.json')))
    P(f"- throughput {s['AchievedOpsPerSec']:.0f} ops/s over {s['MeasuredSeconds']:.0f} s; completed {s['Completed']} failed {s['Failed']} indeterminate {s['Indeterminate']}")
except Exception as ex: P('- summary.json:', ex)
a = os.path.join(run, 'analysis.md')
if os.path.exists(a):
    P('\n## analysis.md'); P(open(a).read()[:6000])
