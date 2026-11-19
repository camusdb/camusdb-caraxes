#!/usr/bin/env python3
"""Does moving a replica take away the history a database branch reads?

A replica move adds a learner on a node that never held the partition and seeds it from the leader's log, or with a
whole-partition snapshot when the log no longer reaches back. The held-history fix makes that snapshot carry each moved
key's revision at each hold, so this should now be safe. It was tested on an install into a node that had the partition
before; this tests one into a node that never did, which has no leftovers to answer from.

Runs beside a Caraxes scenario (bank load on `caraxes`, one data partition, more nodes than the replication factor):

  seed repro_marks, fork under load, then update, delete and insert marks in the parent
  D0  baseline
  wait until the log no longer reaches the fork
  D1  control, leader unchanged
  ask a host of the partition that is neither its leader nor the meta leader to leave: its replica moves to a new node
  D2  control on the leader that did not move
  D3  leadership on the new replica: the test, through two gateways
  restart every node, then D4 with leadership on the new replica again, to rule out memory
  D5  control back on a replica that held the partition all along

Prints and writes verdict.json: REPRODUCED (wrong rows on the new replica only), REFUSED (the new replica declined the
read, which is safe but unavailable), NOT_REPRODUCED, ANOMALY_NOT_ATTRIBUTED, or VACUOUS (no move, or leadership could
not reach the new replica). `seeded_by` says whether the new replica took an install or caught up from the log; only an
install tests the fix.
"""
import argparse
import importlib.util
import json
import os
import re
import subprocess
import sys
import time

spec = importlib.util.spec_from_file_location(
    "install_repro", os.path.join(os.path.dirname(os.path.abspath(__file__)), "install-repro.py"))
install_repro = importlib.util.module_from_spec(spec)
spec.loader.exec_module(install_repro)

PARENT, BRANCH = install_repro.PARENT, install_repro.BRANCH
PARTITION = 1


class MoveRun(install_repro.Run):
    def hosts(self):
        """The partition's replicas as (endpoint, role), or None when no node answers."""
        for node in self.nodes:
            placement = self.placement(node)
            for part in (placement or {}).get("partitions", []):
                if part.get("partitionId") == PARTITION:
                    return {r["endpoint"]: r.get("role") for r in part.get("replicas", [])}
        return None

    def node_at(self, endpoint):
        return next((n for n in self.nodes if self.endpoint(n) == endpoint), None)

    def meta_leader(self):
        """The node that most recently won the meta partition's election, from the logs. No API reports it."""
        best = (None, "")
        for node in self.nodes:
            out = subprocess.run(["docker", "logs", f"{self.args.cluster}-{node}"],
                                 capture_output=True, text=True, errors="replace")
            for line in (out.stdout + out.stderr).splitlines():
                if "/0/Candidate] Voted to become leader" in line and line[:19] > best[1]:
                    best = (node, line[:19])
        return best[0]

    def leave(self, node):
        """Asks this node to leave. The route takes loopback only, so the call runs inside its network."""
        out = subprocess.run(
            ["docker", "run", "--rm", "--network", f"container:{self.args.cluster}-{node}", "curlimages/curl:latest",
             "-sS", "-m", "300", "-X", "POST", "http://127.0.0.1:5095/v1/cluster/leave"],
            capture_output=True, text=True)
        return out.stdout or out.stderr

    def restart_all(self):
        for node in self.nodes:
            started = time.time()
            subprocess.run(["docker", "restart", f"{self.args.cluster}-{node}"], capture_output=True, text=True)
            healthy = self.wait_healthy(node)
            time.sleep(self.args.restart_gap)
            self.log("restarted", node=node, healthy=healthy, seconds=round(time.time() - started, 1))

    def run(self):
        self.d0 = None
        a = self.args
        self.log("waiting-for-nemesis", timeline=a.timeline)
        self.wait_start()

        self.wait_offset(a.marks_at)
        gateway = "camus1"
        self.seed_marks(gateway)

        self.wait_offset(a.fork_at)
        self.sql(gateway, PARENT, f"CREATE DATABASE {BRANCH} BRANCH FROM {PARENT}")
        branches = self.sql(gateway, PARENT, f"SHOW BRANCHES FROM {PARENT}")
        self.log("forked", rows=branches.get("rows"))
        m = re.search(r"HLC\((-?\d+):(\d+):(\d+)\)", str(branches["rows"][0]))
        as_of_ms = int(m.group(2)) if m else None
        self.write_marks_after_fork(gateway)

        d0 = self.digest("D0-baseline", self.leader(), as_of_ms=as_of_ms)[0]
        time.sleep(a.compact_wait)
        d1 = self.digest("D1-before-move", self.leader(), as_of_ms=as_of_ms)[0]

        before = self.hosts()
        leader, meta = self.leader(), self.meta_leader()
        candidates = [n for n in (self.node_at(e) for e in before) if n and n != leader]
        victim = next((n for n in candidates if n != meta), candidates[0])
        outsiders = [n for n in self.nodes if self.endpoint(n) not in before]
        counts = {n: self.install_count(n) for n in outsiders}
        self.log("before-move", hosts=before, leader=leader, meta=meta, victim=victim, outsiders=outsiders, installs=counts)

        started = time.time()
        deadline = started + a.move_wait
        answer = {}
        while time.time() < deadline:  # a drain longer than the node's timeout answers retryable, and keeps what it moved
            try:
                answer = json.loads(self.leave(victim))
            except ValueError as e:
                answer = {"retryable": True, "reason": str(e)}
            self.log("leave-answered", victim=victim, answer=answer, seconds=round(time.time() - started, 1))
            if answer.get("left") or not answer.get("retryable"):
                break
            time.sleep(5)
        left = bool(answer.get("left"))
        if not left:
            self.finish({"victim": victim, "before": before, "leave": answer, "verdict": "VACUOUS"})
            return
        self.nodes = [n for n in self.nodes if n != victim]
        if gateway == victim:
            gateway = self.nodes[0]

        deadline = max(deadline, time.time() + 60)
        new_replica, after = None, before
        while time.time() < deadline:
            after = self.hosts() or after
            fresh = [e for e in after if e not in before]
            if fresh and all(role == "Voter" for role in after.values()) and len(after) == len(before):
                new_replica = self.node_at(fresh[0])
                break
            time.sleep(5)
        self.log("after-move", hosts=after, new_replica=new_replica, seconds=round(time.time() - started, 1))
        verdict = {"victim": victim, "before": before, "after": after, "new_replica": new_replica, "leave": answer,
                   "d0_clean": self.clean(d0), "d1_clean": self.clean(d1)}
        if not new_replica:
            verdict["verdict"] = "VACUOUS"
            self.finish(verdict)
            return

        time.sleep(a.settle)
        installs = self.install_count(new_replica) or 0
        verdict["seeded_by"] = "install" if installs > counts.get(new_replica, 0) else "log"
        keeper = self.leader()
        d2 = self.digest("D2-before-new-leads", keeper, as_of_ms=as_of_ms)[0]
        moved = self.transfer(new_replica)
        others = [n for n in self.nodes if n != new_replica]
        d3 = self.digest("D3-new-replica-leads", new_replica, gateways=[new_replica, others[0]], as_of_ms=as_of_ms) if moved else []

        self.restart_all()
        moved_again = self.transfer(new_replica)
        d4 = self.digest("D4-new-replica-leads-after-restart", new_replica, gateways=[others[0]], as_of_ms=as_of_ms) if moved_again else []
        old = next((n for n in (self.node_at(e) for e in after if e in before) if n), None)
        back = bool(old) and self.transfer(old)
        d5 = self.digest("D5-old-replica-leads", old, as_of_ms=as_of_ms)[0] if back else None

        tests = d3 + d4
        verdict.update({"transferred": moved, "transferred_after_restart": moved_again, "d2_clean": self.clean(d2), "d5_clean": self.clean(d5),
                        "tests": [{k: d.get(k) for k in ("label", "gateway", "leader_as_expected", "rows", "missing", "extra", "changed", "identical", "marks",
                                                         "parent_as_of", "parent_as_of_error", "error", "wrong_rows_total")} for d in tests]})
        if not tests or not all(d["leader_as_expected"] for d in tests) or not verdict["d0_clean"] or not verdict["d1_clean"]:
            verdict["verdict"] = "VACUOUS"
        elif all(self.clean(d) for d in tests):
            verdict["verdict"] = "NOT_REPRODUCED"
        elif any(d.get("error") for d in tests) and not any(d.get("marks") and not self.clean(d) for d in tests):
            verdict["verdict"] = "REFUSED"
        elif verdict["d2_clean"] and verdict["d5_clean"]:
            verdict["verdict"] = "REPRODUCED"
        else:
            verdict["verdict"] = "ANOMALY_NOT_ATTRIBUTED"
        self.finish(verdict)

    def clean(self, d):
        return bool(d) and d.get("identical") is True and bool(d.get("marks")) and self.marks_clean(d["marks"])


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--cluster", required=True, help="cluster name in the scenario; containers are <cluster>-camusN")
    p.add_argument("--timeline", required=True, help="the scenario run's timeline.jsonl")
    p.add_argument("--out", required=True, help="directory for events.jsonl, d0.json, verdict.json")
    p.add_argument("--nodes", type=int, default=5)
    p.add_argument("--base-port", type=int, default=15095)
    p.add_argument("--marks-at", type=int, default=45, help="seconds after the nemesis starts")
    p.add_argument("--fork-at", type=int, default=150, help="seconds after the nemesis starts")
    p.add_argument("--compact-wait", type=int, default=300, help="seconds after the fork for the log to stop reaching it; retention is 3 minutes")
    p.add_argument("--move-wait", type=int, default=600, help="seconds to wait for the new replica to become a voter")
    p.add_argument("--settle", type=int, default=45, help="seconds to let the new replica catch up before reading")
    p.add_argument("--restart-gap", type=int, default=20, help="seconds between one node answering and the next restart")
    p.add_argument("--parent-as-of-budget", type=int, default=20)
    args = p.parse_args()
    try:
        MoveRun(args).run()
    except Exception as e:  # report the failure in the run's own record, then fail
        with open(os.path.join(args.out, "verdict.json"), "w") as f:
            json.dump({"verdict": "DRIVER_ERROR", "error": f"{type(e).__name__}: {e}"}, f, indent=2)
        print(f"DRIVER_ERROR {type(e).__name__}: {e}", file=sys.stderr, flush=True)
        raise


if __name__ == "__main__":
    main()
