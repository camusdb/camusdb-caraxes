"""Summarize one pgbench-bank point: <dir>/pgbench.txt, <dir>/facts.txt and the sampled latency logs -> summary.json."""
import glob
import json
import os
import re
import sys


def percentile(sorted_values, p):
    if not sorted_values:
        return None
    k = (len(sorted_values) - 1) * p / 100.0
    lo, hi = int(k), min(int(k) + 1, len(sorted_values) - 1)
    return sorted_values[lo] + (sorted_values[hi] - sorted_values[lo]) * (k - lo)


def main(d):
    text = open(os.path.join(d, "pgbench.txt")).read()
    facts = dict(line.split("=", 1) for line in open(os.path.join(d, "facts.txt")).read().splitlines()
                 if "=" in line and not line.startswith("clients="))

    def grab(pattern, cast=float, src=text):
        m = re.search(pattern, src)
        return cast(m.group(1)) if m else None

    duration = grab(r"duration: (\d+) s", int)
    total = grab(r"number of transactions actually processed: (\d+)", int)
    scripts = {}
    for name, body in re.findall(r"SQL script \d+: /scripts/(\w+)\.sql\n(.*?)(?=\nSQL script|\Z)", text, re.S):
        scripts[name] = {
            "transactions": grab(r" - (\d+) transactions", int, body),
            "tps": grab(r"tps = ([\d.]+)\)", float, body),
            "failed": grab(r"number of failed transactions: (\d+)", int, body),
            "retried": grab(r"number of transactions retried: (\d+)", int, body),
            "retries": grab(r"total number of retries: (\d+)", int, body),
            "latency_avg_ms": grab(r"latency average = ([\d.]+) ms", float, body),
            "statements_ms": [(float(ms), sql.strip()) for ms, sql in re.findall(r"^\s+([\d.]+)\s+\d+\s+\d+\s+(.+)$", body, re.M)],
        }

    # Sampled per-transaction log: client_id transaction_no time_us script_no epoch epoch_us [retries].
    lat = {0: [], 1: []}
    for path in glob.glob(os.path.join(d, "latency.*")):
        with open(path) as f:
            for line in f:
                parts = line.split()
                if len(parts) >= 4 and parts[2].isdigit():
                    lat.setdefault(int(parts[3]), []).append(int(parts[2]) / 1000.0)
    for idx, name in ((0, "read"), (1, "transfer")):
        values = sorted(lat.get(idx, []))
        if name in scripts:
            scripts[name]["sampled"] = len(values)
            for p in (50, 90, 95, 99, 99.9):
                scripts[name][f"p{p:g}_ms"] = round(percentile(values, p), 3) if values else None
            scripts[name]["max_sampled_ms"] = round(values[-1], 3) if values else None

    server_cores = int(facts["server_cpu_usec"]) / 1e6 / duration
    client_cores = int(facts["client_cpu_usec"]) / 1e6 / duration
    tps = grab(r"tps = ([\d.]+) \(without initial connection time\)")
    transfers = scripts.get("transfer", {}).get("transactions") or 0
    version_delta = int(facts["version_after"]) - int(facts["version_before"])
    summary = {
        "clients": grab(r"number of clients: (\d+)", int),
        "duration_s": duration,
        "tps": tps,
        "transactions": total,
        "failed": grab(r"number of failed transactions: (\d+)", int),
        "retried": grab(r"number of transactions retried: (\d+)", int),
        "retries": grab(r"total number of retries: (\d+)", int),
        "latency_avg_ms": grab(r"latency average = ([\d.]+) ms"),
        "scripts": scripts,
        "server_cores": round(server_cores, 2),
        "client_cores": round(client_cores, 2),
        "tps_per_server_core": round(tps / server_cores, 1) if tps and server_cores else None,
        "server_memory_peak_mib": round(int(facts["server_memory_peak"]) / 2**20),
        "balance_sum": int(facts["balance_sum"]),
        "balance_conserved": int(facts["balance_sum"]) == 2000 * 1000,
        "row_count": int(facts["row_count"]),
        "version_delta": version_delta,
        # The server's count of committed transfers (two version bumps each) against pgbench's per-script line. That
        # line runs a little short of pgbench's own per-transaction log (by ~0.05% in a full-log check on 2026-11-19,
        # where the log matched SUM(version) exactly), so the server may exceed it slightly but never fall below it.
        "version_delta_matches_transfers": 2 * transfers <= version_delta <= 2 * transfers * 1.002,
        "server_version": facts["server_version"],
    }
    json.dump(summary, open(os.path.join(d, "summary.json"), "w"), indent=2)
    r, t = scripts.get("read", {}), scripts.get("transfer", {})
    print(f"  c={summary['clients']}: {tps:,.0f} tps (reads {r.get('tps', 0):,.0f}/s, transfers {t.get('tps', 0):,.0f}/s), "
          f"failed {summary['failed']}, retried {summary['retried']}, read p50/p99 {r.get('p50_ms')}/{r.get('p99_ms')} ms, "
          f"transfer p50/p99 {t.get('p50_ms')}/{t.get('p99_ms')} ms, server {server_cores:.2f} cores, client {client_cores:.2f} cores, "
          f"{summary['tps_per_server_core']} tps/core, sum(balance) {'OK' if summary['balance_conserved'] else 'BROKEN'}, "
          f"server transfers {version_delta // 2:,} vs counted {transfers:,} {"OK" if summary["version_delta_matches_transfers"] else "MISMATCH"}")


if __name__ == "__main__":
    main(sys.argv[1])
