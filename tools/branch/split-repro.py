#!/usr/bin/env python3
"""Does a range split take away the history a database branch reads?

Hypothesis (from reading the code, Kahuna 1.16.2): a split copies the moving half with CopyRangeToPartitionAsync at one
snapshot timestamp, so the new partition holds one revision per key. A row the parent changed after a branch's fork
has no revision at or before forkT there, and an as-of read reports it as absent. Every replica of the new partition
is in the same state, so unlike an install no replica can answer, and leadership cannot route around it. RangeSplitter
and RangeMerger never read the snapshot holds, and the install's fence does not cover a split.

Runs beside a Caraxes scenario (bank load on `caraxes`, count-based auto-split with a threshold above both tables):

  seed repro_marks just under the threshold, filler first
  fork under load, then update and delete marks in the parent
  D0  baseline, every gateway
  wait through the split checker's interval
  D1  control: must equal D0, and the table must still be one range
  insert rows until the table is over the threshold, wait for the split
  D2  the test, every gateway

The checker samples the first 4,096 live keys of a range in key order, needs at least the threshold among them, so a
threshold above 4,096 never fires, and splits inside the sample: at the write centroid when it has a histogram, or at
the 50th or 75th percentile. Filler is inserted first and has the lowest primary keys, and at 3,300 rows it covers the
75th percentile of the largest sample (3,072), so the split point falls inside it in both the row space and the primary
index and every mark moves in both. Marks number only 100 per group so that the table stays under the threshold until
the driver crosses it. workload_accounts stays under the threshold and never moves: it is the control inside D2.

The prediction: untouched marks survive, updated and deleted marks vanish, inserted marks stay invisible, and the
parent's latest state stays right.

Storage is node-global and a split leaves the source's rows in place, so a replica of the new partition that also hosts
the source keeps every old revision. Three nodes at RF3 therefore cannot show the loss: run spl1 read everything. The
question is a new partition's replica that never hosted the source, which needs more nodes than the replication factor.
The count checker runs on the meta partition's leader and samples its own store, so it sees a range only when that node
hosts it (spp1: no split in 19 minutes with the meta leader elsewhere, the row split 39 s after it moved to a host). REST refuses
to move the meta partition, so the driver restarts its leader until an election lands on a host of the table's partition.
A read is served by the partition's leader, so after the split the driver moves each new partition's leadership to each
of its replicas and reads the branch through it (M1), then restarts every node and repeats that (M2) to rule out memory.

Prints and writes verdict.json: REPRODUCED (a read led by a fresh replica is wrong and the replicas that hosted the source
are right), NOT_REPRODUCED, ANOMALY_NOT_ATTRIBUTED, or VACUOUS (no split, the marks did not move, or no fresh replica).
"""
import argparse
import importlib.util
import json
import os
import random
import subprocess
import sys
import time

spec = importlib.util.spec_from_file_location(
    "install_repro", os.path.join(os.path.dirname(os.path.abspath(__file__)), "install-repro.py"))
install_repro = importlib.util.module_from_spec(spec)
spec.loader.exec_module(install_repro)

PARENT, BRANCH = install_repro.PARENT, install_repro.BRANCH
GROUP = 100
FILLER_BASE = 0
MARK_BASE = 1_000_000
EXTRA_BASE = 2_000_000
GROUPS = {"untouched": range(MARK_BASE, MARK_BASE + GROUP), "updated": range(MARK_BASE + GROUP, MARK_BASE + 2 * GROUP),
          "deleted": range(MARK_BASE + 2 * GROUP, MARK_BASE + 3 * GROUP),
          "inserted": range(MARK_BASE + 3 * GROUP, MARK_BASE + 4 * GROUP)}


class SplitRun(install_repro.Run):
    # ---- Seeding -------------------------------------------------------------------------------------------------
    def insert_range(self, gateway, base, count, value):
        for start in range(0, count, 100):
            ids = range(base + start, base + min(start + 100, count))
            values = ", ".join(f"({i}, {value})" for i in ids)
            self.sql(gateway, PARENT, f"INSERT INTO repro_marks (id, v) VALUES {values}")

    def seed(self, gateway):
        self.sql(gateway, PARENT, "CREATE TABLE repro_marks (id INT64 PRIMARY KEY, v INT64 NOT NULL)")
        self.insert_range(gateway, FILLER_BASE, self.args.filler, 0)
        self.insert_range(gateway, MARK_BASE, 3 * GROUP, 1)
        count = self.sql(gateway, PARENT, "SELECT COUNT(*) FROM repro_marks")["rows"][0][0]
        self.log("marks-seeded", rows=count)

    def write_after_fork(self, gateway):
        updated, deleted, inserted = GROUPS["updated"], GROUPS["deleted"], GROUPS["inserted"]
        self.sql(gateway, PARENT, f"UPDATE repro_marks SET v = 2 WHERE id >= {updated.start} AND id < {updated.stop}")
        self.sql(gateway, PARENT, f"DELETE FROM repro_marks WHERE id >= {deleted.start} AND id < {deleted.stop}")
        self.insert_range(gateway, inserted.start, GROUP, 3)
        self.log("marks-written-after-fork", parent=self.parent_marks(gateway))

    def parent_marks(self, gateway):
        sql = f"SELECT COUNT(*), SUM(v) FROM repro_marks WHERE id >= {MARK_BASE} AND id < {MARK_BASE + 4 * GROUP}"
        return self.sql(gateway, PARENT, sql)["rows"][0]

    def read_marks(self, gateway):
        """Every group through a full scan, which reads the row space, and a sample through point reads, which go through the primary index."""
        rows = self.sql(gateway, BRANCH, "SELECT id, v FROM repro_marks")["rows"]
        seen = {int(r[0]): int(r[1]) for r in rows}
        result = {"scan_rows": len(seen)}
        for name, ids in GROUPS.items():
            present = [i for i in ids if i in seen]
            result[name] = {"present": len(present), "v1": sum(1 for i in present if seen[i] == 1),
                            "other": sum(1 for i in present if seen[i] != 1)}
        rng = random.Random(7)
        points = {}
        for name, ids in GROUPS.items():
            points[name] = sum(1 for i in rng.sample(list(ids), 20)
                               if self.sql(gateway, BRANCH, f"SELECT v FROM repro_marks WHERE id = {i}")["rows"])
        result["point_reads_found_of_20"] = points
        return result

    @staticmethod
    def marks_clean(marks):
        return (marks["untouched"]["v1"] == GROUP and marks["updated"]["v1"] == GROUP
                and marks["deleted"]["v1"] == GROUP and marks["inserted"]["present"] == 0
                and marks["point_reads_found_of_20"] == {"untouched": 20, "updated": 20, "deleted": 20, "inserted": 0})

    # ---- Ranges --------------------------------------------------------------------------------------------------
    def row_spans(self, gateway, table="repro_marks"):
        """The table's row-space spans as this node routes them."""
        answer = self.sql(gateway, PARENT, f"SHOW RANGES FROM TABLE {table}")
        names = [c["name"] for c in answer["columns"]]
        return [dict(zip(names, row)) for row in answer["rows"]]

    def index_spans(self, gateway):
        answer = self.sql(gateway, PARENT, "SHOW RANGES FROM INDEX repro_marks@primary")
        names = [c["name"] for c in answer["columns"]]
        return [dict(zip(names, row)) for row in answer["rows"]]

    def locate(self, gateway, mark_id, relation="TABLE repro_marks"):
        """The partition holding the row with this primary key, or the error if the row is gone from the parent."""
        try:
            answer = self.sql(gateway, PARENT, f"SHOW RANGE FROM {relation} FOR ROW ({mark_id})")
        except RuntimeError as e:
            return {"error": str(e)[:160]}
        names = [c["name"] for c in answer["columns"]]
        row = dict(zip(names, answer["rows"][0]))
        return {"partition_id": row.get("partition_id"), "span": row.get("span"), "probe_key": row.get("probe_key")}

    # ---- Leadership ----------------------------------------------------------------------------------------------
    def replicas(self, partition):
        """The partition's replica endpoints in the committed map, or [] when no node answers."""
        for node in self.nodes:
            placement = self.placement(node)
            for part in (placement or {}).get("partitions", []):
                if part.get("partitionId") == partition:
                    return sorted(r["endpoint"] for r in part.get("replicas", []))
        return []

    def node_at(self, endpoint):
        return next((n for n in self.nodes if self.endpoint(n) == endpoint), None)

    def leader_of(self, partition):
        for node in self.nodes:
            placement = self.placement(node)
            for part in (placement or {}).get("partitions", []):
                if part.get("partitionId") == partition and part.get("leaderLocal"):
                    return node
        return None

    def transfer_partition(self, partition, to, budget=240):
        """Moves this partition's leadership to `to`, retrying while the target catches up. The route takes loopback only."""
        deadline = time.time() + budget
        attempts = 0
        while time.time() < deadline:
            current = self.leader_of(partition)
            if current == to:
                self.log("partition-transfer-done", partition=partition, to=to, attempts=attempts)
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
            self.log("partition-transfer-attempt", partition=partition, frm=current, to=to, attempt=attempts,
                     answer=(out.stdout or out.stderr)[:200])
            time.sleep(3)
        self.log("partition-transfer-failed", partition=partition, to=to, attempts=attempts)
        return False

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

    def ensure_meta_on(self, partition, attempts=8):
        """Restarts the meta leader until it hosts this partition. True when it does."""
        hosts = {self.node_at(e) for e in self.replicas(partition)}
        for _ in range(attempts):
            leader = self.meta_leader()
            if leader in hosts:
                self.log("meta-on-host", meta=leader, hosts=sorted(n for n in hosts if n))
                return True
            subprocess.run(["docker", "restart", f"{self.args.cluster}-{leader}"], capture_output=True, text=True)
            self.wait_healthy(leader)
            time.sleep(15)
            self.log("meta-restarted", restarted=leader, now=self.meta_leader(), hosts=sorted(n for n in hosts if n))
        return False

    def restart_all(self):
        """Restarts every node, one at a time, so nothing a read could use survives in memory."""
        for node in self.nodes:
            started = time.time()
            subprocess.run(["docker", "restart", f"{self.args.cluster}-{node}"], capture_output=True, text=True)
            healthy = self.wait_healthy(node)
            time.sleep(self.args.restart_gap)
            self.log("restarted", node=node, healthy=healthy, seconds=round(time.time() - started, 1))

    # ---- The matrix ----------------------------------------------------------------------------------------------
    def matrix(self, label, gateway, as_of_ms, row_partition, index_partition, source_replicas):
        """One read with each replica of each new partition leading. The other new partition stays led by a replica
        that hosted the source, when it has one, so that a wrong answer names the partition that gave it."""
        results = []
        for partition, other in ((row_partition, index_partition), (index_partition, row_partition)):
            if partition is None:
                continue
            if other is not None:
                keeper = [e for e in self.replicas(other) if e in source_replicas]
                if keeper and self.node_at(keeper[0]):
                    self.transfer_partition(other, self.node_at(keeper[0]))
            for endpoint in self.replicas(partition):
                node = self.node_at(endpoint)
                moved = bool(node) and self.transfer_partition(partition, node)
                entry = self.read(f"{label}-P{partition}-led-by-{node}", gateway, as_of_ms) if moved else {}
                results.append({"label": label, "partition": partition, "leader": node, "transferred": moved,
                                "fresh": endpoint not in source_replicas, "clean": bool(entry) and self.clean(entry),
                                "marks": entry.get("marks"), "accounts_identical": entry.get("accounts_identical"),
                                "branch_marks_total": entry.get("branch_marks_total"),
                                "parent_latest": entry.get("parent_latest"), "parent_as_of": entry.get("parent_as_of"),
                                "parent_as_of_error": entry.get("parent_as_of_error"), "floor": entry.get("floor"),
                                "marks_error": entry.get("marks_error")})
        return results

    # ---- Digests -------------------------------------------------------------------------------------------------
    def read(self, label, gateway, as_of_ms):
        started = time.time()
        entry = {"label": label, "gateway": gateway}
        try:
            entry["marks"] = self.read_marks(gateway)
        except RuntimeError as e:
            entry["marks"], entry["marks_error"] = None, str(e)
        try:
            accounts = self.scan_accounts(gateway)
        except RuntimeError as e:
            accounts, entry["accounts_error"] = None, str(e)
        if accounts is not None:
            if self.d0 is None:
                self.d0 = accounts
            missing = [k for k in self.d0 if k not in accounts]
            changed = [k for k in accounts if k in self.d0 and accounts[k] != self.d0[k]]
            entry.update(accounts_rows=len(accounts), accounts_missing=len(missing), accounts_changed=len(changed),
                         accounts_identical=not missing and not changed and len(accounts) == len(self.d0))
        try:
            entry["branch_marks_total"] = self.sql(gateway, BRANCH, "SELECT COUNT(*), SUM(v) FROM repro_marks")["rows"][0]
        except RuntimeError as e:
            entry["branch_total_error"] = str(e)
        try:
            entry["parent_latest"] = self.parent_marks(gateway)
        except RuntimeError as e:
            entry["parent_latest_error"] = str(e)
        if as_of_ms is not None:
            sql = f"SELECT COUNT(*), SUM(v) FROM repro_marks AS OF SYSTEM TIME {as_of_ms}"
            try:
                entry["parent_as_of"] = self.sql(gateway, PARENT, sql, budget=self.args.parent_as_of_budget)["rows"][0]
            except RuntimeError as e:
                entry["parent_as_of_error"] = str(e)
        entry["seconds"] = round(time.time() - started, 1)
        entry["floor"] = {n: self.floor_metrics(n) for n in self.nodes}
        self.log("digest", **entry)
        return entry

    def clean(self, entry):
        return (bool(entry.get("marks")) and self.marks_clean(entry["marks"])
                and entry.get("accounts_identical") is True)

    # ---- The run -------------------------------------------------------------------------------------------------
    def run(self):
        self.d0 = None
        a = self.args
        self.log("waiting-for-nemesis", timeline=a.timeline)
        self.wait_start()

        self.wait_offset(a.marks_at)
        gateway = "camus1"
        self.seed(gateway)
        spans_before = self.row_spans(gateway)
        self.log("spans-before-fork", spans=spans_before)
        source = spans_before[0].get("partition_id")
        meta_ready = self.ensure_meta_on(source)  # before the control, which proves nothing if the checker cannot see the range

        self.wait_offset(a.fork_at)
        started = time.time()
        answer = self.sql(gateway, PARENT, f"CREATE DATABASE {BRANCH} BRANCH FROM {PARENT}")
        self.log("forked", seconds=round(time.time() - started, 2), answer=answer.get("status"))
        branches = self.sql(gateway, PARENT, f"SHOW BRANCHES FROM {PARENT}")
        self.log("show-branches", rows=branches.get("rows"))
        m = install_repro.re.search(r"HLC\((-?\d+):(\d+):(\d+)\)", str(branches["rows"][0]))
        as_of_ms = int(m.group(2)) if m else None
        self.write_after_fork(gateway)

        d0s = [self.read("D0-baseline", n, as_of_ms) for n in self.nodes]
        probes = {"updated": GROUPS["updated"].start + 1, "untouched": GROUPS["untouched"].start + 1,
                  "inserted": GROUPS["inserted"].start + 1}
        self.log("located-before-split", where={k: self.locate(gateway, v) for k, v in probes.items()})

        time.sleep(a.control_wait)
        spans_control = self.row_spans(gateway)
        d1s = [self.read("D1-control-no-split", n, as_of_ms) for n in self.nodes]
        split_too_early = len(spans_control) != 1 or len(self.index_spans(gateway)) != 1
        self.log("control", spans=len(spans_control), split_too_early=split_too_early)

        # The test: over the threshold, then wait for the checker.
        meta_ready = self.ensure_meta_on(source) and meta_ready
        self.insert_range(gateway, EXTRA_BASE, a.extra, 4)
        self.log("threshold-crossed", rows=self.sql(gateway, PARENT, "SELECT COUNT(*) FROM repro_marks")["rows"][0][0])
        deadline = time.time() + a.split_wait
        spans_after, index_after = spans_control, []
        while time.time() < deadline:
            spans_after, index_after = self.row_spans(gateway), self.index_spans(gateway)
            if len(spans_after) > 1 and len(index_after) > 1:
                break
            time.sleep(5)
        self.log("spans-after", spans=spans_after, index_spans=index_after, seconds_left=round(deadline - time.time()))
        split = len(spans_after) > 1 and len(index_after) > 1
        accounts_spans = self.row_spans(gateway, "workload_accounts")
        self.log("accounts-spans", spans=accounts_spans)
        time.sleep(a.settle)

        relation = "INDEX repro_marks@primary"
        where = {k: self.locate(gateway, v) for k, v in probes.items()}
        where_index = {k: self.locate(gateway, v, relation) for k, v in probes.items()}
        self.log("located-after-split", where=where, where_index=where_index)

        moved = (all(where[k].get("partition_id") not in (None, source) for k in probes)
                 and all(where_index[k].get("partition_id") not in (None, source) for k in probes))
        row_partition = where["updated"].get("partition_id")
        index_partition = where_index["updated"].get("partition_id")
        source_replicas = self.replicas(source)
        fresh = {p: [e for e in self.replicas(p) if e not in source_replicas] for p in (row_partition, index_partition) if p}
        self.log("replicas-after-split", source=source, source_replicas=source_replicas,
                 row=self.replicas(row_partition), index=self.replicas(index_partition), fresh=fresh)

        reads = []
        if split and moved:
            reads = self.matrix("M1", gateway, as_of_ms, row_partition, index_partition, source_replicas)
            self.restart_all()
            reads += self.matrix("M2", gateway, as_of_ms, row_partition, index_partition, source_replicas)

        verdict = {"split": split, "split_too_early": split_too_early, "marks_moved": moved,
                   "accounts_spans": len(accounts_spans), "spans_after": len(spans_after), "index_spans_after": len(index_after),
                   "where_after": where, "where_index_after": where_index, "source_replicas": source_replicas, "fresh": fresh,
                   "d0_clean": all(self.clean(d) for d in d0s), "d1_clean": all(self.clean(d) for d in d1s),
                   "meta_ready": meta_ready, "reads": reads}
        fresh_reads = [r for r in reads if r["fresh"]]
        kept_reads = [r for r in reads if not r["fresh"]]
        if (not split or split_too_early or not moved or not verdict["d0_clean"] or not verdict["d1_clean"]
                or not any(r["transferred"] for r in fresh_reads)):
            verdict["verdict"] = "VACUOUS"
        elif all(r["clean"] for r in reads if r["transferred"]):
            verdict["verdict"] = "NOT_REPRODUCED"
        elif (any(r["transferred"] and not r["clean"] and r["marks"] and r["accounts_identical"] for r in fresh_reads)
              and all(r["clean"] for r in kept_reads if r["transferred"])):
            verdict["verdict"] = "REPRODUCED"  # wrong only where the leader never hosted the source
        else:
            verdict["verdict"] = "ANOMALY_NOT_ATTRIBUTED"
        self.finish(verdict)


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--cluster", required=True, help="cluster name in the scenario; containers are <cluster>-camusN")
    p.add_argument("--timeline", required=True, help="the scenario run's timeline.jsonl")
    p.add_argument("--out", required=True, help="directory for events.jsonl, verdict.json")
    p.add_argument("--nodes", type=int, default=3)
    p.add_argument("--base-port", type=int, default=15095)
    p.add_argument("--marks-at", type=int, default=45, help="seconds after the nemesis starts")
    p.add_argument("--fork-at", type=int, default=150, help="seconds after the nemesis starts")
    p.add_argument("--filler", type=int, default=3300, help="rows sorted before the marks; must cover the 75th percentile of the checker's sample")
    p.add_argument("--extra", type=int, default=1000, help="rows inserted to cross the split threshold")
    p.add_argument("--control-wait", type=int, default=150, help="seconds to give the checker a chance to split too early")
    p.add_argument("--split-wait", type=int, default=480, help="seconds to wait for the split")
    p.add_argument("--settle", type=int, default=30, help="seconds between the split and the digest")
    p.add_argument("--restart-gap", type=int, default=20, help="seconds between one node answering and the next restart")
    p.add_argument("--parent-as-of-budget", type=int, default=20)
    args = p.parse_args()
    try:
        SplitRun(args).run()
    except Exception as e:  # report the failure in the run's own record, then fail
        with open(os.path.join(args.out, "verdict.json"), "w") as f:
            json.dump({"verdict": "DRIVER_ERROR", "error": f"{type(e).__name__}: {e}"}, f, indent=2)
        print(f"DRIVER_ERROR {type(e).__name__}: {e}", file=sys.stderr, flush=True)
        raise


if __name__ == "__main__":
    main()
