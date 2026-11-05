#!/usr/bin/env python3
"""Compare two run dirs of the same body: throughput, latency, leader CPU per op (user+system), GC collections per generation,
GC pause, allocation and committed heap over the measured window, with counter resets handled.
  usage: python3 tools/p3c/compare-arms.py <controlDir> <headDir> <controlLeader> <headLeader>
The leader of each arm is the node with =1 on the line after "waiting up to .. for partition leadership" in its driver log."""
import csv, json, sys, datetime as dt
def load(run, leader):
    s = json.load(open(f"{run}/artifacts/run/summary.json"))
    meta = json.load(open(f"{run}/artifacts/run/run-meta.json"))
    iso = meta["measureStartUtc"].rstrip("Z"); iso = iso[:26]
    start_ms = int(dt.datetime.fromisoformat(iso).replace(tzinfo=dt.timezone.utc).timestamp()*1000)
    end_ms = start_ms + meta["measureSeconds"]*1000
    series = {}
    with open(f"{run}/artifacts/run/node-metrics.csv") as f:
        r = csv.reader(f); next(r, None)
        for row in r:
            if len(row) < 5: continue
            ts, node, metric, labels, val = row[0], row[1], row[2], row[3], row[4]
            try: ts = int(ts); val = float(val)
            except ValueError: continue
            if ts < start_ms - 6000 or ts > end_ms + 6000: continue
            series.setdefault((node, metric, labels), []).append((ts, val))
    def delta(node, metric, label_sub=""):
        tot = 0.0
        for (n, m, l), pts in series.items():
            if n != node or m != metric or label_sub not in l: continue
            pts.sort(); prev = None; acc = 0.0
            for ts, v in pts:
                if prev is not None: acc += (v - prev) if v >= prev else v   # counter reset aware
                prev = v
            tot += acc
        return tot
    def stat(node, metric, label_sub=""):
        vals = [v for (n, m, l), pts in series.items() if n == node and m == metric and label_sub in l for _, v in pts]
        return (sum(vals)/len(vals), max(vals)) if vals else (float("nan"), float("nan"))
    ops = s["AchievedOpsPerSec"]; secs = s["MeasuredSeconds"]
    cpu = delta(leader, "dotnet_process_cpu_time_seconds_total")
    out = {
        "ops/s": ops, "completed": s["Completed"], "failed": s["Failed"], "conflicts": s["Conflicts"],
        "retries/write": s["RetriesPerWriteTxn"],
        "read p50": s["ReadLatency"]["P50"], "read p95": s["ReadLatency"]["P95"], "read p99": s["ReadLatency"]["P99"],
        "write p50": s["WriteLatency"]["P50"], "write p95": s["WriteLatency"]["P95"], "write p99": s["WriteLatency"]["P99"],
        "leader": leader, "leader cpu s": cpu, "leader cpu ms/op": cpu*1000/(ops*secs),
        "gen0": delta(leader, "dotnet_gc_collections_total", "gen0"), "gen1": delta(leader, "dotnet_gc_collections_total", "gen1"),
        "gen2": delta(leader, "dotnet_gc_collections_total", "gen2"),
        "gc pause s": delta(leader, "dotnet_gc_pause_time_seconds_total"),
        "alloc GB": delta(leader, "dotnet_gc_heap_total_allocated_bytes_total")/1e9,
        "committed MiB mean": stat(leader, "dotnet_gc_last_collection_memory_committed_size_bytes")[0]/1048576,
        "committed MiB max": stat(leader, "dotnet_gc_last_collection_memory_committed_size_bytes")[1]/1048576,
    }
    for n in ("camus1","camus2","camus3"):
        out[f"cpu s {n}"] = delta(n, "dotnet_process_cpu_time_seconds_total")
    return out
a = load(sys.argv[1], sys.argv[3]); b = load(sys.argv[2], sys.argv[4])
print(f"{'metric':22s} {'control':>14s} {'head':>14s} {'head/ctl':>9s}")
for k in a:
    va, vb = a[k], b[k]
    if isinstance(va, str): print(f"{k:22s} {va:>14s} {vb:>14s}"); continue
    ratio = (vb/va) if va else float("nan")
    print(f"{k:22s} {va:14.3f} {vb:14.3f} {ratio:9.3f}")
