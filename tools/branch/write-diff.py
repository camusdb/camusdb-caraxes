#!/usr/bin/env python3
"""Does a branch behave like a plain database holding the same data?

A branch's writes go through overlay, tombstone and ancestor-lookup paths a root database never uses. So: seed root `twin`
and the parent `caraxes`-side database `source` identically, fork `branch` from `source`, then send the same random
statements to `branch` and `twin`. Any difference in the outcome or the data is a bug in one of them. Meanwhile the
parent takes its own different writes, none of which may show up in the branch.

The statements aim at what differs on a branch: re-inserting deleted keys over a tombstone, unique keys that collide with
a row inherited from the ancestor, foreign keys whose parent row is inherited or deleted, ON CONFLICT against inherited and
tombstoned rows, values above the large-value threshold, multi-row and range writes, and NULLs in unique columns.

After --reseed-every statements `fork2` is forked from `fork1`, and `twin2` is seeded as a copy of `twin`, and both get
the statements from then on too, so the same paths run through a grandchild.

Each statement's outcome is compared by status, by the affected-row count and by error code. Every --compare-every statements,
and at the end, all tables are read in full and through point, unique-index and range queries. A retryable refusal is retried,
so it cannot be mistaken for a difference. Prints and writes verdict.json: DIFFERENCES (at least one), or NO_DIFFERENCE, and
differences.jsonl with the statement list needed to replay any of them. Same --seed, same statements.
"""
import argparse
import hashlib
import importlib.util
import json
import os
import random
import sys
import time

here = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("install_repro", os.path.join(here, "install-repro.py"))
install_repro = importlib.util.module_from_spec(spec)
spec.loader.exec_module(install_repro)

RETRYABLE = install_repro.RETRYABLE
LARGE = 9000  # characters; above the 2,048-byte large-value threshold, and below the sizes that slow a run down
TABLES = {
    "parents": ("id", ["code", "payload"]),
    "children": ("id", ["parent_id", "email", "n", "note"]),
}
DDL = [
    "CREATE TABLE parents (id INT64 PRIMARY KEY NOT NULL, code STRING, payload STRING, UNIQUE KEY parents_code (code))",
    "CREATE TABLE children (id INT64 PRIMARY KEY NOT NULL, parent_id INT64 REFERENCES parents (id), email STRING, n INT64, note STRING, UNIQUE KEY children_email (email))",
    "CREATE INDEX children_n ON children (n)",
]


class Generator:
    """Random statements over small key ranges, so deletes, re-inserts and collisions keep happening."""

    def __init__(self, seed, keys):
        self.rng = random.Random(seed)
        self.keys = keys

    def key(self):
        return self.rng.randrange(self.keys)

    def code(self):
        return f"'c{self.rng.randrange(self.keys)}'"

    def email(self):
        return "NULL" if self.rng.random() < 0.05 else f"'e{self.rng.randrange(self.keys)}@x'"

    def payload(self):
        roll = self.rng.random()
        if roll < 0.15:
            return "'" + chr(97 + self.rng.randrange(26)) * LARGE + "'"
        if roll < 0.2:
            return "NULL"
        return f"'p{self.rng.randrange(1000)}'"

    def note(self):
        return "'" + chr(97 + self.rng.randrange(26)) * LARGE + "'" if self.rng.random() < 0.1 else f"'n{self.rng.randrange(100)}'"

    def parent_ref(self):
        roll = self.rng.random()
        return "NULL" if roll < 0.1 else str(self.key())

    def statement(self):
        r = self.rng.random()
        k, j = self.key(), self.key()
        lo, hi = min(k, j), min(max(k, j), min(k, j) + 8)
        ops = [
            (0.14, lambda: f"INSERT INTO parents (id, code, payload) VALUES ({k}, {self.code()}, {self.payload()})"),
            (0.14, lambda: f"INSERT INTO children (id, parent_id, email, n, note) VALUES ({k}, {self.parent_ref()}, {self.email()}, {self.rng.randrange(50)}, {self.note()})"),
            (0.06, lambda: f"INSERT INTO children (id, parent_id, email, n, note) VALUES ({k}, {self.parent_ref()}, {self.email()}, 1, 'm1'), ({j + self.keys}, {self.parent_ref()}, {self.email()}, 2, 'm2')"),
            (0.08, lambda: f"INSERT INTO parents (id, code, payload) VALUES ({k}, {self.code()}, {self.payload()}) ON CONFLICT (id) DO UPDATE SET payload = excluded.payload"),
            (0.05, lambda: f"INSERT INTO children (id, parent_id, email, n, note) VALUES ({k}, {self.parent_ref()}, {self.email()}, 1, 'oc') ON CONFLICT (id) DO UPDATE SET n = children.n + excluded.n"),
            (0.05, lambda: f"INSERT INTO children (id, parent_id, email, n, note) VALUES ({k}, {self.parent_ref()}, {self.email()}, 1, 'oc') ON CONFLICT (email) DO UPDATE SET n = children.n + 1"),
            (0.05, lambda: f"INSERT INTO parents (id, code, payload) VALUES ({k}, {self.code()}, 'dn') ON CONFLICT DO NOTHING"),
            (0.09, lambda: f"UPDATE parents SET payload = {self.payload()} WHERE id = {k}"),
            (0.05, lambda: f"UPDATE parents SET code = {self.code()} WHERE id = {k}"),
            (0.05, lambda: f"UPDATE children SET email = {self.email()}, n = {self.rng.randrange(50)} WHERE id = {k}"),
            (0.03, lambda: f"UPDATE children SET parent_id = {self.parent_ref()} WHERE id = {k}"),
            (0.03, lambda: f"UPDATE children SET n = n + 1 WHERE n >= {self.rng.randrange(50)} AND n < {self.rng.randrange(50)}"),
            (0.03, lambda: f"UPDATE parents SET id = {j} WHERE id = {k}"),
            (0.09, lambda: f"DELETE FROM parents WHERE id = {k}"),
            (0.09, lambda: f"DELETE FROM children WHERE id = {k}"),
            (0.03, lambda: f"DELETE FROM children WHERE id >= {lo} AND id < {hi}"),
            (0.03, lambda: f"DELETE FROM parents WHERE id >= {lo} AND id < {hi}"),
        ]
        total = 0.0
        for weight, make in ops:
            total += weight
            if r <= total:
                return make()
        return ops[-1][1]()

    def queries(self):
        """Reads whose answers both databases must agree on, in a stable order."""
        out = ["SELECT id, code, payload FROM parents ORDER BY id",
               "SELECT id, parent_id, email, n, note FROM children ORDER BY id",
               "SELECT COUNT(*), SUM(n) FROM children",
               "SELECT c.id, p.code FROM children c JOIN parents p ON c.parent_id = p.id ORDER BY c.id"]
        for _ in range(6):
            k = self.key()
            out.append(f"SELECT id, code FROM parents WHERE id = {k}")
            out.append(f"SELECT id, email FROM children WHERE id = {k}")
            out.append(f"SELECT id FROM parents WHERE code = 'c{k}'")
            out.append(f"SELECT id FROM children WHERE email = 'e{k}@x'")
            out.append(f"SELECT id, n FROM children WHERE n >= {k % 50} AND n < {k % 50 + 5} ORDER BY id")
        return out


class Diff(install_repro.Run):
    def execute(self, node, db, sql):
        """(status, code, count, rows) after retrying retryable refusals. The same for both sides or it is a difference."""
        deadline = time.time() + self.args.statement_budget
        while True:
            try:
                answer = self.sql_once(node, db, sql, 60)
            except (OSError, ValueError) as e:
                if time.time() > deadline:
                    return ("transport", None, None, str(e)[:200])
                time.sleep(1)
                continue
            if answer.get("status") == "ok":
                rows = answer.get("rows")  # a write answers with its modified-row count, a query with its rows
                return ("ok", None, rows, None) if isinstance(rows, int) else ("ok", None, None, rows)
            code = answer.get("code")
            if code in RETRYABLE and time.time() < deadline:
                time.sleep(0.5)
                continue
            return ("error", code, None, str(answer.get("message"))[:240])

    def note_difference(self, kind, pair, idx, sql, left, right):
        record = {"kind": kind, "pair": pair, "statement_index": idx, "sql": sql[:300], "left": self.short(left), "right": self.short(right)}
        self.differences.append(record)
        with open(os.path.join(self.dir, "differences.jsonl"), "a") as f:
            f.write(json.dumps(record) + "\n")
        self.log("difference", **record)

    @staticmethod
    def short(result):
        text = json.dumps(result)
        return text if len(text) < 600 else text[:300] + f"... sha={hashlib.sha256(text.encode()).hexdigest()[:12]} len={len(text)}"

    def compare_reads(self, idx):
        for sql in self.gen.queries():
            for left, right in self.pairs:
                a, b = self.execute(self.next_node(), left, sql), self.execute(self.next_node(), right, sql)
                self.reads += 1
                if self.comparable(a) != self.comparable(b):
                    self.note_difference("read", f"{left}/{right}", idx, sql, a, b)

    @staticmethod
    def comparable(result):
        """Error messages name the database, so only the code of a refusal is compared."""
        return result if result[0] == "ok" else result[:3]

    def next_node(self):
        self.node_i += 1
        return self.nodes[self.node_i % len(self.nodes)]

    def seed_database(self, gateway, db, seed):
        self.sql(gateway, "caraxes", f"CREATE DATABASE {db}")
        for sql in DDL:
            self.sql(gateway, db, sql)
        gen = Generator(seed, self.args.keys)
        self.apply_seed(gateway, db, gen)

    def apply_seed(self, gateway, db, gen):
        """Half the key range, so a statement finds a row about half the time. Large values mix in here too."""
        for k in range(0, self.args.keys, 2):
            self.sql(gateway, db, f"INSERT INTO parents (id, code, payload) VALUES ({k}, 'c{k}', {gen.payload()})")
        for k in range(0, self.args.keys, 2):
            self.sql(gateway, db, f"INSERT INTO children (id, parent_id, email, n, note) VALUES ({k}, {k}, 'e{k}@x', {k % 50}, {gen.note()})")

    def copy_database(self, gateway, source, target):
        """A plain root with the same rows as `source`, by reading them and inserting them."""
        self.sql(gateway, "caraxes", f"CREATE DATABASE {target}")
        for sql in DDL:
            self.sql(gateway, target, sql)
        rows = self.sql(gateway, source, "SELECT id, code, payload FROM parents")["rows"]
        for r in rows:
            self.sql(gateway, target, f"INSERT INTO parents (id, code, payload) VALUES ({int(r[0])}, {self.text(r[1])}, {self.text(r[2])})")
        rows = self.sql(gateway, source, "SELECT id, parent_id, email, n, note FROM children")["rows"]
        for r in rows:
            self.sql(gateway, target, f"INSERT INTO children (id, parent_id, email, n, note) VALUES ({int(r[0])}, {self.number(r[1])}, {self.text(r[2])}, {self.number(r[3])}, {self.text(r[4])})")

    @staticmethod
    def number(v):
        return "NULL" if v is None else str(int(v))

    @staticmethod
    def text(v):
        return "NULL" if v is None else "'" + str(v).replace("'", "''") + "'"

    def run(self):
        a = self.args
        self.differences, self.reads, self.node_i = [], 0, 0
        self.log("waiting-for-nemesis", timeline=a.timeline, seed=a.seed)
        self.wait_start()
        self.wait_offset(a.start_at)
        gateway = "camus1"

        self.seed_database(gateway, "source", a.seed + 1)
        self.seed_database(gateway, "twin", a.seed + 1)
        self.sql(gateway, "source", "CREATE DATABASE fork1 BRANCH FROM source")
        self.log("seeded", keys=a.keys)
        self.pairs = [("fork1", "twin")]
        self.gen = Generator(a.seed, a.keys)
        noise = Generator(a.seed + 99, a.keys)
        self.compare_reads(-1)
        baseline_differences = len(self.differences)

        outcome_counts, outcome_errors = {}, {}
        started = time.time()
        idx = 0
        for idx in range(a.statements):
            sql = self.gen.statement()
            for left, right in self.pairs:
                x, y = self.execute(self.next_node(), left, sql), self.execute(self.next_node(), right, sql)
                if x[:3] != y[:3]:
                    self.note_difference("statement", f"{left}/{right}", idx, sql, x, y)
                if x[0] == "ok":
                    outcome_counts[sql.split()[0]] = outcome_counts.get(sql.split()[0], 0) + 1
                else:
                    outcome_errors[x[1]] = outcome_errors.get(x[1], 0) + 1
            self.execute(self.next_node(), "source", noise.statement())  # the parent moves on, and the branch must not notice
            if (idx + 1) % a.compare_every == 0:
                self.compare_reads(idx)
                self.log("progress", statements=idx + 1, differences=len(self.differences), reads=self.reads, seconds=round(time.time() - started),
                         outcomes=outcome_counts, errors=outcome_errors)
                if len(self.differences) >= a.max_differences:
                    self.log("stopping-at-max-differences", differences=len(self.differences))
                    break
            if idx + 1 == a.reseed_every:
                self.sql(gateway, "fork1", "CREATE DATABASE fork2 BRANCH FROM fork1")
                self.copy_database(gateway, "twin", "twin2")
                self.pairs.append(("fork2", "twin2"))
                self.log("grandchild-added", at=idx + 1)
        self.compare_reads(idx)

        verdict = {"statements": idx + 1, "reads_compared": self.reads, "differences": len(self.differences),
                   "baseline_differences": baseline_differences, "outcomes": outcome_counts, "errors": outcome_errors,
                   "seed": a.seed, "first_differences": self.differences[:10], "seconds": round(time.time() - started),
                   "verdict": "DIFFERENCES" if self.differences else "NO_DIFFERENCE"}
        with open(os.path.join(self.dir, "verdict.json"), "w") as f:
            json.dump(verdict, f, indent=2)
        self.log("verdict", verdict=verdict["verdict"], statements=verdict["statements"], differences=verdict["differences"], outcomes=outcome_counts, errors=outcome_errors)
        print(verdict["verdict"], flush=True)


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--cluster", required=True, help="cluster name in the scenario; containers are <cluster>-camusN")
    p.add_argument("--timeline", required=True, help="the scenario run's timeline.jsonl")
    p.add_argument("--out", required=True, help="directory for events.jsonl, differences.jsonl and verdict.json")
    p.add_argument("--nodes", type=int, default=5)
    p.add_argument("--base-port", type=int, default=15095)
    p.add_argument("--seed", type=int, default=1)
    p.add_argument("--keys", type=int, default=200, help="key range; small, so statements keep colliding")
    p.add_argument("--statements", type=int, default=6000)
    p.add_argument("--compare-every", type=int, default=200)
    p.add_argument("--reseed-every", type=int, default=3000, help="add the grandchild pair after this many statements")
    p.add_argument("--max-differences", type=int, default=40)
    p.add_argument("--statement-budget", type=int, default=60)
    p.add_argument("--start-at", type=int, default=60, help="seconds after the nemesis starts")
    args = p.parse_args()
    try:
        Diff(args).run()
    except Exception as e:  # report the failure in the run's own record, then fail
        with open(os.path.join(args.out, "verdict.json"), "w") as f:
            json.dump({"verdict": "DRIVER_ERROR", "error": f"{type(e).__name__}: {e}"}, f, indent=2)
        print(f"DRIVER_ERROR {type(e).__name__}: {e}", file=sys.stderr, flush=True)
        raise


if __name__ == "__main__":
    main()
