#!/usr/bin/env python3
"""Read a run's node-metrics.csv over its measured window.

  window-metrics.py delta <runDir> <regex> [node]   counter deltas over the window: per node/metric/labels, delta and delta/s;
                                                    a _sum/_count pair is also reported as a mean (sum delta / count delta)
  window-metrics.py gauge <runDir> <regex> [node]   gauge statistics over the window: mean, min, max, last
  window-metrics.py stages <runDir> [node]          the Kommander raft.round.stage_ms histogram (KOMMANDER_ROUND_STAGES=1):
                                                    mean ms and samples/s per node and stage, leader chain first

The window is [measureStartUtc, measureStartUtc + measureSeconds] from run-meta.json; node-metrics.csv is the 5-second
series unix_ms,node,metric,labels,value. Deltas take the last sample at or before each window edge.
"""
import csv, json, os, re, sys
from collections import defaultdict
from datetime import datetime, timezone

LEADER_CHAIN = ["leader.queue", "leader.propose", "leader.wal", "leader.wal_completion", "leader.fanout",
                "leader.replication", "leader.resume", "leader.round"]
FOLLOWER_CHAIN = ["follower.queue", "follower.append", "follower.wal", "follower.wal_completion", "follower.ack",
                  "leader.ack_queue", "leader.ack", "transport.dispatch", "leader.commit_wal"]


def window(run_dir):
    art = os.path.join(run_dir, "artifacts", "run")
    meta = json.load(open(os.path.join(art, "run-meta.json")))
    start = datetime.fromisoformat(meta["measureStartUtc"].replace("Z", "+00:00")).timestamp() * 1000
    return art, start, start + meta["measureSeconds"] * 1000


def load(art, pattern, node=None):
    rx = re.compile(pattern)
    series = defaultdict(list)  # (node, metric, labels) -> [(t, v)]
    with open(os.path.join(art, "node-metrics.csv"), encoding="utf-8-sig", newline="") as f:
        for row in csv.reader(f):
            if len(row) < 5 or row[0] == "unix_ms":
                continue
            t, n, m, labels, v = row[0], row[1], row[2], row[3], row[4]
            if node and n != node:
                continue
            if not rx.search(m):
                continue
            try:
                series[(n, m, labels)].append((int(t), float(v)))
            except ValueError:
                continue
    for k in series:
        series[k].sort()
    return series


def at_or_before(points, t):
    best = None
    for pt, pv in points:
        if pt <= t:
            best = pv
        else:
            break
    return best


def deltas(series, start, end):
    out = {}
    for k, pts in series.items():
        a, b = at_or_before(pts, start), at_or_before(pts, end)
        if a is None or b is None:
            continue
        out[k] = b - a
    return out


def cmd_delta(run_dir, pattern, node):
    art, start, end = window(run_dir)
    secs = (end - start) / 1000
    d = deltas(load(art, pattern, node), start, end)
    print(f"window {secs:.0f} s")
    for (n, m, labels), v in sorted(d.items()):
        print(f"{n:7s} {m:60s} {labels:50s} {v:14.1f} {v / secs:12.2f}/s")
    # means for _sum/_count pairs
    for (n, m, labels), s in sorted(d.items()):
        if m.endswith("_sum"):
            c = d.get((n, m[:-4] + "_count", labels))
            if c:
                print(f"{n:7s} {m[:-4]:60s} {labels:50s} mean {s / c:10.4f}  n/s {c / secs:10.1f}")


def cmd_gauge(run_dir, pattern, node):
    art, start, end = window(run_dir)
    for (n, m, labels), pts in sorted(load(art, pattern, node).items()):
        vals = [v for t, v in pts if start <= t <= end]
        if not vals:
            continue
        print(f"{n:7s} {m:60s} {labels:40s} mean {sum(vals) / len(vals):12.2f} min {min(vals):12.2f} max {max(vals):12.2f} last {vals[-1]:12.2f} (n={len(vals)})")


def cmd_stages(run_dir, node):
    art, start, end = window(run_dir)
    secs = (end - start) / 1000
    d = deltas(load(art, r"raft_round_stage", node), start, end)
    if not d:
        print("no raft_round_stage_* series in the window (KOMMANDER_ROUND_STAGES not set, or the meter is not exported)")
        return
    by_node = defaultdict(dict)
    for (n, m, labels), s in d.items():
        if not m.endswith("_sum"):
            continue
        c = d.get((n, m[:-4] + "_count", labels))
        stage = re.search(r"stage=([^;,]+)", labels)
        stage = stage.group(1) if stage else labels
        if c:
            by_node[n][stage] = (s / c, c / secs)
    for n in sorted(by_node):
        print(f"\n{n}  (window {secs:.0f} s)")
        print(f"  {'stage':28s} {'mean ms':>10s} {'samples/s':>10s}")
        rows = by_node[n]
        order = [s for s in LEADER_CHAIN + FOLLOWER_CHAIN if s in rows] + sorted(s for s in rows if s not in LEADER_CHAIN + FOLLOWER_CHAIN)
        for s in order:
            mean, rate = rows[s]
            print(f"  {s:28s} {mean:10.3f} {rate:10.1f}")


if __name__ == "__main__":
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(1)
    verb, run = sys.argv[1], sys.argv[2]
    if verb == "delta":
        cmd_delta(run, sys.argv[3], sys.argv[4] if len(sys.argv) > 4 else None)
    elif verb == "gauge":
        cmd_gauge(run, sys.argv[3], sys.argv[4] if len(sys.argv) > 4 else None)
    elif verb == "stages":
        cmd_stages(run, sys.argv[3] if len(sys.argv) > 3 else None)
    else:
        print(__doc__)
        sys.exit(1)
