#!/usr/bin/env python3
"""Per-stage point-read attribution from a run's node-metrics.csv.

Reads the counters CamusDB publishes when a node runs with CAMUS_QUERY_STAGE_PROFILE=1
(camus_query_profile_{queries,time_milliseconds,calls}_total, by path/stage/part) plus
camus_request_{count,duration} for the query operation, takes each counter's delta over the measured
window (run-meta.json: measureStartUtc + measureSeconds, nearest 5-s samples inside it), sums the
nodes, and prints the per-query mean of every stage split into its CamusDB and Kahuna parts.

    tools/p3c/query-profile.py runs/scenarios/<run>            # all nodes
    tools/p3c/query-profile.py runs/scenarios/<run> camus1     # one node
    tools/p3c/query-profile.py runs/scenarios/<run> all 360 600  # a sub-window, seconds into the measured window

Nested stages are printed under their parent; a parent's "self" row is its time minus its children,
and the "unattributed" row is the request time minus the top-level stages.
"""
import csv
import datetime as dt
import json
import os
import sys
from collections import defaultdict

run = sys.argv[1]
only = sys.argv[2] if len(sys.argv) > 2 and sys.argv[2] != "all" else None
art = os.path.join(run, "artifacts", "run")
meta = json.load(open(os.path.join(art, "run-meta.json")))
start = dt.datetime.fromisoformat(meta["measureStartUtc"].replace("Z", "+00:00")[:26] + "+00:00")
t0 = start.timestamp() * 1000
t1 = t0 + meta["measureSeconds"] * 1000
if len(sys.argv) > 4:
    t0, t1 = t0 + float(sys.argv[3]) * 1000, t0 + float(sys.argv[4]) * 1000

WANTED = (
    "camus_query_profile_queries_total",
    "camus_query_profile_time_milliseconds_total",
    "camus_query_profile_calls_total",
    "camus_request_count_total",
    "camus_request_duration_milliseconds_sum",
    "camus_request_duration_milliseconds_count",
)

# (node, metric, labels) -> [(ts, value)]
series = defaultdict(list)
with open(os.path.join(art, "node-metrics.csv")) as f:
    for row in csv.reader(f):
        if len(row) < 5 or row[2] not in WANTED:
            continue
        if only and row[1] != only:
            continue
        ts = int(row[0])
        labels = dict(kv.split("=", 1) for kv in row[3].split(";") if "=" in kv)
        labels.pop("otel_scope_name", None)
        labels.pop("otel_scope_version", None)
        series[(row[1], row[2], tuple(sorted(labels.items())))].append((ts, float(row[4])))


def delta(points):
    inside = [p for p in points if t0 <= p[0] <= t1]
    before = [p for p in points if p[0] < t0]
    if not inside:
        return 0.0
    # A series first seen inside the window started at zero.
    base = before[-1][1] if before else 0.0
    return inside[-1][1] - base


window_s = None
agg = defaultdict(float)
for (node, metric, labels), points in series.items():
    points.sort()
    agg[(metric, labels)] += delta(points)
    inside = [p[0] for p in points if t0 <= p[0] <= t1]
    if inside:
        span = (inside[-1] - t0) / 1000
        window_s = span if window_s is None else max(window_s, span)


def get(metric, **want):
    total = 0.0
    for (m, labels), v in agg.items():
        if m != metric:
            continue
        d = dict(labels)
        if all(d.get(k) == val for k, val in want.items()):
            total += v
    return total


print(f"run {os.path.basename(run.rstrip('/'))}  nodes {only or 'all'}  window {window_s:.0f} s from "
      f"{dt.datetime.fromtimestamp(t0 / 1000, dt.timezone.utc).isoformat(timespec='seconds')}")
q_ok = get("camus_request_count_total", operation="query", outcome="ok")
q_all = get("camus_request_duration_milliseconds_count", operation="query")
q_sum = get("camus_request_duration_milliseconds_sum", operation="query")
if q_all:
    print(f"camus_request_duration{{operation=query}}: {q_all:,.0f} queries ({q_ok:,.0f} ok), mean {q_sum / q_all:.3f} ms, {q_all / window_s:,.0f}/s")

CHILDREN = {
    "prepare": ["parse", "authorize", "db_open"],
    "fetch": ["index_lookup", "row_get", "decode"],
}
TOP = ["admit", "chain_wait", "slot_wait", "resolve", "begin", "prepare", "fetch", "write", "commit", "complete"]
PARTS = ["kahuna_start", "kahuna_kv", "kahuna_commit", "kahuna_rollback"]

for path in ("autocommit", "txn"):
    n = get("camus_query_profile_queries_total", path=path)
    if not n:
        continue

    def ms(stage, part="total"):
        return get("camus_query_profile_time_milliseconds_total", path=path, stage=stage, part=part) / n

    def calls(stage, part="total"):
        return get("camus_query_profile_calls_total", path=path, stage=stage, part=part) / n

    def row(name, total, indent, stage=None):
        kahuna = {p: ms(stage, p) for p in PARTS} if stage else {}
        k = sum(kahuna.values())
        kc = sum(calls(stage, p) for p in PARTS) if stage else 0
        detail = "  ".join(f"{p[7:]} {v * 1000:.0f} us x{calls(stage, p):.2f}" for p, v in kahuna.items() if v > 0)
        print(f"  {'  ' * indent}{name:<{22 - 2 * indent}} {total * 1000:8.0f} us   camus {max(total - k, 0) * 1000:7.0f}   kahuna {k * 1000:7.0f} ({kc:.2f} calls)  {detail}")

    request = ms("request")
    print(f"\npath={path}: {n:,.0f} profiled queries ({n / window_s:,.0f}/s)  request mean {request * 1000:.0f} us"
          f"  (+ admit/chain/slot before it)")
    print(f"  {'stage':<22} {'mean':>8}      {'CamusDB':>11}   {'Kahuna':>12}")
    top_sum = 0.0
    for stage in TOP:
        t = ms(stage)
        if t == 0 and not CHILDREN.get(stage):
            continue
        row(stage, t, 0, stage)
        if stage not in ("admit", "chain_wait", "slot_wait"):
            top_sum += t
        kids = CHILDREN.get(stage, [])
        if kids:
            child_sum = 0.0
            for kid in kids:
                kt = ms(kid)
                child_sum += kt
                row(kid, kt, 1, kid)
            print(f"    {'(self)':<20} {(t - child_sum) * 1000:8.0f} us")
    print(f"  {'unattributed':<22} {(request - top_sum) * 1000:8.0f} us   (request minus top-level stages)")
