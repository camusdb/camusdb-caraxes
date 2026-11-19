#!/usr/bin/env python3
"""Does held history survive a chain of installs, and a fork during an export?

An install carries each moved key's revision at each registered hold, its revisions from just before the export onward
and the newest one below that. This replaces all three replicas of the partition, each new one seeded from the one
before, so by the end no replica ever saw the original writes. A fork can also land while an export is running, after
the export read the list of holds. Its rows are covered only by the export's revisions from just before it started.
The rows are also written right after the fork, while the export has yet to reach many of them.

Runs beside a Caraxes scenario (bank load on `caraxes`, one data partition, six nodes, RF3):

  seed the marks, fork `frozen` under load, write to the marks in the parent, wait until the log no longer reaches the fork
  for each of three steps:
    leadership on the replica the last step added, so the install comes from it
    an original replica leaves, and is stopped once it has left. From step 2, when the leader starts the export:
    fork a new branch and write to its marks in the parent, recording whether the writes finished before the export
    read every branch on the leader that seeded the new replica, then with leadership on the new replica, through two gateways
  restart every node, then read every branch with leadership on each replica in turn

Prints and writes verdict.json: REPRODUCED (wrong rows, controls clean), REFUSED (a branch read declined, which is safe but
unavailable), NOT_REPRODUCED, ANOMALY_NOT_ATTRIBUTED, or VACUOUS (a step did not reach the state under test).
"""
import argparse
import calendar
import importlib.util
import json
import os
import random
import re
import subprocess
import sys
import threading
import time

here = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("move_repro", os.path.join(here, "move-repro.py"))
move_repro = importlib.util.module_from_spec(spec)
spec.loader.exec_module(move_repro)

PARENT, BRANCH = move_repro.PARENT, move_repro.BRANCH
MID_GROUP = 300  # rows per group of a mid-export branch's marks
TRANSFER = re.compile(r"/1/Leader\] Starting a snapshot transfer to (\S+) at index (\d+)")
INSTALLED = re.compile(r"/1/Leader\] Snapshot installed on (\S+) at index (\d+) \((\d+) chunk")


def log_epoch(line):
    """Node logs carry UTC to the second."""
    return calendar.timegm(time.strptime(line[:19], "%Y-%m-%d %H:%M:%S"))


class ChainRun(move_repro.MoveRun):
    def mid_table(self, branch):
        return f"marks_{branch}"

    def seed_mid_marks(self, gateway, branch):
        table = self.mid_table(branch)
        self.sql(gateway, PARENT, f"CREATE TABLE {table} (id INT64 PRIMARY KEY, v INT64 NOT NULL)")
        for start in range(0, 3 * MID_GROUP, 100):
            values = ", ".join(f"({i}, 1)" for i in range(start, start + 100))
            self.sql(gateway, PARENT, f"INSERT INTO {table} (id, v) VALUES {values}")

    def write_mid_marks(self, gateway, branch):
        table = self.mid_table(branch)
        self.sql(gateway, PARENT, f"UPDATE {table} SET v = 2 WHERE id >= {MID_GROUP} AND id < {2 * MID_GROUP}")
        self.sql(gateway, PARENT, f"DELETE FROM {table} WHERE id >= {2 * MID_GROUP} AND id < {3 * MID_GROUP}")
        values = ", ".join(f"({i}, 3)" for i in range(3 * MID_GROUP, 3 * MID_GROUP + 100))
        self.sql(gateway, PARENT, f"INSERT INTO {table} (id, v) VALUES {values}")

    def read_mid_marks(self, gateway, branch):
        table = self.mid_table(branch)
        rows = self.sql(gateway, branch, f"SELECT id, v FROM {table}")["rows"]
        seen = {int(r[0]): int(r[1]) for r in rows}
        groups = {"untouched": range(0, MID_GROUP), "updated": range(MID_GROUP, 2 * MID_GROUP),
                  "deleted": range(2 * MID_GROUP, 3 * MID_GROUP)}
        result = {"inserted_present": sum(1 for i in seen if i >= 3 * MID_GROUP)}
        rng = random.Random(11)
        for name, ids in groups.items():
            present = [i for i in ids if i in seen]
            result[name] = {"present": len(present), "v1": sum(1 for i in present if seen[i] == 1)}
            hits = 0
            for i in rng.sample(list(ids), 10):
                hits += 1 if self.sql(gateway, branch, f"SELECT v FROM {table} WHERE id = {i}")["rows"] else 0
            result[name]["point_reads_of_10"] = hits
        return result

    @staticmethod
    def mid_clean(marks):
        return bool(marks) and marks["inserted_present"] == 0 and all(
            marks[g]["v1"] == MID_GROUP and marks[g]["point_reads_of_10"] == 10
            for g in ("untouched", "updated", "deleted"))

    def check_branch(self, label, branch, expected_leader, gateway):
        before = self.leader()
        started = time.time()
        entry = {"label": label, "branch": branch, "gateway": gateway, "expected_leader": expected_leader}
        try:
            accounts = self.scan_accounts(gateway, branch)
            marks = self.read_marks(gateway) if branch == BRANCH else self.read_mid_marks(gateway, branch)
            baseline = self.d0 if branch == BRANCH else self.baselines[branch]
            missing = [k for k in baseline if k not in accounts]
            extra = [k for k in accounts if k not in baseline]
            changed = [k for k in accounts if k in baseline and accounts[k] != baseline[k]]
            marks_ok = self.marks_clean(marks) if branch == BRANCH else self.mid_clean(marks)
            entry.update(rows=len(accounts), missing=len(missing), extra=len(extra), changed=len(changed),
                         identical=not (missing or extra or changed), marks=marks, marks_ok=marks_ok, error=None,
                         sample_missing=missing[:3], sample_changed=[(k, baseline[k], accounts[k]) for k in changed[:3]])
        except RuntimeError as e:
            entry.update(error=str(e), identical=None, marks_ok=None)
        after = self.leader()
        entry.update(leader_before=before, leader_after=after, leader_as_expected=before == after == expected_leader,
                     seconds=round(time.time() - started, 1))
        self.log("branch-read", **entry)
        self.reads.append(entry)
        return entry

    def check_all(self, label, expected_leader, gateways):
        return [self.check_branch(label, b, expected_leader, g) for g in gateways for b in [BRANCH, *self.baselines]]

    # ---- A fork while the export runs ----------------------------------------------------------------------------
    def watch_export(self, source, branch, gateway):
        """Follows the source's log. When the export starts, forks and writes straight away and records the timing."""
        state = {"branch": branch, "source": source, "export_started": None, "forked": False, "installed": None}
        self.watch_stop = threading.Event()

        def read():
            proc = subprocess.Popen(["docker", "logs", "-f", "--since", "2s", f"{self.args.cluster}-{source}"],
                                    stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, errors="replace")
            state["proc"] = proc
            for line in proc.stdout:
                if state["export_started"] is None and TRANSFER.search(line):
                    state["export_started"] = log_epoch(line)
                    state["target"] = TRANSFER.search(line).group(1)
                    self.fork_in_export(state, gateway)
                match = INSTALLED.search(line)
                if match and state.get("target") == match.group(1):
                    state["installed"] = log_epoch(line)
                    state["chunks"] = int(match.group(3))
                    break
            proc.terminate()

        state["thread"] = threading.Thread(target=read, daemon=True)
        state["thread"].start()
        return state

    def fork_in_export(self, state, gateway):
        branch = state["branch"]
        try:
            self.sql(gateway, PARENT, f"CREATE DATABASE {branch} BRANCH FROM {PARENT}", budget=30)
            state["forked_at"] = time.time()
            self.write_mid_marks(gateway, branch)
            state["written_at"] = time.time()
            state["forked"] = True
            self.log("forked-in-export", branch=branch, export_started=state["export_started"],
                     forked_at=round(state["forked_at"], 1), written_at=round(state["written_at"], 1))
            self.pending_baseline = (branch, gateway)
        except RuntimeError as e:
            state["error"] = str(e)
            self.log("fork-in-export-failed", branch=branch, error=str(e))

    def take_mid_baseline(self):
        """The baseline is the branch as the source read it. A later read that disagrees is the finding."""
        if self.pending_baseline:
            branch, gateway = self.pending_baseline
            self.pending_baseline = None
            live = gateway if gateway in self.nodes else self.nodes[0]
            self.baselines[branch] = self.scan_accounts(live, branch)
            self.log("mid-baseline", branch=branch, rows=len(self.baselines[branch]))

    def finish_watch(self, state):
        state["thread"].join(timeout=60)
        if state.get("proc"):
            state["proc"].terminate()
        overlap = bool(state["forked"] and state["installed"] and state["written_at"] < state["installed"])
        record = {k: state.get(k) for k in ("branch", "source", "target", "export_started", "installed", "chunks", "forked", "error")}
        record.update(fork_before_install_s=round(state["installed"] - state["forked_at"], 1) if overlap else None,
                      writes_before_install_s=round(state["installed"] - state["written_at"], 1) if overlap else None, overlap=overlap)
        self.log("export-watch", **record)
        return record

    # ---- Replacing one replica -----------------------------------------------------------------------------------
    def seeded_from(self, source, endpoint):
        out = subprocess.run(["docker", "logs", f"{self.args.cluster}-{source}"], capture_output=True, text=True, errors="replace")
        for line in (out.stdout + out.stderr).splitlines():
            match = INSTALLED.search(line)
            if match and match.group(1) == endpoint:
                return {"chunks": int(match.group(3)), "index": int(match.group(2))}
        return None

    def replace_one(self, step, originals):
        a = self.args
        before = self.hosts()
        source, meta = self.leader(), self.meta_leader()
        candidates = [n for n in (self.node_at(e) for e in before) if n and n != source and self.ep[n] in originals]
        if not candidates:
            return {"step": step, "error": "no original replica left to replace", "hosts": before}
        victim = next((n for n in candidates if n != meta), candidates[0])
        outsiders = [n for n in self.nodes if self.ep[n] not in before]
        counts = {n: self.install_count(n) for n in outsiders}
        self.log("step-start", step=step, source=source, victim=victim, meta=meta, hosts=before, outsiders=outsiders)

        watch = None
        if step >= 2:
            branch = f"midexport{step}"
            gateway = next(n for n in self.nodes if n not in (victim, source))
            watch = self.watch_export(source, branch, gateway)
            time.sleep(3)  # let the log follower attach before the export can start

        started = time.time()
        deadline = started + a.move_wait
        answer = {}
        while time.time() < deadline:
            try:
                answer = json.loads(self.leave(victim))
            except ValueError as e:
                answer = {"retryable": True, "reason": str(e)}
            self.log("leave-answered", step=step, victim=victim, answer=answer, seconds=round(time.time() - started, 1))
            if answer.get("left") or not answer.get("retryable"):
                break
            time.sleep(5)
        if not answer.get("left"):
            return {"step": step, "victim": victim, "error": "did not leave", "leave": answer}
        self.nodes = [n for n in self.nodes if n != victim]
        subprocess.run(["docker", "stop", f"{self.args.cluster}-{victim}"], capture_output=True, text=True)
        self.log("stopped-leaver", victim=victim)

        new_replica, after = None, before
        deadline = max(deadline, time.time() + 60)
        while time.time() < deadline:
            after = self.hosts() or after
            fresh = [e for e in after if e not in before]
            if fresh and all(r == "Voter" for r in after.values()) and len(after) == len(before):
                new_replica = self.node_at(fresh[0])
                break
            time.sleep(5)
        record = {"step": step, "source": source, "victim": victim, "new_replica": new_replica, "hosts_after": after,
                  "seconds": round(time.time() - started, 1)}
        if not new_replica:
            record["error"] = "no new voter"
            return record

        time.sleep(a.settle)
        installs = self.install_count(new_replica) or 0
        record["installed"] = installs > counts.get(new_replica, 0)
        record["install_from_source"] = self.seeded_from(source, self.ep[new_replica])
        if watch:
            record["watch"] = self.finish_watch(watch)
            self.take_mid_baseline()
        return record

    # ---- The run -------------------------------------------------------------------------------------------------
    def run(self):
        self.d0 = None
        self.baselines = {}
        self.reads = []
        self.pending_baseline = None
        a = self.args
        self.log("waiting-for-nemesis", timeline=a.timeline)
        self.wait_start()
        self.ep = {n: self.endpoint(n) for n in self.nodes}

        self.wait_offset(a.marks_at)
        gateway = "camus1"
        self.seed_marks(gateway)
        for step in (2, 3):
            self.seed_mid_marks(gateway, f"midexport{step}")

        self.wait_offset(a.fork_at)
        self.sql(gateway, PARENT, f"CREATE DATABASE {BRANCH} BRANCH FROM {PARENT}")
        branches = self.sql(gateway, PARENT, f"SHOW BRANCHES FROM {PARENT}")
        self.log("forked", rows=branches.get("rows"))
        self.write_marks_after_fork(gateway)
        leader = self.leader()
        d0 = self.digest("D0-baseline", leader)[0]
        time.sleep(a.compact_wait)
        d1 = self.check_all("D1-before-any-move", self.leader(), [self.leader()])
        verdict = {"d0_clean": self.clean(d0), "d1_clean": all(self.read_clean(r) for r in d1), "steps": []}

        originals = set(self.hosts())
        new_replicas = []
        controls = list(d1)
        for step in (1, 2, 3):
            if new_replicas and not self.transfer(new_replicas[-1]):
                verdict["steps"].append({"step": step, "error": "leadership did not reach the last new replica"})
                break
            record = self.replace_one(step, originals)
            verdict["steps"].append(record)
            self.log("step-done", **record)
            if record.get("error"):
                break
            controls += self.check_all(f"S{step}-source-leads", self.leader(), [self.leader()])
            new = record["new_replica"]
            new_replicas.append(new)
            if not self.transfer(new):
                record["error"] = "leadership did not reach the new replica"
                break
            other = next(n for n in self.nodes if n != new)
            self.check_all(f"S{step}-new-replica-leads", new, [new, other])

        complete = len(new_replicas) == 3 and not any(s.get("error") for s in verdict["steps"])
        if complete:
            self.restart_all()
            time.sleep(a.settle)
            for node in reversed(new_replicas):
                if self.transfer(node):
                    self.check_all("after-restart", node, [node])
                else:
                    verdict.setdefault("errors", []).append(f"no leadership on {node} after restart")

        verdict["new_replicas"] = new_replicas
        verdict["hosts_end"] = self.hosts()
        verdict["controls_clean"] = all(self.read_clean(r) for r in controls)
        tests = [r for r in self.reads if r not in controls]
        verdict["tests"] = [{k: r.get(k) for k in ("label", "branch", "gateway", "leader_as_expected", "rows", "missing", "extra",
                                                    "changed", "identical", "marks_ok", "error")} for r in self.reads]
        verdict["overlap"] = [s["watch"]["overlap"] for s in verdict["steps"] if s.get("watch")]
        all_installed = complete and all(s.get("installed") for s in verdict["steps"])
        chained = complete and all(s.get("install_from_source") for s in verdict["steps"])
        verdict["all_installed"], verdict["chained"] = all_installed, chained
        verdict["tests_as_expected"] = all(r.get("leader_as_expected") for r in tests)

        wrong = [r for r in self.reads if r.get("error") is None and not self.read_clean(r)]
        refused = [r for r in self.reads if r.get("error")]
        verdict["wrong_reads"], verdict["refused_reads"] = len(wrong), len(refused)
        if wrong:
            verdict["verdict"] = "REPRODUCED" if verdict["d0_clean"] and verdict["d1_clean"] and verdict["controls_clean"] else "ANOMALY_NOT_ATTRIBUTED"
        elif not verdict["d0_clean"] or not verdict["d1_clean"] or not all_installed or not chained or not tests or not verdict["tests_as_expected"]:
            verdict["verdict"] = "VACUOUS"
        elif refused:
            verdict["verdict"] = "REFUSED"
        else:
            verdict["verdict"] = "NOT_REPRODUCED"
        self.finish(verdict)

    @staticmethod
    def read_clean(r):
        return r.get("error") is None and r.get("identical") is True and r.get("marks_ok") is True

    def finish(self, verdict):
        with open(os.path.join(self.dir, "verdict.json"), "w") as f:
            json.dump(verdict, f, indent=2)
        self.log("verdict", verdict=verdict["verdict"], wrong_reads=verdict["wrong_reads"], refused_reads=verdict["refused_reads"],
                 all_installed=verdict["all_installed"], chained=verdict["chained"], overlap=verdict["overlap"])
        print(verdict["verdict"], flush=True)


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--cluster", required=True, help="cluster name in the scenario; containers are <cluster>-camusN")
    p.add_argument("--timeline", required=True, help="the scenario run's timeline.jsonl")
    p.add_argument("--out", required=True, help="directory for events.jsonl, d0.json, verdict.json")
    p.add_argument("--nodes", type=int, default=6)
    p.add_argument("--base-port", type=int, default=15095)
    p.add_argument("--marks-at", type=int, default=45, help="seconds after the nemesis starts")
    p.add_argument("--fork-at", type=int, default=150, help="seconds after the nemesis starts")
    p.add_argument("--compact-wait", type=int, default=300, help="seconds after the fork for the log to stop reaching it; retention is 3 minutes")
    p.add_argument("--move-wait", type=int, default=900, help="seconds to wait for one replica to move")
    p.add_argument("--settle", type=int, default=45, help="seconds to let a replica catch up before reading")
    p.add_argument("--restart-gap", type=int, default=20, help="seconds between one node answering and the next restart")
    p.add_argument("--parent-as-of-budget", type=int, default=20)
    args = p.parse_args()
    try:
        ChainRun(args).run()
    except Exception as e:  # report the failure in the run's own record, then fail
        with open(os.path.join(args.out, "verdict.json"), "w") as f:
            json.dump({"verdict": "DRIVER_ERROR", "error": f"{type(e).__name__}: {e}"}, f, indent=2)
        print(f"DRIVER_ERROR {type(e).__name__}: {e}", file=sys.stderr, flush=True)
        raise


if __name__ == "__main__":
    main()
