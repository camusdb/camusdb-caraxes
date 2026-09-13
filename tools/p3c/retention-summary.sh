#!/usr/bin/env bash
# Durable-2PC retention summary for one run (Kahuna feature caf52e10): per-minute resident records / receipts /
# estimated bytes, early reclaims, managed heap (gen2 + LOH + POH from dotnet_gc_last_collection_heap_size_bytes),
# the WAL zombie signature (a node whose raft_wal_batches_total stops while the leader's continues), OOM lines, and
# cgroup memory minus the tmpfs data footprint when the tmpfs sampler csv is given.
# Usage: tools/p3c/retention-summary.sh <run-dir> [tmpfs-footprint.csv]
set -u
python3 - "$1" "${2:-}" <<'PY'
import sys,csv,collections,glob,os,json
run,fp=sys.argv[1],sys.argv[2]
art=os.path.join(run,'artifacts/run')
meta=json.load(open(os.path.join(art,'run-meta.json'))); s=json.load(open(os.path.join(art,'summary.json')))
print(f"{os.path.basename(run)}: {s['AchievedOpsPerSec']:,.0f} ops/s over {s['MeasuredSeconds']}s, failed {s.get('Failed')}")
want=('kahuna_durable_tx_resident_records','kahuna_durable_tx_resident_receipts','kahuna_durable_tx_resident_record_bytes',
      'kahuna_durable_tx_resident_receipt_bytes','kahuna_durable_tx_gc_records_reclaimed_early_total','kahuna_durable_tx_gc_heap_pressure_sweeps_total',
      'dotnet_gc_last_collection_heap_size_bytes','dotnet_gc_last_collection_memory_committed_size_bytes','raft_wal_batches_total','raft_wal_queue_depth')
# Values are summed across label rows at the same timestamp (partition labels etc.), then the last timestamp of
# each minute is kept. The GC heap-size gauge is kept per generation label instead of summed.
bytime=collections.defaultdict(float); t0=None; heapgen=collections.defaultdict(dict)
for x in csv.DictReader(open(os.path.join(art,'node-metrics.csv'),encoding='utf-8-sig')):
    m=x['metric']
    if m not in want: continue
    t=int(x['unix_ms']); t0=t0 or t
    if m=='dotnet_gc_last_collection_heap_size_bytes':
        lab=x['labels']; v=float(x['value'])
        g=next((p.split('=')[1] for p in lab.split(';') if 'generation' in p),'?')
        heapgen[(x['node'],g)][(t-t0)//60000]=v
        continue
    bytime[(x['node'],m,t)]+=float(x['value'])
ser=collections.defaultdict(dict); lastts={}
for (node,m,t),v in sorted(bytime.items(), key=lambda kv: kv[0][2]):
    mi=(t-t0)//60000
    ser[(node,m)][mi]=v
nodes=sorted({k[0] for k in ser})
def row(node,m,scale=1,fmt="{:.0f}"):
    sd=ser.get((node,m),{})
    return ' '.join(fmt.format(sd[i]/scale) for i in sorted(sd)) if sd else '-'
for n in nodes:
    print(f"== {n}")
    print("  resident records   ", row(n,'kahuna_durable_tx_resident_records',1e3,"{:.0f}k"))
    print("  resident receipts  ", row(n,'kahuna_durable_tx_resident_receipts',1e3,"{:.0f}k"))
    print("  record+receipt MB  ", ' '.join(f"{(ser.get((n,'kahuna_durable_tx_resident_record_bytes'),{}).get(i,0)+ser.get((n,'kahuna_durable_tx_resident_receipt_bytes'),{}).get(i,0))/1e6:.0f}" for i in sorted(ser.get((n,'kahuna_durable_tx_resident_records'),{}))) )
    print("  reclaimed early    ", row(n,'kahuna_durable_tx_gc_records_reclaimed_early_total',1e3,"{:.0f}k"), "| pressure sweeps", row(n,'kahuna_durable_tx_gc_heap_pressure_sweeps_total'))
    gens=sorted({g for (nn,g) in heapgen if nn==n})
    for g in gens:
        sd=heapgen[(n,g)]; print(f"  heap {g:>6} MB     ", ' '.join(f"{sd[i]/1e6:.0f}" for i in sorted(sd)))
    print("  committed MB       ", row(n,'dotnet_gc_last_collection_memory_committed_size_bytes',1e6))
    b=ser.get((n,'raft_wal_batches_total'),{}); ks=sorted(b)
    print("  wal batches/min    ", ' '.join(f"{b[ks[i]]-b[ks[i-1]]:.0f}" for i in range(1,len(ks))), "| queue depth", row(n,'raft_wal_queue_depth'))
for f in sorted(glob.glob(os.path.join(art,'node-log-camus*.txt'))):
    t=open(f,errors='ignore').read()
    print(os.path.basename(f), "OutOfMemory lines:", t.count('OutOfMemory'), "| fatal/FailFast:", t.count('FailFast')+t.count('fatalFault'), "| WAL saturated:", t.count('WAL saturated'), "| over budget warnings:", t.lower().count('over budget'))
if fp and os.path.exists(fp):
    rows=list(csv.DictReader(open(fp)))
    for c in sorted({r['container'] for r in rows}):
        rs=[r for r in rows if r['container']==c]
        print(c, "cgroup-minus-data MiB first/peak/last:", int(rs[0]['mem_current_mib'])-int(rs[0]['data_mib']), max(int(r['mem_current_mib'])-int(r['data_mib']) for r in rs), int(rs[-1]['mem_current_mib'])-int(rs[-1]['data_mib']), "| data MiB last", rs[-1]['data_mib'], "| host avail MiB min", min(int(r['host_mem_available_mib']) for r in rs))
PY
