#!/usr/bin/env python3
"""Can a replica be seeded when a partition's snapshot is larger than the receive cap, with no branches at all?

A whole-partition snapshot travels in 3 MiB chunks and a receiver refuses a session that would pass
SnapshotMaxPendingBytes, 512 MiB by default, which CamusDB does not expose. Runs of many branches failed at chunk 170, the
one that would pass it, so this asks whether the same happens to a plain large table. No fork is made, so the export
carries current rows and the few revisions from just before it.

Runs beside a Caraxes scenario (bank load on `caraxes`, one data partition, more nodes than the replication factor):

  wait until the log no longer reaches the start, so the new replica needs an install
  ask a host that is neither leader nor meta leader to leave: its replica moves to a new node
  retry the leave until it commits or the budget runs out

After a successful move, leadership goes to the new replica and every row is counted through it.

Prints and writes verdict.json: INSTALLED (the new replica was seeded, with the chunk count), BLOCKED (never seeded, with
each transfer failure the leader logged), or VACUOUS (no install was needed).
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
    "move_repro", os.path.join(os.path.dirname(os.path.abspath(__file__)), "move-repro.py"))
move_repro = importlib.util.module_from_spec(spec)
spec.loader.exec_module(move_repro)

STARTED = re.compile(r"/1/Leader\] Starting a snapshot transfer to (\S+) at index (\d+)")
INSTALLED = re.compile(r"/1/Leader\] Snapshot installed on (\S+) at index (\d+) \((\d+) chunk")
FAILED = re.compile(r"/1/Leader\] Snapshot transfer to (\S+) failed \((\w+), attempt (\d+)")


class SizeRun(move_repro.MoveRun):
    def transfers(self):
        """What the logs of every node still in the cluster say about snapshot transfers of the data partition."""
        started, failed, installed = [], [], []
        for node in self.nodes:
            out = subprocess.run(["docker", "logs", f"{self.args.cluster}-{node}"], capture_output=True, text=True, errors="replace")
            for line in (out.stdout + out.stderr).splitlines():
                if STARTED.search(line):
                    started.append({"node": node, "at": line[:19], "target": STARTED.search(line).group(1)})
                elif INSTALLED.search(line):
                    m = INSTALLED.search(line)
                    installed.append({"node": node, "at": line[:19], "target": m.group(1), "chunks": int(m.group(3))})
                elif FAILED.search(line):
                    failed.append({"node": node, "at": line[:19], "line": line[20:240]})
        return started, failed, installed

    def read_through_new_replica(self, before, victim, expected_rows):
        """Moves leadership onto the replica the install seeded and counts every row there. Leadership stays, so the scenario's reconciliation reads through it too."""
        self.nodes = [n for n in self.nodes if n != victim]
        new_replica, deadline = None, time.time() + 120
        while time.time() < deadline and not new_replica:
            after = self.hosts()
            fresh = [e for e in after if e not in before]
            if fresh and all(role == "Voter" for role in after.values()):
                new_replica = self.node_at(fresh[0])
            else:
                time.sleep(5)
        if not new_replica:
            return {"new_replica": None}
        self.transfer(new_replica)
        leader = self.leader()
        started = time.time()
        answer = self.sql(new_replica, "caraxes", "SELECT COUNT(*) FROM workload_accounts", timeout=300, budget=300)
        count = int(answer["rows"][0][0])
        result = {"new_replica": new_replica, "leader": leader, "rows": count, "expected": expected_rows, "correct": count == expected_rows and leader == new_replica,
                  "seconds": round(time.time() - started, 1)}
        self.log("read-through-new-replica", **result)
        return result

    def run(self):
        a = self.args
        self.wait_start()
        self.wait_offset(a.compact_wait)
        before = self.hosts()
        leader, meta = self.leader(), self.meta_leader()
        candidates = [n for n in (self.node_at(e) for e in before) if n and n != leader]
        victim = next((n for n in candidates if n != meta), candidates[0])
        self.log("before-move", hosts=before, leader=leader, meta=meta, victim=victim)

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

        transfer_starts, failures, installs = self.transfers()
        verdict = {"victim": victim, "leave": answer, "move_seconds": round(time.time() - started, 1), "transfer_starts": len(transfer_starts),
                   "failures": failures[:12], "installs": installs}
        if answer.get("left") and installs:
            verdict["read_through_new_replica"] = self.read_through_new_replica(before, victim, a.expected_rows)
        if installs:
            verdict["verdict"] = "INSTALLED"
        elif answer.get("left"):
            verdict["verdict"] = "VACUOUS"
        else:
            verdict["verdict"] = "BLOCKED"
        with open(os.path.join(a.out, "verdict.json"), "w") as f:
            json.dump(verdict, f, indent=2)
        self.log("verdict", verdict=verdict["verdict"], starts=len(transfer_starts), installs=installs)
        print(verdict["verdict"], flush=True)


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--cluster", required=True, help="cluster name in the scenario; containers are <cluster>-camusN")
    p.add_argument("--timeline", required=True, help="the scenario run's timeline.jsonl")
    p.add_argument("--out", required=True, help="directory for events.jsonl and verdict.json")
    p.add_argument("--nodes", type=int, default=5)
    p.add_argument("--base-port", type=int, default=15095)
    p.add_argument("--compact-wait", type=int, default=300, help="seconds after the nemesis starts before the move")
    p.add_argument("--move-wait", type=int, default=480, help="seconds to keep retrying the leave")
    p.add_argument("--expected-rows", type=int, default=900000)
    args = p.parse_args()
    try:
        SizeRun(args).run()
    except Exception as e:  # report the failure in the run's own record, then fail
        with open(os.path.join(args.out, "verdict.json"), "w") as f:
            json.dump({"verdict": "DRIVER_ERROR", "error": f"{type(e).__name__}: {e}"}, f, indent=2)
        print(f"DRIVER_ERROR {type(e).__name__}: {e}", file=sys.stderr, flush=True)
        raise


if __name__ == "__main__":
    main()
