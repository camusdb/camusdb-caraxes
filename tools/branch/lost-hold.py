#!/usr/bin/env python3
"""When a branch's hold is removed, do reads refuse, or return short results?

A hold's protection ends only when the reaper removes it after its lease lapses, and history can then be reclaimed. The
branch must fail with BranchSnapshotProtectionLost (CADB0539, HTTP 410) from then on: on every node, for its children,
and after a restart. A short or empty successful result is the one outcome that must never happen.

Two things renew a hold: the sweep on the registry's leader, and the guard on any branch read. To starve both, this
stops all reads of the branches and pauses two replicas of the data partition that holds the registry. The sweep then
has no leader, while the meta partition, which holds the holds and runs the reaper, keeps its majority.

Runs beside a Caraxes scenario (bank load on `caraxes`, one data partition, RF3, five nodes, a short lease):

  seed the marks, fork `frozen` and its child `grand`, write to the marks in the parent, read both for a baseline
  control: pause for less than the lease, then read. Both must still read correctly: an outage alone loses nothing
  starve for the lease plus two reaper cycles, resume, and read both from every node straight away and after a wait
  restart every node, and read both again
  writes on the lost branch must refuse; the parent must work; a fresh fork must read correctly; the lost branch must drop

Each read is CORRECT, LOST (CADB0539), RETRY (a retryable refusal) or WRONG (a success that differs from the baseline).
Prints and writes verdict.json: WRONG_RESULTS (any WRONG after the holds could have gone, the finding this test looks for),
FAILS_CLOSED (only LOST at the end, on every node and after the restart), NOT_LOST (the holds survived, so the test says
nothing), or ANOMALY_NOT_ATTRIBUTED (for instance the control failed, or a branch stopped refusing).
"""
import argparse
import collections
import importlib.util
import json
import os
import re
import subprocess
import sys
import time

here = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("move_repro", os.path.join(here, "move-repro.py"))
move_repro = importlib.util.module_from_spec(spec)
spec.loader.exec_module(move_repro)

PARENT, BRANCH = move_repro.PARENT, move_repro.BRANCH
CHILD = "grand"
GROUP = 300
LOST = "CADB0539"
RETRYABLE_CODES = {"CADB0502", "CADB0503", "CADB0504"}


class LostRun(move_repro.MoveRun):
    def statement(self, node, db, sql, timeout=60):
        """One attempt: (status, code, answer). A connection failure is status 'transport'."""
        try:
            answer = self.sql_once(node, db, sql, timeout)
        except (OSError, ValueError) as e:
            return "transport", None, str(e)
        if answer.get("status") == "ok":
            return "ok", None, answer
        return "error", answer.get("code"), answer.get("message")

    def seed(self, gateway):
        self.sql(gateway, PARENT, "CREATE TABLE lost_marks (id INT64 PRIMARY KEY, v INT64 NOT NULL)")
        for start in range(0, 3 * GROUP, 100):
            values = ", ".join(f"({i}, 1)" for i in range(start, start + 100))
            self.sql(gateway, PARENT, f"INSERT INTO lost_marks (id, v) VALUES {values}")

    def write_after_fork(self, gateway):
        """Old revisions the holds were pinning: without a hold, they are what reclamation may remove."""
        self.sql(gateway, PARENT, f"UPDATE lost_marks SET v = 2 WHERE id >= {GROUP} AND id < {2 * GROUP}")
        self.sql(gateway, PARENT, f"DELETE FROM lost_marks WHERE id >= {2 * GROUP} AND id < {3 * GROUP}")

    def read_branch(self, node, branch):
        """Classifies one read of this branch through this node. Marks first: they are small and have an exact answer."""
        status, code, answer = self.statement(node, branch, "SELECT id, v FROM lost_marks")
        if status == "transport":
            return {"class": "TRANSPORT", "detail": str(answer)[:120]}
        if status == "error":
            kind = "LOST" if code == LOST else "RETRY" if code in RETRYABLE_CODES else "ERROR"
            return {"class": kind, "code": code, "detail": str(answer)[:160]}
        rows = {int(r[0]): int(r[1]) for r in answer["rows"]}
        if rows != {i: 1 for i in range(3 * GROUP)}:
            return {"class": "WRONG", "table": "lost_marks", "rows": len(rows), "v1": sum(1 for v in rows.values() if v == 1)}
        status, code, answer = self.statement(node, branch, "SELECT COUNT(*), SUM(balance), SUM(version) FROM workload_accounts")
        if status == "transport":
            return {"class": "TRANSPORT", "detail": str(answer)[:120]}
        if status == "error":
            kind = "LOST" if code == LOST else "RETRY" if code in RETRYABLE_CODES else "ERROR"
            return {"class": kind, "code": code, "detail": str(answer)[:160]}
        got = [str(x) for x in answer["rows"][0]]
        if got != self.baseline[branch]:
            return {"class": "WRONG", "table": "workload_accounts", "got": got, "baseline": self.baseline[branch]}
        return {"class": "CORRECT"}

    def sweep_reads(self, label, nodes=None):
        results = []
        for branch in (BRANCH, CHILD):
            for node in nodes or self.nodes:
                entry = {"label": label, "branch": branch, "node": node, **self.read_branch(node, branch)}
                results.append(entry)
        tally = collections.Counter((r["branch"], r["class"]) for r in results)
        self.log("reads", label=label, tally={f"{k[0]}:{k[1]}": v for k, v in tally.items()},
                 wrong=[r for r in results if r["class"] == "WRONG"][:3], other=[r for r in results if r["class"] in ("ERROR", "TRANSPORT")][:3])
        self.all_reads += results
        return results

    def read_until_settled(self, label, budget):
        """Reads repeatedly until every node answers LOST or the budget ends. Every answer on the way is kept."""
        deadline = time.time() + budget
        rounds = 0
        while True:
            results = self.sweep_reads(f"{label}-{rounds}")
            rounds += 1
            if all(r["class"] == "LOST" for r in results) or time.time() > deadline:
                return results
            time.sleep(1)

    def pause(self, nodes):
        subprocess.run(["docker", "pause"] + [f"{self.args.cluster}-{n}" for n in nodes], capture_output=True, text=True)

    def unpause(self, nodes):
        subprocess.run(["docker", "unpause"] + [f"{self.args.cluster}-{n}" for n in nodes], capture_output=True, text=True)

    def pick_paused(self):
        """Two replicas of the data partition, not the meta leader if that can be helped, so meta keeps a majority."""
        hosts = [self.node_at(e) for e in self.hosts()]
        meta = self.meta_leader()
        hosts = [n for n in hosts if n]
        hosts.sort(key=lambda n: n == meta)
        return hosts[:2], meta

    def live_holds(self):
        return {n: self.floor_metrics(n).get("kahuna_snapshot_floor_live_holds") for n in self.nodes}

    def logged(self, needle):
        total = 0
        for node in self.nodes:
            out = subprocess.run(["docker", "logs", f"{self.args.cluster}-{node}"], capture_output=True, text=True, errors="replace")
            total += (out.stdout + out.stderr).count(needle)
        return total

    def run(self):
        a = self.args
        self.d0 = None
        self.all_reads = []
        self.baseline = {}
        self.log("waiting-for-nemesis", timeline=a.timeline, lease_ms=a.lease_ms)
        self.wait_start()
        self.wait_offset(a.marks_at)
        gateway = "camus1"
        self.seed(gateway)
        self.wait_offset(a.fork_at)
        self.sql(gateway, PARENT, f"CREATE DATABASE {BRANCH} BRANCH FROM {PARENT}")
        self.sql(gateway, BRANCH, "SELECT COUNT(*) FROM lost_marks")
        self.sql(gateway, BRANCH, f"CREATE DATABASE {CHILD} BRANCH FROM {BRANCH}")
        self.write_after_fork(gateway)
        leader = self.leader()
        for branch in (BRANCH, CHILD):
            row = self.sql(leader, branch, "SELECT COUNT(*), SUM(balance), SUM(version) FROM workload_accounts")["rows"][0]
            self.baseline[branch] = [str(x) for x in row]
        self.log("baseline", baseline=self.baseline, live_holds=self.live_holds(), branches=self.sql(gateway, PARENT, f"SHOW BRANCHES FROM {PARENT}").get("rows"))
        start_reads = self.sweep_reads("baseline")
        verdict = {"baseline_correct": all(r["class"] == "CORRECT" for r in start_reads)}

        # Control: an outage shorter than the lease has nothing to lose.
        paused, meta = self.pick_paused()
        self.log("control-pause", paused=paused, meta=meta, seconds=a.control_pause)
        self.pause(paused)
        time.sleep(a.control_pause)
        self.unpause(paused)
        time.sleep(a.recover_wait)
        control = self.read_control()
        verdict["control_correct"] = all(r["class"] == "CORRECT" for r in control)

        # Starve. Nothing reads a branch from here until the pause ends.
        paused, meta = self.pick_paused()
        starve = a.lease_ms / 1000 + 2 * a.reaper_seconds + a.starve_margin
        holds_before = self.live_holds()
        self.log("starve-start", paused=paused, meta=meta, seconds=starve, live_holds=holds_before)
        self.pause(paused)
        time.sleep(starve)
        survivors = [n for n in self.nodes if n not in paused]
        holds_during = {n: self.floor_metrics(n).get("kahuna_snapshot_floor_live_holds") for n in survivors}
        self.unpause(paused)
        self.log("starve-end", live_holds=holds_during)
        verdict.update(paused=paused, starve_seconds=starve, holds_before=holds_before, holds_during=holds_during)

        # Straight away, including while the partition has no leader yet, and until every node has settled.
        settled = self.read_until_settled("after-loss", a.settle_budget)
        time.sleep(a.reclaim_wait)  # let the revision sweeps reclaim what the holds were pinning
        late = self.sweep_reads("after-reclaim")

        self.restart_all()
        time.sleep(a.settle)
        after_restart = self.read_until_settled("after-restart", a.settle_budget)

        write = {}
        for branch in (BRANCH, CHILD):
            status, code, answer = self.statement(leader, branch, "UPDATE lost_marks SET v = 9 WHERE id = 0")
            write[branch] = {"status": status, "code": code, "detail": str(answer)[:160]}
        self.log("branch-write-attempt", write=write)

        parent_ok, fresh_ok, drop = None, None, {}
        try:
            parent = self.sql(leader, PARENT, "SELECT COUNT(*) FROM lost_marks")["rows"][0][0]
            parent_ok = int(parent) == 2 * GROUP
            self.sql(leader, PARENT, "CREATE DATABASE fresh BRANCH FROM caraxes")
            rows = self.sql(leader, "fresh", "SELECT COUNT(*) FROM lost_marks")["rows"][0][0]
            fresh_ok = int(rows) == 2 * GROUP
        except RuntimeError as e:
            self.log("parent-or-fresh-failed", error=str(e)[:400])
        for branch in (CHILD, BRANCH):  # the child first: a parent with live branches may refuse to drop
            status, code, answer = self.statement(leader, PARENT, f"DROP DATABASE {branch}")
            drop[branch] = {"status": status, "code": code, "detail": str(answer)[:200]}
        self.log("cleanup", parent_ok=parent_ok, fresh_ok=fresh_ok, drop=drop)

        wrong = [r for r in self.all_reads if r["class"] == "WRONG"]
        ended_lost = all(r["class"] == "LOST" for r in settled) and all(r["class"] == "LOST" for r in late) and all(r["class"] == "LOST" for r in after_restart)
        any_lost = any(r["class"] == "LOST" for r in settled + late + after_restart)
        tally = collections.Counter(r["class"] for r in self.all_reads)
        by_stage = {}
        for r in self.all_reads:
            stage = re.sub(r"-\d+$", "", r["label"])
            by_stage.setdefault(stage, collections.Counter())[f"{r['branch']}:{r['class']}"] += 1
        verdict.update(read_classes=dict(tally), by_stage={k: dict(v) for k, v in by_stage.items()}, wrong=wrong[:20], branch_write=write,
                       parent_ok=parent_ok, fresh_fork_ok=fresh_ok, drop=drop, ended_lost=ended_lost,
                       marker_logs=self.logged("lost snapshot protection"), renew_failures=self.logged("Failed to renew snapshot hold"))
        if wrong and not verdict["baseline_correct"]:
            verdict["verdict"] = "ANOMALY_NOT_ATTRIBUTED"
        elif wrong:
            verdict["verdict"] = "WRONG_RESULTS"
        elif not verdict["baseline_correct"] or not verdict["control_correct"]:
            verdict["verdict"] = "ANOMALY_NOT_ATTRIBUTED"
        elif ended_lost:
            verdict["verdict"] = "FAILS_CLOSED"
        elif not any_lost:
            verdict["verdict"] = "NOT_LOST"
        else:
            verdict["verdict"] = "ANOMALY_NOT_ATTRIBUTED"
        self.finish(verdict)

    def read_control(self):
        """A pause leaves a moment of retries, so wait for the first answers, then require them correct."""
        deadline = time.time() + self.args.settle_budget
        while True:
            results = self.sweep_reads("control")
            if not any(r["class"] in ("RETRY", "TRANSPORT") for r in results) or time.time() > deadline:
                return results
            time.sleep(2)

    def finish(self, verdict):
        with open(os.path.join(self.dir, "verdict.json"), "w") as f:
            json.dump(verdict, f, indent=2)
        self.log("verdict", verdict=verdict["verdict"], read_classes=verdict.get("read_classes"), by_stage=verdict.get("by_stage"))
        print(verdict["verdict"], flush=True)


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--cluster", required=True, help="cluster name in the scenario; containers are <cluster>-camusN")
    p.add_argument("--timeline", required=True, help="the scenario run's timeline.jsonl")
    p.add_argument("--out", required=True, help="directory for events.jsonl and verdict.json")
    p.add_argument("--nodes", type=int, default=5)
    p.add_argument("--base-port", type=int, default=15095)
    p.add_argument("--marks-at", type=int, default=45, help="seconds after the nemesis starts")
    p.add_argument("--fork-at", type=int, default=120, help="seconds after the nemesis starts")
    p.add_argument("--lease-ms", type=int, default=20000, help="the scenario's branch_snapshot_hold_lease_ms")
    p.add_argument("--reaper-seconds", type=int, default=60, help="Kahuna's CollectionInterval")
    p.add_argument("--control-pause", type=int, default=6, help="seconds; less than the lease")
    p.add_argument("--recover-wait", type=int, default=15)
    p.add_argument("--starve-margin", type=int, default=30)
    p.add_argument("--settle-budget", type=int, default=240, help="seconds to keep reading until every node refuses")
    p.add_argument("--reclaim-wait", type=int, default=420, help="seconds for revision sweeps to reclaim what the holds pinned")
    p.add_argument("--settle", type=int, default=45)
    p.add_argument("--restart-gap", type=int, default=20)
    args = p.parse_args()
    try:
        LostRun(args).run()
    except Exception as e:  # report the failure in the run's own record, then fail
        with open(os.path.join(args.out, "verdict.json"), "w") as f:
            json.dump({"verdict": "DRIVER_ERROR", "error": f"{type(e).__name__}: {e}"}, f, indent=2)
        print(f"DRIVER_ERROR {type(e).__name__}: {e}", file=sys.stderr, flush=True)
        raise


if __name__ == "__main__":
    main()
