#!/usr/bin/env python3
"""Does a snapshot install take away the history a database branch reads?

Hypothesis (from reading the code, Kahuna 1.16.1): a whole-partition install exports only ~CURRENT rows and its
purge removes the receiver's revision rows, so a replica that was installed has no revision at or before a branch's
forkT for any row the parent changed after the fork. While that replica leads, an as-of read reports those rows as
absent. The hold is still registered, so BranchSnapshotHoldGuard has nothing to refuse.

Runs beside a Caraxes scenario (bank load on `caraxes`, one data partition, leader balancer off) whose nemesis has
one short and, unless --no-long-kill, one long kill of the leader:

  fork under load, then write to the parent     frozen: the branch nobody writes
  D0  baseline, on the original leader
  short kill, leadership moved to that node     D1  negative control: a restarted node with its history
  long kill, wait for the install               D2  control: healthy leader
  leadership moved to the installed node        D3  the test
  leadership moved back                         D4  control: must equal D0 again

The A-B-A around D3 is what attributes a difference to the installed replica. The marks table makes the prediction
exact: rows untouched since the fork survive, rows the parent updated or deleted after the fork go wrong, rows it
inserted after the fork stay invisible.

Prints and writes verdict.json: REPRODUCED, NOT_REPRODUCED, or VACUOUS (the run did not reach the state under test).
"""
import argparse
import json
import os
import random
import re
import subprocess
import sys
import time
import urllib.error
import urllib.request

PARENT = "caraxes"
BRANCH = "frozen"
RETRYABLE = {"CADB0502", "CADB0503", "CADB0504"}
GROUP = 1000  # rows per marks group


def now():
    return time.strftime("%Y-%m-%dT%H:%M:%S%z")


class Run:
    def __init__(self, args):
        self.args = args
        self.nodes = [f"camus{i}" for i in range(1, args.nodes + 1)]
        self.port = {n: args.base_port + i for i, n in enumerate(self.nodes)}
        self.dir = args.out
        os.makedirs(self.dir, exist_ok=True)
        self.log_path = os.path.join(self.dir, "events.jsonl")
        self.t0 = None

    def log(self, event, **fields):
        offset = round(time.time() - self.t0, 1) if self.t0 else None
        record = {"ts": now(), "offset": offset, "event": event, **fields}
        with open(self.log_path, "a") as f:
            f.write(json.dumps(record) + "\n")
        print(json.dumps(record)[:600], flush=True)

    # ---- HTTP ----------------------------------------------------------------------------------------------------
    def http(self, node, path, body=None, timeout=30):
        data = json.dumps(body).encode() if body is not None else None
        req = urllib.request.Request(
            f"http://localhost:{self.port[node]}{path}", data=data, headers={"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=timeout) as r:
                return r.read().decode()
        except urllib.error.HTTPError as e:  # a refused statement answers with its envelope and a 4xx/5xx status
            return e.read().decode()

    @staticmethod
    def route(sql):
        word = re.match(r"\s*([A-Za-z]+)", sql).group(1).upper()
        if word in ("CREATE", "ALTER", "DROP"):
            return "/execute-sql-ddl"
        if word in ("INSERT", "UPDATE", "DELETE"):
            return "/execute-sql-non-query"
        return "/execute-sql-query"

    def sql_once(self, node, db, sql, timeout=300):
        return json.loads(self.http(node, self.route(sql), {"databaseName": db, "sql": sql}, timeout))

    def sql(self, node, db, sql, timeout=300, budget=180):
        """Retries retryable refusals and transport failures for `budget` seconds; raises on anything else."""
        deadline = time.time() + budget
        last = None
        while time.time() < deadline:
            try:
                answer = self.sql_once(node, db, sql, timeout)
            except (OSError, ValueError) as e:
                last = f"transport: {e}"
                time.sleep(2)
                continue
            if answer.get("status") == "ok":
                return answer
            last = f"{answer.get('code')} {answer.get('message')}"
            if answer.get("code") not in RETRYABLE:
                raise RuntimeError(f"{sql[:120]} on {node}/{db}: {last}")
            time.sleep(1)
        raise RuntimeError(f"{sql[:120]} on {node}/{db}: gave up after {budget} s: {last}")

    # ---- Cluster state -------------------------------------------------------------------------------------------
    def placement(self, node):
        try:
            return json.loads(self.http(node, "/v1/cluster/placement", timeout=10))
        except (OSError, ValueError):
            return None

    def leader(self):
        """The node leading the data partition, or None."""
        for node in self.nodes:
            p = self.placement(node)
            if not p:
                continue
            for part in p.get("partitions", []):
                if part.get("partitionId", 0) > 0 and part.get("leaderLocal"):
                    return node
        return None

    def endpoint(self, node):
        p = self.placement(node)
        return p.get("localEndpoint") if p else None

    def transfer(self, to, budget=300):
        """Moves the data partition's leadership to `to`, retrying while the target catches up."""
        deadline = time.time() + budget
        partition = 1
        attempts = 0
        while time.time() < deadline:
            current = self.leader()
            if current == to:
                self.log("transfer-done", to=to, attempts=attempts)
                return True
            endpoint = self.endpoint(to)
            if current is None or endpoint is None:
                time.sleep(2)
                continue
            attempts += 1
            body = json.dumps({"partitionId": partition, "targetEndpoint": endpoint})
            out = subprocess.run(
                ["docker", "run", "--rm", "--network", f"container:{self.args.cluster}-{current}",
                 "curlimages/curl:latest", "-sS", "-m", "30", "-X", "POST", "-H", "Content-Type: application/json",
                 "-d", body, "http://127.0.0.1:5095/v1/cluster/transfer-leadership"],
                capture_output=True, text=True)
            self.log("transfer-attempt", frm=current, to=to, attempt=attempts, answer=(out.stdout or out.stderr)[:200])
            time.sleep(3)
        self.log("transfer-failed", to=to, attempts=attempts)
        return False

    def install_count(self, node):
        """Installs this node's process has completed (the counter restarts with the process)."""
        try:
            text = self.http(node, "/metrics", timeout=15)
        except OSError:
            return None
        total = 0
        for line in text.splitlines():
            if line.startswith("raft_snapshot_install_duration_ms_milliseconds_count") and 'outcome="installed"' in line:
                total += int(float(line.rsplit(" ", 1)[1]))
        return total

    def floor_metrics(self, node):
        try:
            text = self.http(node, "/metrics", timeout=15)
        except OSError:
            return {}
        found = {}
        for line in text.splitlines():
            m = re.match(r"(kahuna_snapshot_floor_(?:live_holds|effective_floor_ms|missing_protected_version_total))[^ ]* (\S+)", line)
            if m:
                found[m.group(1)] = float(m.group(2))
        return found

    # ---- The nemesis's clock -------------------------------------------------------------------------------------
    def timeline(self):
        path = self.args.timeline
        if not os.path.exists(path):
            return []
        with open(path) as f:
            return [json.loads(line) for line in f if line.strip().endswith("}")]

    def wait_start(self):
        while True:
            for e in self.timeline():
                if e.get("phase") == "note" and e.get("kind") == "start":
                    # The file appears as the nemesis starts, so "now" is its clock to within this poll.
                    self.t0 = time.time()
                    self.log("nemesis-started")
                    return
            time.sleep(1)

    def wait_offset(self, seconds):
        while time.time() - self.t0 < seconds:
            time.sleep(1)

    def wait_kill_healed(self, ordinal):
        """Blocks until the ordinal-th kill (1-based) has healed; returns the node it hit."""
        while True:
            heals = [e for e in self.timeline() if e.get("phase") == "heal" and e.get("kind") == "kill"]
            if len(heals) >= ordinal:
                return heals[ordinal - 1]["target"]
            time.sleep(2)

    def wait_healthy(self, node, budget=300):
        deadline = time.time() + budget
        while time.time() < deadline:
            if self.placement(node):
                return True
            time.sleep(2)
        return False

    # ---- Marks: the exact prediction -----------------------------------------------------------------------------
    def seed_marks(self, gateway):
        self.sql(gateway, PARENT, "CREATE TABLE repro_marks (id INT64 PRIMARY KEY, v INT64 NOT NULL)")
        for start in range(0, 3 * GROUP, 100):
            values = ", ".join(f"({i}, 1)" for i in range(start, start + 100))
            self.sql(gateway, PARENT, f"INSERT INTO repro_marks (id, v) VALUES {values}")
        answer = self.sql(gateway, PARENT, "SELECT COUNT(*) FROM repro_marks")
        self.log("marks-seeded", rows=answer["rows"][0][0])

    def write_marks_after_fork(self, gateway):
        """Group 0 untouched; group 1 updated; group 2 deleted; group 3 inserted. Nothing here may reach the branch."""
        self.sql(gateway, PARENT, f"UPDATE repro_marks SET v = 2 WHERE id >= {GROUP} AND id < {2 * GROUP}")
        self.sql(gateway, PARENT, f"DELETE FROM repro_marks WHERE id >= {2 * GROUP} AND id < {3 * GROUP}")
        for start in range(3 * GROUP, 4 * GROUP, 100):
            values = ", ".join(f"({i}, 3)" for i in range(start, start + 100))
            self.sql(gateway, PARENT, f"INSERT INTO repro_marks (id, v) VALUES {values}")
        parent = self.sql(gateway, PARENT, "SELECT COUNT(*), SUM(v) FROM repro_marks")["rows"][0]
        self.log("marks-written-after-fork", parent_rows=parent[0], parent_sum=parent[1])

    def read_marks(self, gateway):
        rows = self.sql(gateway, BRANCH, "SELECT id, v FROM repro_marks")["rows"]
        seen = {int(r[0]): int(r[1]) for r in rows}
        groups = {"untouched": range(0, GROUP), "updated": range(GROUP, 2 * GROUP),
                  "deleted": range(2 * GROUP, 3 * GROUP), "inserted": range(3 * GROUP, 4 * GROUP)}
        result = {}
        for name, ids in groups.items():
            present = [i for i in ids if i in seen]
            result[name] = {"present": len(present), "v1": sum(1 for i in present if seen[i] == 1),
                            "other": sum(1 for i in present if seen[i] != 1)}
        rng = random.Random(7)
        points = {}
        for name, ids in groups.items():
            hits = 0
            sample = rng.sample(list(ids), 20)
            for i in sample:
                if self.sql(gateway, BRANCH, f"SELECT v FROM repro_marks WHERE id = {i}")["rows"]:
                    hits += 1
            points[name] = hits
        result["point_reads_found_of_20"] = points
        return result

    @staticmethod
    def marks_clean(marks):
        return (marks["untouched"]["v1"] == GROUP and marks["updated"]["v1"] == GROUP
                and marks["deleted"]["v1"] == GROUP and marks["inserted"]["present"] == 0
                and marks["point_reads_found_of_20"] == {"untouched": 20, "updated": 20, "deleted": 20, "inserted": 0})

    # ---- Digests -------------------------------------------------------------------------------------------------
    def scan_accounts(self, gateway, db=BRANCH):
        rows = self.sql(gateway, db, "SELECT id, balance, version FROM workload_accounts")["rows"]
        return {str(r[0]): (int(r[1]), int(r[2])) for r in rows}

    def digest(self, label, expected_leader, gateways=None, as_of_ms=None):
        """Reads the branch and compares it with D0. The leader is read before and after, because a digest taken
        while leadership moved says nothing about either replica."""
        gateways = gateways or [expected_leader]
        results = []
        for gateway in gateways:
            before = self.leader()
            started = time.time()
            try:
                accounts = self.scan_accounts(gateway)
                marks = self.read_marks(gateway)
                aggregate = self.sql(gateway, BRANCH, "SELECT COUNT(*), SUM(balance), SUM(version) FROM workload_accounts")["rows"][0]
                error = None
            except RuntimeError as e:
                accounts, marks, aggregate, error = None, None, None, str(e)
            after = self.leader()
            entry = {"label": label, "gateway": gateway, "leader_before": before, "leader_after": after,
                     "leader_as_expected": before == after == expected_leader,
                     "seconds": round(time.time() - started, 1), "error": error, "marks": marks, "aggregate": aggregate}
            if accounts is not None:
                entry["rows"] = len(accounts)
                if self.d0 is None:
                    self.d0 = accounts
                    with open(os.path.join(self.dir, "d0.json"), "w") as f:
                        json.dump(accounts, f)
                missing = [k for k in self.d0 if k not in accounts]
                extra = [k for k in accounts if k not in self.d0]
                changed = [k for k in accounts if k in self.d0 and accounts[k] != self.d0[k]]
                entry.update(missing=len(missing), extra=len(extra), changed=len(changed),
                             identical=not missing and not extra and not changed)
                entry["sample_missing"] = missing[:5]
                entry["sample_changed"] = [(k, self.d0[k], accounts[k]) for k in changed[:5]]
                if missing or changed:
                    self.explain(entry, gateway, missing, changed)
            if as_of_ms is not None:
                try:
                    sql = f"SELECT COUNT(*), SUM(balance) FROM workload_accounts AS OF SYSTEM TIME {as_of_ms}"
                    entry["parent_as_of"] = self.sql(gateway, PARENT, sql, budget=self.args.parent_as_of_budget)["rows"][0]
                except RuntimeError as e:
                    entry["parent_as_of_error"] = str(e)
            entry["floor"] = {n: self.floor_metrics(n) for n in self.nodes}
            self.log("digest", **entry)
            results.append(entry)
        return results

    def explain(self, entry, gateway, missing, changed):
        """The mechanism's prediction: every row that went wrong moved on in the parent after the fork."""
        try:
            parent = self.scan_accounts(gateway, PARENT)
        except RuntimeError as e:
            entry["explain_error"] = str(e)
            return
        moved_on = sum(1 for k in missing + changed if k in parent and parent[k][1] > self.d0[k][1])
        untouched_ok = sum(1 for k in self.d0 if k in parent and parent[k][1] == self.d0[k][1])
        entry["wrong_rows_parent_moved_on"] = moved_on
        entry["wrong_rows_total"] = len(missing) + len(changed)
        entry["rows_parent_never_touched_since_fork"] = untouched_ok

    # ---- The run -------------------------------------------------------------------------------------------------
    def run(self):
        self.d0 = None
        a = self.args
        self.log("waiting-for-nemesis", timeline=a.timeline)
        self.wait_start()

        self.wait_offset(a.marks_at)
        gateway = "camus1"
        self.seed_marks(gateway)

        self.wait_offset(a.fork_at)
        leader0 = self.leader()
        started = time.time()
        answer = self.sql(gateway, PARENT, f"CREATE DATABASE {BRANCH} BRANCH FROM {PARENT}")
        self.log("forked", seconds=round(time.time() - started, 2), leader=leader0, answer=answer.get("status"))
        branches = self.sql(gateway, PARENT, f"SHOW BRANCHES FROM {PARENT}")
        self.log("show-branches", columns=[c.get("name") for c in branches.get("columns", [])], rows=branches.get("rows"))
        as_of_ms = None
        fork_text = str(branches["rows"][0])
        m = re.search(r"HLC\((-?\d+):(\d+):(\d+)\)", fork_text)
        if m:
            as_of_ms = int(m.group(2))
        self.write_marks_after_fork(gateway)

        d0 = self.digest("D0-baseline", self.leader(), as_of_ms=as_of_ms)[0]
        clean_at_start = bool(d0["marks"]) and self.marks_clean(d0["marks"])
        self.log("baseline", marks_clean=clean_at_start, rows=d0.get("rows"), aggregate=d0.get("aggregate"))

        # Negative control: a restarted node that kept its history.
        victim1 = self.wait_kill_healed(1)
        self.wait_healthy(victim1)
        time.sleep(a.settle)
        installs1 = self.install_count(victim1)
        self.log("short-kill-healed", victim=victim1, installs=installs1)
        moved1 = self.transfer(victim1)
        d1 = self.digest("D1-restarted-no-install", victim1, as_of_ms=as_of_ms)[0] if moved1 else None
        installs1_after = self.install_count(victim1)

        verdict = {"baseline_marks_clean": clean_at_start, "d0_rows": d0.get("rows"),
                   "negative_control": {"victim": victim1, "installs": installs1_after, "moved": moved1,
                                        "identical": d1.get("identical") if d1 else None,
                                        "marks_clean": self.marks_clean(d1["marks"]) if d1 and d1["marks"] else None}}

        if a.no_long_kill:
            verdict["verdict"] = "PREFLIGHT"
            self.finish(verdict)
            return

        # The test.
        victim2 = self.wait_kill_healed(2)
        self.wait_healthy(victim2)
        healthy = self.leader()
        deadline = time.time() + a.install_wait
        installs2 = 0
        while time.time() < deadline:
            installs2 = self.install_count(victim2) or 0
            if installs2 > 0:
                break
            time.sleep(5)
        self.log("long-kill-healed", victim=victim2, installs=installs2, leader=healthy)
        time.sleep(a.settle)

        healthy = self.leader()
        d2 = self.digest("D2-healthy-leader", healthy, as_of_ms=as_of_ms)[0]
        moved = self.transfer(victim2)
        d3s = self.digest("D3-installed-leader", victim2, gateways=[victim2] + [n for n in self.nodes if n != victim2],
                          as_of_ms=as_of_ms) if moved else []
        back = self.transfer(healthy) if moved else False
        d4 = self.digest("D4-healthy-again", healthy, as_of_ms=as_of_ms)[0] if back else None

        def clean(d):
            return bool(d) and d.get("identical") is True and bool(d.get("marks")) and self.marks_clean(d["marks"])

        verdict.update({"victim": victim2, "healthy": healthy, "installs_on_victim": installs2, "moved_to_installed": moved,
                        "d2_clean": clean(d2), "d3": [{k: d.get(k) for k in ("gateway", "leader_as_expected", "rows", "missing", "extra", "changed", "identical", "marks", "parent_as_of", "parent_as_of_error", "error", "wrong_rows_parent_moved_on", "wrong_rows_total", "rows_parent_never_touched_since_fork")} for d in d3s],
                        "d4_clean": clean(d4)})
        if installs2 == 0 or not moved or not d3s or not all(d["leader_as_expected"] for d in d3s):
            verdict["verdict"] = "VACUOUS"
        elif all(clean(d) for d in d3s):
            verdict["verdict"] = "NOT_REPRODUCED"
        elif clean(d2) and clean(d4):
            verdict["verdict"] = "REPRODUCED"
        else:
            verdict["verdict"] = "ANOMALY_NOT_ATTRIBUTED"  # D3 wrong, but a control was wrong too
        self.finish(verdict)

    def finish(self, verdict):
        with open(os.path.join(self.dir, "verdict.json"), "w") as f:
            json.dump(verdict, f, indent=2)
        self.log("verdict", **verdict)


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--cluster", required=True, help="cluster name in the scenario; containers are <cluster>-camusN")
    p.add_argument("--timeline", required=True, help="the scenario run's timeline.jsonl")
    p.add_argument("--out", required=True, help="directory for events.jsonl, d0.json, verdict.json")
    p.add_argument("--nodes", type=int, default=3)
    p.add_argument("--base-port", type=int, default=15095)
    p.add_argument("--marks-at", type=int, default=45, help="seconds after the nemesis starts")
    p.add_argument("--fork-at", type=int, default=120, help="seconds after the nemesis starts")
    p.add_argument("--settle", type=int, default=45, help="seconds to let a restarted node catch up before digesting")
    p.add_argument("--install-wait", type=int, default=300, help="seconds to wait for the install after the long kill heals")
    p.add_argument("--parent-as-of-budget", type=int, default=180,
                   help="seconds to retry the parent AS OF read; a fixed Kahuna refuses it on an installed leader, so it can be short")
    p.add_argument("--no-long-kill", action="store_true", help="preflight: stop after the negative control")
    args = p.parse_args()
    try:
        Run(args).run()
    except Exception as e:  # report the failure in the run's own record, then fail
        with open(os.path.join(args.out, "verdict.json"), "w") as f:
            json.dump({"verdict": "DRIVER_ERROR", "error": f"{type(e).__name__}: {e}"}, f, indent=2)
        print(f"DRIVER_ERROR {type(e).__name__}: {e}", file=sys.stderr, flush=True)
        raise


if __name__ == "__main__":
    main()
