#!/usr/bin/env python3
"""What does an install cost with many live branches, and does each branch still read its own fork?

The export walks each key's revisions and answers every registered hold, so its size grows with the distinct revisions
those holds pin, and the hold renewer renews one hold at a time. This forks --branches branches under load, spread over
--fork-span seconds so they pin different revisions, then has a replica move onto a node that never held the partition.

Each fork gets its own row in the marks table, updated in the parent straight after that fork. So branch i of N must
read rows 0..i-1 at 2 and rows i..N-1 at 1: a staircase that proves each branch reads its own fork, not merely some
fork. A sample of branches also has its bank accounts compared with a baseline taken when the branch forked.

Prints and writes verdict.json: REPRODUCED (a branch read wrong on the new replica, with the same read clean before),
REFUSED (a branch read declined, which is safe but unavailable), NOT_REPRODUCED, ANOMALY_NOT_ATTRIBUTED, or VACUOUS (no
install, or leadership could not reach the new replica). It also records fork latency, renewals, export size and time,
memory and which branches lost protection. Those numbers are the point of the test, so a clean verdict is not the result.
"""
import argparse
import hashlib
import importlib.util
import json
import os
import re
import statistics
import subprocess
import sys
import threading
import time

here = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("move_repro", os.path.join(here, "move-repro.py"))
move_repro = importlib.util.module_from_spec(spec)
spec.loader.exec_module(move_repro)

PARENT = move_repro.PARENT
TRANSFER = re.compile(r"/1/Leader\] Starting a snapshot transfer to (\S+) at index (\d+)")
INSTALLED = re.compile(r"/1/Leader\] Snapshot installed on (\S+) at index (\d+) \((\d+) chunk")
TABLE = "hold_marks"


def percentile(values, q):
    ordered = sorted(values)
    return ordered[min(len(ordered) - 1, int(q * len(ordered)))] if ordered else None


class HoldsRun(move_repro.MoveRun):
    def branch(self, i):
        return f"hold{i:04d}"

    def seed_table(self, gateway):
        n = self.args.branches
        self.sql(gateway, PARENT, f"CREATE TABLE {TABLE} (id INT64 PRIMARY KEY, v INT64 NOT NULL)")
        for start in range(0, n, 200):
            values = ", ".join(f"({i}, 1)" for i in range(start, min(n, start + 200)))
            self.sql(gateway, PARENT, f"INSERT INTO {TABLE} (id, v) VALUES {values}")

    def digest_accounts(self, gateway, branch):
        rows = self.sql(gateway, branch, "SELECT id, balance, version FROM workload_accounts")["rows"]
        text = "\n".join(sorted(f"{r[0]}:{r[1]}:{r[2]}" for r in rows))
        return len(rows), hashlib.sha256(text.encode()).hexdigest()

    def fork_all(self, gateway):
        """Forks every branch on schedule, updating that fork's mark in the parent right after it."""
        a = self.args
        gap = a.fork_span / max(1, a.branches)
        started = time.time()
        step = max(1, a.branches // a.account_samples)
        self.samples = sorted({0, a.branches - 1, *range(0, a.branches, step)})
        latencies = []
        self.fork_errors = []
        for i in range(a.branches):
            due = started + i * gap
            time.sleep(max(0, due - time.time()))
            t = time.time()
            try:
                self.sql(gateway, PARENT, f"CREATE DATABASE {self.branch(i)} BRANCH FROM {PARENT}", budget=a.fork_budget)
                latencies.append(round(time.time() - t, 3))
                self.sql(gateway, PARENT, f"UPDATE {TABLE} SET v = 2 WHERE id = {i}")
            except RuntimeError as e:
                self.fork_errors.append({"branch": i, "seconds": round(time.time() - t, 1), "error": str(e)[:400]})
                self.log("fork-failed", branch=i, error=str(e)[:400])
                if len(self.fork_errors) >= a.max_fork_errors:
                    break
                continue
            if i in self.samples:
                self.baselines[i] = self.digest_accounts(gateway, self.branch(i))
            if (i + 1) % max(1, a.branches // 20) == 0:
                self.log("fork-progress", forked=len(latencies), last_seconds=latencies[-1],
                         p50=percentile(latencies[-100:], 0.5), p99=percentile(latencies[-100:], 0.99))
        self.forked = len(latencies)
        self.fork_latency = {"count": len(latencies), "p50": percentile(latencies, 0.5), "p95": percentile(latencies, 0.95),
                             "p99": percentile(latencies, 0.99), "max": max(latencies) if latencies else None,
                             "first_decile_mean": round(statistics.mean(latencies[:max(1, len(latencies) // 10)]), 3) if latencies else None,
                             "last_decile_mean": round(statistics.mean(latencies[-max(1, len(latencies) // 10):]), 3) if latencies else None,
                             "wall_seconds": round(time.time() - started, 1)}
        self.log("forks-done", **self.fork_latency, errors=len(self.fork_errors))

    def check_stairs(self, gateway, indices):
        """Reads each branch's marks and counts the rows that are not what its fork must see."""
        wrong, errors = [], []
        started = time.time()
        for i in indices:
            try:
                rows = self.sql(gateway, self.branch(i), f"SELECT id, v FROM {TABLE}", budget=self.args.read_budget)["rows"]
            except RuntimeError as e:
                errors.append({"branch": i, "error": str(e)[:300]})
                continue
            seen = {int(r[0]): int(r[1]) for r in rows}
            bad = sum(1 for k in range(self.args.branches) if seen.get(k) != (2 if k < i else 1))
            if bad:
                wrong.append({"branch": i, "bad_rows": bad})
        return {"read": len(indices), "wrong": len(wrong), "errors": len(errors), "sample_wrong": wrong[:5], "sample_errors": errors[:3],
                "seconds": round(time.time() - started, 1)}

    def check_accounts(self, gateway):
        wrong, errors = [], []
        for i in self.baselines:
            try:
                got = self.digest_accounts(gateway, self.branch(i))
                if got != self.baselines[i]:
                    wrong.append({"branch": i, "rows": got[0], "baseline_rows": self.baselines[i][0]})
            except RuntimeError as e:
                errors.append({"branch": i, "error": str(e)[:300]})
        return {"read": len(self.baselines), "wrong": len(wrong), "errors": len(errors), "sample_wrong": wrong[:5], "sample_errors": errors[:3]}

    def read_all(self, label, expected_leader, gateway):
        before = self.leader()
        stairs = self.check_stairs(gateway, self.live_indices)
        accounts = self.check_accounts(gateway)
        after = self.leader()
        entry = {"label": label, "gateway": gateway, "leader_as_expected": before == after == expected_leader, "stairs": stairs,
                 "accounts": accounts, "floor": self.floor_metrics(gateway)}
        self.log("read-all", **entry)
        self.reads.append(entry)
        return entry

    @staticmethod
    def wrong_rows(r):
        return r["stairs"]["wrong"] + r["accounts"]["wrong"]

    @staticmethod
    def refused(r):
        return r["stairs"]["errors"] + r["accounts"]["errors"]

    # ---- Resource sampling ---------------------------------------------------------------------------------------
    def rss_mb(self, node):
        out = subprocess.run(["docker", "stats", "--no-stream", "--format", "{{.MemUsage}}", f"{self.args.cluster}-{node}"],
                             capture_output=True, text=True).stdout
        m = re.match(r"([\d.]+)(MiB|GiB)", out.strip())
        return None if not m else round(float(m.group(1)) * (1024 if m.group(2) == "GiB" else 1))

    def sample_rss(self, stop, node, out):
        while not stop.wait(2):
            value = self.rss_mb(node)
            if value is not None:
                out.append(value)

    def hold_problems(self):
        """Counts, in every node's log, the hold warnings an operator would look for."""
        patterns = {"renew_transient": "Renew of snapshot hold", "renew_failed": "Failed to renew snapshot hold",
                    "sweep_failed": "Snapshot-hold renewer sweep failed", "protection_lost": "lost snapshot protection"}
        found = {k: 0 for k in patterns}
        for node in self.nodes:
            out = subprocess.run(["docker", "logs", f"{self.args.cluster}-{node}"], capture_output=True, text=True, errors="replace")
            text = out.stdout + out.stderr
            for key, pattern in patterns.items():
                found[key] += text.count(pattern)
        return found

    def install_size(self, node):
        """The bytes and milliseconds the installed node recorded for its install, from /metrics."""
        try:
            text = self.http(node, "/metrics", timeout=15)
        except OSError:
            return {}
        found = {}
        for line in text.splitlines():
            if line.startswith("raft_snapshot") and not line.startswith("#"):
                if any(s in line for s in ("_sum", "_count", "_total", "bytes")) and "_bucket" not in line:
                    key, _, value = line.rpartition(" ")
                    found[re.sub(r"otel_scope_[a-z]+=\"[^\"]*\",?", "", key)] = float(value)
        return found

    # ---- The run -------------------------------------------------------------------------------------------------
    def run(self):
        a = self.args
        self.d0 = None
        self.baselines = {}
        self.reads = []
        self.log("waiting-for-nemesis", timeline=a.timeline, branches=a.branches)
        self.wait_start()
        self.wait_offset(a.marks_at)
        gateway = "camus1"
        self.seed_table(gateway)
        self.wait_offset(a.fork_at)
        forks_at = time.time() - self.t0
        self.fork_all(gateway)
        if not self.forked:
            self.finish({"verdict": "VACUOUS", "reason": "no fork succeeded", "fork_errors": self.fork_errors})
            return
        bad = {e["branch"] for e in self.fork_errors}
        self.live_indices = [i for i in range(a.branches) if i not in bad]
        wait = max(0, a.fork_at + a.fork_span + a.compact_wait - (time.time() - self.t0))
        self.log("waiting-for-compaction", seconds=round(wait), forks_at=round(forks_at))
        time.sleep(wait)

        before = self.hosts()
        leader, meta = self.leader(), self.meta_leader()
        if a.separate_meta and leader == meta:  # keeps a data leader that also leads the meta partition out of the comparison
            other = next((n for n in (self.node_at(e) for e in before) if n and n != meta), None)
            if other and self.transfer(other):
                leader = other
            self.log("separated-meta", leader=leader, meta=meta)
        d1 = self.read_all("D1-before-move", leader, leader)
        rss_leader_before = self.rss_mb(leader)
        candidates = [n for n in (self.node_at(e) for e in before) if n and n != leader]
        victim = next((n for n in candidates if n != meta), candidates[0])
        outsiders = [n for n in self.nodes if self.endpoint(n) not in before]
        counts = {n: self.install_count(n) for n in outsiders}
        self.log("before-move", hosts=before, leader=leader, meta=meta, victim=victim, outsiders=outsiders)

        rss_during = []
        stop = threading.Event()
        sampler = threading.Thread(target=self.sample_rss, args=(stop, leader, rss_during), daemon=True)
        sampler.start()
        started = time.time()
        deadline = started + a.move_wait
        answer = {}
        while time.time() < deadline:
            try:
                answer = json.loads(self.leave(victim))
            except ValueError as e:
                answer = {"retryable": True, "reason": str(e)}
            self.log("leave-answered", victim=victim, answer=answer, seconds=round(time.time() - started, 1))
            if answer.get("left") or not answer.get("retryable"):
                break
            time.sleep(5)
        if not answer.get("left"):
            # A move that cannot finish is a result: say what the transfers did and whether the branches still read right.
            stop.set()
            sampler.join(timeout=10)
            out = subprocess.run(["docker", "logs", f"{a.cluster}-{leader}"], capture_output=True, text=True, errors="replace")
            lines = (out.stdout + out.stderr).splitlines()
            self.finish({"verdict": "MOVE_BLOCKED", "branches_requested": a.branches, "branches_forked": self.forked,
                         "fork_latency": self.fork_latency, "fork_errors": self.fork_errors[:10], "victim": victim,
                         "leave": answer, "move_seconds": round(time.time() - started, 1),
                         "transfer_starts": sum(1 for l in lines if "Starting a snapshot transfer" in l),
                         "transfer_failures": [l[:260] for l in lines if "Snapshot transfer to" in l and " failed (" in l][:10],
                         "rss_leader_before_mb": rss_leader_before, "rss_leader_during_max_mb": max(rss_during) if rss_during else None,
                         "d1": d1, "d_blocked": self.read_all("D-after-blocked-move", self.leader(), self.leader()),
                         "hold_problems": self.hold_problems()})
            return
        self.nodes = [n for n in self.nodes if n != victim]
        subprocess.run(["docker", "stop", f"{a.cluster}-{victim}"], capture_output=True, text=True)

        new_replica, after = None, before
        deadline = max(deadline, time.time() + 60)
        while time.time() < deadline:
            after = self.hosts() or after
            fresh = [e for e in after if e not in before]
            if fresh and all(r == "Voter" for r in after.values()) and len(after) == len(before):
                new_replica = self.node_at(fresh[0])
                break
            time.sleep(5)
        stop.set()
        sampler.join(timeout=10)
        verdict = {"branches_requested": a.branches, "branches_forked": self.forked, "fork_latency": self.fork_latency,
                   "fork_errors": self.fork_errors[:10], "victim": victim, "new_replica": new_replica,
                   "move_seconds": round(time.time() - started, 1), "rss_leader_before_mb": rss_leader_before,
                   "rss_leader_during_max_mb": max(rss_during) if rss_during else None, "d1": d1}
        if not new_replica:
            verdict["verdict"] = "VACUOUS"
            self.finish(verdict)
            return

        time.sleep(a.settle)
        out = subprocess.run(["docker", "logs", f"{a.cluster}-{leader}"], capture_output=True, text=True, errors="replace")
        target = self.endpoint(new_replica)
        for line in (out.stdout + out.stderr).splitlines():
            m = TRANSFER.search(line)
            if m and m.group(1) == target and "export_started" not in verdict:
                verdict["export_started"] = line[:19]
            m = INSTALLED.search(line)
            if m and m.group(1) == target:
                verdict["export_finished"], verdict["chunks"] = line[:19], int(m.group(3))
        verdict["installed"] = (self.install_count(new_replica) or 0) > counts.get(new_replica, 0)
        verdict["install_metrics"] = self.install_size(new_replica)

        moved = self.transfer(new_replica)
        d2 = self.read_all("D2-new-replica-leads", new_replica, new_replica) if moved else None
        self.restart_all()
        time.sleep(a.settle)
        hosts_end = self.hosts() or {}
        verdict["hosts_after_restart"] = hosts_end
        d3 = None
        if self.endpoint(new_replica) in hosts_end and self.transfer(new_replica):
            d3 = self.read_all("D3-new-replica-leads-after-restart", new_replica, new_replica)
        verdict.update(d2=d2, d3=d3, hold_problems=self.hold_problems(), floor={n: self.floor_metrics(n) for n in self.nodes})

        tests = [r for r in (d2, d3) if r]
        if self.wrong_rows(d1) or self.refused(d1):
            verdict["verdict"] = "ANOMALY_NOT_ATTRIBUTED"
        elif any(self.wrong_rows(r) for r in tests):
            verdict["verdict"] = "REPRODUCED"
        elif not verdict["installed"] or not d2 or not all(r["leader_as_expected"] for r in tests):
            verdict["verdict"] = "VACUOUS"
        elif any(self.refused(r) for r in tests):
            verdict["verdict"] = "REFUSED"
        else:
            verdict["verdict"] = "NOT_REPRODUCED"
        self.finish(verdict)

    def finish(self, verdict):
        with open(os.path.join(self.dir, "verdict.json"), "w") as f:
            json.dump(verdict, f, indent=2)
        self.log("verdict", verdict=verdict.get("verdict"), forked=verdict.get("branches_forked"), installed=verdict.get("installed"))
        print(verdict.get("verdict"), flush=True)


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--cluster", required=True, help="cluster name in the scenario; containers are <cluster>-camusN")
    p.add_argument("--timeline", required=True, help="the scenario run's timeline.jsonl")
    p.add_argument("--out", required=True, help="directory for events.jsonl and verdict.json")
    p.add_argument("--nodes", type=int, default=5)
    p.add_argument("--base-port", type=int, default=15095)
    p.add_argument("--branches", type=int, required=True)
    p.add_argument("--fork-span", type=int, default=600, help="seconds over which to spread the forks")
    p.add_argument("--account-samples", type=int, default=20, help="roughly how many branches also get a full accounts baseline")
    p.add_argument("--marks-at", type=int, default=45, help="seconds after the nemesis starts")
    p.add_argument("--fork-at", type=int, default=120, help="seconds after the nemesis starts")
    p.add_argument("--fork-budget", type=int, default=120, help="seconds one fork may retry")
    p.add_argument("--max-fork-errors", type=int, default=20)
    p.add_argument("--read-budget", type=int, default=60, help="seconds one branch read may retry")
    p.add_argument("--compact-wait", type=int, default=240, help="seconds after the last fork for the log to stop reaching it; retention is 3 minutes")
    p.add_argument("--move-wait", type=int, default=900, help="seconds to wait for the replica to move")
    p.add_argument("--separate-meta", action="store_true", help="before the move, take data-partition leadership off the meta leader")
    p.add_argument("--settle", type=int, default=45, help="seconds to let the replica catch up before reading")
    p.add_argument("--restart-gap", type=int, default=20, help="seconds between one node answering and the next restart")
    args = p.parse_args()
    try:
        HoldsRun(args).run()
    except Exception as e:  # report the failure in the run's own record, then fail
        with open(os.path.join(args.out, "verdict.json"), "w") as f:
            json.dump({"verdict": "DRIVER_ERROR", "error": f"{type(e).__name__}: {e}"}, f, indent=2)
        print(f"DRIVER_ERROR {type(e).__name__}: {e}", file=sys.stderr, flush=True)
        raise


if __name__ == "__main__":
    main()
