# Caraxes

[![nightly-append](https://github.com/camusdb/camusdb-caraxes/actions/workflows/nightly-append.yml/badge.svg)](https://github.com/camusdb/camusdb-caraxes/actions/workflows/nightly-append.yml)

Jepsen-style reliability/chaos testing harness for [CamusDB](../camusdb). Caraxes orchestrates a
Docker cluster of CamusDB nodes from a declarative YAML spec, drives SQL load through
`CamusDB.Workload`, injects faults (kills, partitions, latency, membership changes), and renders a
verdict from the run's artifacts.

Standard test posture: **replication factor 3, leader balancer on**.

## Requirements

- .NET 10 SDK
- Docker with `docker compose`
- A CamusDB checkout (default `~/camusdb`) — the image is built from its `docker/Dockerfile`

## Usage

```bash
# bring up a 3-node RF=3 cluster, wait until every node is ready, print status
dotnet run --project Caraxes -- up --spec scenarios/cluster-3.yml

# health + placement table
dotnet run --project Caraxes -- status --spec scenarios/cluster-3.yml

# one node's logs
dotnet run --project Caraxes -- logs --spec scenarios/cluster-3.yml --node camus2

# tear down (containers, network, volumes)
dotnet run --project Caraxes -- down --spec scenarios/cluster-3.yml

# run a full scenario: cluster up -> seed -> workload -> verdict -> teardown
dotnet run --project Caraxes -- run --scenario scenarios/smoke-optimistic.yml
```

`run` stands the cluster up, seeds the dataset with `CamusDB.Workload init`, drives the measured
`run`, collects its artifacts under `runs/scenarios/<name>/artifacts/`, writes a `scenario.json`
verdict, and (unless `teardown: false`) tears the cluster down. It exits 0 on PASS, 1 on FAIL. The
workload runs in a one-shot container attached to the cluster's Docker network, so it reaches nodes
over TLS by their in-cluster DNS names without any host-side certificate trust changes.

A scenario file embeds a `cluster:` block (the same keys as a cluster spec) and a `workload:` block
(`rows`, `mode` open/closed, `duration`, `warmup`, `workers`, `target_ops`, `read_percent` /
`write_percent`, `locking`, `isolation`, `expect_faults`, …). Workload `locking`/`isolation` inherit
the cluster's when left blank. See `scenarios/smoke-*.yml`.

### Nemesis (fault injection)

A scenario may add a `nemesis:` block that drives faults concurrently with the workload, writing a
`timeline.jsonl` of every injection and heal (each stamped with an offset from the workload's start,
so a later verdict stage can correlate faults with the workload's per-second `intervals.csv`).

Fault kinds: `kill` (SIGKILL, restart on heal), `stop` (graceful), `pause` (SIGSTOP freeze), `backup` (an online
backup on the node, see below), `add-node` (boot a replacement that joins the cluster, seeded from a backup, see below),
`partition` (iptables-isolate a node from all peers), `slow` / `loss` (tc netem latency / packet
loss), `fill-disk` (exhaust a node's `/data` until ENOSPC — needs a size-capped tmpfs data mount, see
below), `slow-disk` (cap one node's block writes with cgroup `io.max` — a slow or paused device, see
below), `clock-skew` (move one node's wall clock forward or back, see below), `remove-node`
(drain via `/v1/cluster/leave`, a one-way scale-down), and `sql` (an online schema change or bulk write, see
below). Targets are a node name, `random` (seeded, reproducible), or `zone:<name>` (every node in a failure zone — the fault
applies to each, so `kill` of a zone kills the whole zone together). Every healable fault still in
effect when the workload finishes is healed on the way out, so the cluster is never left broken.

**Crash dumps.** Every node runs with the runtime's minidump enabled, written to `/dumps`, which is a
per-node host bind mount under the cluster's run directory (`runs/clusters/<name>/dumps/camusN/`). It
is a bind mount rather than a named volume so a dump survives the teardown's `down -v`, and it is
deliberately not under `/data`: on a tmpfs-backed rig `/data` vanishes with the container, which is how
the Kahuna 1.7.7 OOM dumps were lost. `%p-%t` in the name keeps every dump of a crash loop.

**Disk faults.** `fill-disk` needs a size cap to be meaningful, so set `data_tmpfs_mb: <MiB>` on the
cluster — each node's `/data` then becomes a RAM-backed, size-capped tmpfs the fault can exhaust. This
is for disk-pressure behavior, not durability (tmpfs is lost on restart). tmpfs pages are charged to
the container's memory cgroup, so a tmpfs-backed node needs a `memory_limit_mb` large enough for
process plus data; pair it with `gc_heap_hard_limit_mb` (the heap budget a 4096 MiB limit derives is
2458 MiB) so the larger container does not also inflate the heap and CamusDB's proportional caches.

**In-window scan probe.** `workload.scan_probe_interval: 5s` (and optional `scan_probe_timeout` seconds) makes the
measured run issue `SELECT COUNT(*)` on every gateway at that interval over read-only, routing-pinned connections and
fail the run's validity on any short count — the check a post-load reconciliation can never make (CamusDB feature
e31cf9bc). `tools/p3c/count-probe.sh` is the manual, REST-based equivalent for a held cluster.

**Steady device regime.** `precondition_device_gb: <GiB>` (top level) streams that much fsynced ballast
to the device backing the run directory during the workload's warm-up and records `precondition.json`.
The benchmark host's NVMe serves the first ~30-35 GB of a run from a write cache and then changes
regime (fsync 0.4 → 1.9 ms), folding back only in idle gaps; filling the cache right before the measured
window puts every run in the same steady regime from its first second. `p3c regime` reports it and refuses
a run whose ballast finished after `measureStartUtc`. Preconditioned, unpreconditioned and tmpfs runs are
three different denominators; compare within one kind. See `scenarios/disk-full.yml` and
`scenarios/zone-failure.yml`.

**Slow or paused device (`slow-disk`).** Caps one node's block I/O with cgroup v2 `io.max` on the
disk that backs docker's named volumes, so every durable write the node issues — WAL appends, SST
flushes and the fsyncs behind them — waits at the block layer while the process, its network and its
peers stay healthy. An fsync then takes *bytes ÷ write_bps*: the default `write_bps: 4096` is a device
pause (one 4 KiB write per second; a node writing tens of KiB per operation stalls for the whole hold),
a few MiB/s is a merely slow disk. `write_iops` and `read_bps` cap I/O operations and reads the same
way. The cap is applied and lifted through the Docker Engine's container-update API (the per-device
throttle resources the CLI does not expose; docker-group membership is enough, no sudo), which rewrites
the container's own `io.max` line in place, and every write is read back from the host's cgroup tree to
confirm it took. **Nothing is created or removed while a node is throttled, and nothing may be:**
removing any container unmounts its overlay, which syncs the whole upper filesystem and waits behind the
throttled node's writeback; that stuck removal holds the daemon's layer lock, every later `docker
run`/`create` hangs, and a heal that needs one can never run (run `sd1`, 2026-09-14, wedged the host
for eighteen minutes that way before the update API released it). `exec`, `inspect`, `kill` and the
update API stay usable under a cap. Needs a **volume-backed** data mount: on a `data_tmpfs_mb` rig the
writes never reach a block device and the fault refuses to inject. The disk is derived from
`/proc/self/mountinfo` and `/sys/dev/block` (a partition is walked up to its disk, which is what
`io.max` keys on); `device: "MAJ:MIN"` overrides it on a host where that fails. This is the on-demand
form of the natural fsync pauses the benchmark host's NVMe produces under sustained load (CamusDB
`b83c72db`), targeted at a single node. `dm-delay` / `dm-flakey` corruption faults remain a follow-up
because they need root on the host. See `scenarios/bank-slow-disk-pause-nvme.yml`.

```yaml
nemesis:
  events:
    - { at: 4m, fault: slow-disk, target: camus1, duration: 30s }              # pause
    - { at: 7m, fault: slow-disk, target: camus2, duration: 60s, write_bps: 2097152 }  # 2 MiB/s
```

**Clock skew (`clock-skew`).** Moves one node's wall clock by `offset_ms` (positive is ahead, negative
is behind; default +10 s, limit ±12 h) and moves it back to real time on heal. Containers share the host
kernel's clock, so a node's clock cannot be set; instead, with `clock_skew: true` on the cluster,
Caraxes layers a small shim (`tools/clockskew/skew.c`) on the node image as `<image>-clockskew` and
preloads it (`LD_PRELOAD`) into every node. The shim shifts `CLOCK_REALTIME`, `gettimeofday` and `time`
by the offset in the node's `/tmp/caraxes-skew` and leaves monotonic time, sleeps and timers alone.
libfaketime is not used: under .NET 10 it made `Thread.Sleep` and timed waits return at once. Each
inject measures the node's skew back and fails if the shim is not active; `docker exec <node> date`
prints the node's skewed time.

Only the wall clock moves, so this fault reaches CamusDB's HLC (Kommander `HybridLogicalClock` reads
`DateTimeOffset.UtcNow`) and the expiries built on it, and not Raft, whose elections and leases use
monotonic ticks. The HLC takes the highest clock it hears and persists a floor per partition, so a
forward jump on one node moves the whole cluster's HLC forward and stays after the heal. What each size
reaches: more than 5 s ahead skips the snapshot clock fence (`append-clock-jump.yml`); more than 15 s
expires write intents and locks in flight (`append-clock-jump-locks.yml`); minutes reach the wall-clock
gossip liveness and the 30 s schema-ack lease (`append-clock-jump-liveness.yml`).
`append-clock-skew-small.yml` (±2 s) is the control. A node set further back than the development
certificate's `notBefore` rejects its peers' certificates, which is why CamusDB's
`docker/certs/generate.sh` backdates it one day.

```yaml
cluster:
  clock_skew: true
nemesis:
  events:
    - { at: 15s, fault: clock-skew, target: camus1, duration: 20s, offset_ms: 10000 }   # 10 s ahead
    - { at: 45s, fault: clock-skew, target: camus2, duration: 20s, offset_ms: -2000 }   # 2 s behind
```

Two schedule forms — an explicit `events:` timeline or a seeded `random:` soak:

```yaml
nemesis:
  seed: 7
  events:
    - { at: 20s, fault: kill, target: random, duration: 20s }
```

`target: leader` is resolved when the event fires, from the `LeaderLocal` flags each node reports on
its placement: the node leading the most partitions is hit. If no node leads anything at that moment
(an election is in progress, which is common right after a WAL-stall step-down on a saturated
device), the runner keeps sweeping every 0.5 s for up to 10 s before it gives up on the fault, and
the timeline note records how many sweeps it took.

See `scenarios/kill-follower.yml`, `partition-and-slow.yml`, `soak-random.yml`. The verdict grades
internal errors by context: with no fault injected any internal error fails the run, but under a
fault a few client-side disposal races are tolerated as long as reconciliation (the real consistency
guard) still holds.

### Fault correlation and resilience checks

For a run with faults, Caraxes correlates the `timeline.jsonl` fault windows with the workload's
per-second `intervals.csv` (aligned by the wall-clock anchor the workload writes to `run-meta.json`)
and computes, per fault: the peak error rate, whether the workload kept making progress (availability
under fault), and the **recovery time** — seconds from the heal until the error rate returns to
near-zero. These feed a `checks:` block of pass/fail rules (all defaulted):

```yaml
checks:
  max_recovery_seconds: 45          # every healed fault must recover within this
  min_recovered_throughput_fraction: 0.9  # and regain this share of its pre-fault throughput in that time (0 = off)
  require_recovery: true            # a fault that never recovers fails the run
  require_progress_under_fault: true # a total outage (0 ops) during a fault fails the run
```

The correlation is written to `analysis.md` and `scenario.json`.

### Divergent-apply check (every scenario)

Kahuna logs `Same-revision divergent apply for key … overwrites a different value already recorded at this
revision` when two committed log entries write different values to one key at one revision. The second record
replaces the first, so an acknowledged write is gone, and Kahuna continues. Caraxes scans the captured node logs
for that line after every run, for every workload, and fails the scenario when it finds one. The matched lines go
to `run/divergent-applies.txt`.

```yaml
checks:
  require_no_divergent_apply: true  # default; false only for a run that reproduces the defect on purpose
```

The check needs the node logs (`capture_node_logs`, on by default). Without them the verdict says the check did
not run; it does not pass silently. It was first seen in an Elle run under a pause fault, where the history held
an acknowledged append that no later read contained.

### Backups under load (`backup` fault and `backup_verify`)

`cluster.backups: true` turns CamusDB's backup and point-in-time-recovery API on for every node: a host bind
mount at `/backups` (one per node under the scenario's run directory, so the artifacts outlive the container
and a tagged run keeps its own), `kahuna.backup_dir: /backups/catalog`, `kahuna.restore_root: /backups/restore`
and `kahuna.backup_cluster_id` set to the cluster name. The PITR window, the base-snapshot cadence and retention
go through the `kahuna:` passthrough. A bind mount rather than a directory under `/data` on purpose: the question
a backup scenario asks is whether what was written can be read back after the cluster is gone. The cost is that
the base image is a copy, not a hard link (RocksDB hard-links a checkpoint only within one filesystem), which is
what a production backup to a separate volume pays too.

The `backup` nemesis fault is the operator's cron job scheduled as a fault, so the correlator grades what it cost.
`backup_kind` is `incremental` (the default: it chains on the newest backup in the target node's own catalog, and
starts the chain with a full when the catalog is empty), `full`, or `coordinated` (the cluster-wide consistent cut,
accepted only by the meta-partition leader; the fault retries on the other nodes when the target is not it). The
window runs from the request to `duration` after it returns, so keep `duration` short. Each backup's id, kind,
parent, partitions and elapsed time go to the timeline, and a substitution (an incremental the engine turned into
a full) is written there in capitals. Backups are node-local, so a chain lives on the node that took it. The
request runs from the node's loopback like the topology mutations (the endpoints are loopback-only without
authentication). A failed request is a nemesis error with no window, and the verification below counts it.

```yaml
cluster:
  backups: true
  kahuna: { pitr_window_seconds: 7200, base_snapshot_interval_seconds: 1800 }
nemesis:
  events:
    - { at: 3m, fault: backup, target: camus1, duration: 5s }                     # empty catalog: a full
    - { at: 7m, fault: backup, target: camus1, duration: 5s }                     # incremental on it
    - { at: 11m, fault: backup, target: camus1, duration: 5s, backup_kind: full }
backup_verify:
  node: camus1                   # default: the node the schedule names most, else camus1
  final_incremental: true        # one closing incremental once the load has stopped
  restore: true                  # POST /v1/restore into the node's restore root
  boot: true                     # the rehearsal: needs teardown: true and every node hosting every range
  require_unbroken_chain: true   # every backup the run took must still be in the leaf's chain
  restore_timeout_seconds: 3600  # the node aborts a restore whose request is cut off; cover the whole replay
checks:
  min_throughput_under_fault_fraction: 0.8   # in-window mean ops/s must stay above this share of the pre-fault median
```

`backup_verify` is the no-data-loss proof, run after the workload: one closing incremental on the node; the live
ledger fingerprint (rows, `SUM(balance)`, `SUM(version)`, `MAX(version)` over the dataset) read from the live
cluster; the chain ending at the newest backup resolved and checked (a full at the root, every link readable, no
substitution, and — with `require_unbroken_chain` — every backup the run took on the node still in it); the chain
restored on the node; then, after teardown, the **disaster-recovery rehearsal**: the same fleet is brought back up
with every node's data root copied from the restore, and the fingerprint read from every node and compared with
the live one. Equal on every node, with the run's own reconciliation passed, is the claim. `SUM(balance)` alone
cannot carry it — every transfer conserves it, so a restore that dropped the last ten minutes conserves it too;
`SUM(version)` rises with every transfer and is what a lost tail shows up in. Every finding goes to the verdict and
to `backup-verify.json`; a step that could not run fails the run rather than being skipped.

**Replacing a node from the chain (`add-node`).** The operator's recovery as a fault: a `kill` with
`heal: false` (or a `remove-node`) takes a node away for good — the cluster evicts a dead member on its own,
5 s suspicion plus 30 s grace by default — and an `add-node` scheduled after it boots a replacement that joins
the survivors through CamusDB's `--join-existing`. With `seed_from_backup: true` (the default) the fault takes a
closing incremental on the target node, restores that chain on the target into its restore root, copies the
restored data root into the joiner's data directory and the chain's links from the target's catalog into the
joiner's backup mount (only those: a catalog holds every backup the node ever took), and
boots the joiner with `--pitr-bootstrap-from` naming the closing backup: the node replays the chain and writes the
restored index into its empty WAL before Raft starts, so it presents at that index and the leader ships only the
delta since the backup. The injection's outcome says whether the seed was honoured (CamusDB's "Seeded the node"
line present, no "presence frontier 0" line in the joiner's log). With it off the joiner starts empty and is
seeded by snapshot. The joiner takes the next
free `camusN` slot (`node_name` picks one; it must be within `spare_certs`, which the certificate's SANs cover),
runs the same image, config, environment and mounts as the fleet, and is removed at teardown. The injection
returns when the node reports ready, which CamusDB does only once Kommander has promoted it to Voter; its seeds
are the fleet nodes answering `/ping` at that moment. After the workload, `backup_verify` reads every joined
node: a Voter in the chain node's roster, ready, hosting the cluster's partitions, and reading the live ledger
exactly, or the run fails. See `scenarios/bank-backup-replace-node-preflight.yml`.

```yaml
nemesis:
  events:
    - { at: 20s,  fault: backup,   target: camus1, duration: 5s }
    - { at: 40s,  fault: kill,     target: camus3, heal: false }
    - { at: 100s, fault: add-node, target: camus1, duration: 5s, seed_from_backup: true }  # boots camus4
```

`checks.min_throughput_under_fault_fraction` (default 0 = off) is the floor on throughput *inside* a window: the
recovery rules judge the tail after a heal, and for an operation that is not an outage the window is the whole
cost. A window whose bar was set on the host device's slow regime is reported as inadmissible, like the held rule.

See `scenarios/bank-backup-preflight.yml` (short, proves the whole path), `bank-backup-soak-nvme-rel1119pkc.yml`
(the incremental arm) and `bank-backup-soak-nvme-rel1119pkc-full.yml` (the full arm; a schedule of fulls sets
`require_unbroken_chain: false`, because its chain is one link long by construction).

### Online schema changes under load (`sql` fault and `schema_verify`)

The `sql` nemesis fault runs one SQL statement on its target node while the workload runs: an online schema change
(`CREATE INDEX`, `ALTER TABLE ... ADD COLUMN`, `ADD CONSTRAINT ... FOREIGN KEY`, `DROP INDEX`) or a bulk write
(`INSERT ... SELECT`, a large `DELETE`). It is scheduled like a fault, so a kill, partition or pause can be timed to
land inside it, and the correlator grades what it cost. The statement goes from the host to the node's REST port
(`/execute-sql-ddl`, `/execute-sql-non-query` or `/execute-sql-query`, by its leading keyword); a follower forwards
DDL to the schema leader, so sending each statement to a different node puts the forwarding in the path. The window
runs from the request to `duration` after it is answered; a schema change waits for its whole backfill, so the window
covers it. Two `sql` events on one node must not overlap (windows are keyed by kind and node).

A statement refused with a retryable code (`CADB0502`, `CADB0504`) is sent again with backoff, up to `sql_retries`
(default 10), and the attempt count goes to the timeline and the verdict. `expect` says what the statement must do:
`ok` (the default), `error` (it must be refused), or `any` (a fault scheduled into its window may cut it, and
`schema_verify` judges what it left). A statement whose outcome does not match fails the run.

`schema_verify` runs after the workload, on the live, quiescent cluster:

- **Convergence.** Every node must report each listed element in the same state, and the state must hold for
  `stable_seconds`, so a job a fault interrupted has been resumed or rolled back first. Then each element must match
  its `expect`: `present`, `absent`, or `either`.
- **Indexes.** The table is read through `@{FORCE_INDEX=pk}` and through `@{FORCE_INDEX=<index>}`, projecting the
  primary key, the key columns and the `INCLUDE` columns, and the two reads must hold the same rows with the same
  values (a covering index answers from its own entries, so a stale entry shows up as a value mismatch). Then
  `probe_rows` rows spread over the key space are looked up through the index at their current key, which catches
  an entry left at an old key even for a non-covering index. A `unique` index must also have unique keys.
- **Columns.** Every row must hold `value` (the workload never writes the column, so only the fill and the inserts'
  default can), with no NULL (`not_null`, default true), and with `distinct: true` no value twice (a function or
  identity default).
- **Foreign keys.** Present means `SHOW CREATE TABLE` renders the constraint (only a public one is rendered); no
  child row may reference a missing parent.
- **Assertions.** A query run on every node whose first value must equal `equals`.
- **The application's view.** `max_workload_errors` (default 0, -1 off) fails the run when the workload recorded
  non-retryable errors: an online change is meant to cost the application retryable conflicts only.
- **The coordinator's evidence**, from the node logs: an `Abandoning coordinator job` line fails the run, and
  `require_resume: true` fails it as VACUOUS when no node logged `Resuming coordinator job`, because then the fault
  missed the change it was aimed at.

The findings go to the verdict and to `schema-verify.json`. The bank ledger's own checks still apply, and they are
the lost-update guard for a column fill, which rewrites rows the transfers update.

```yaml
nemesis:
  events:
    - { at: 30s,  fault: sql,  target: camus2, expect: any, sql: "ALTER TABLE workload_accounts ADD COLUMN tier INT64 NOT NULL DEFAULT (7)" }
    - { at: 36s,  fault: kill, target: camus2, duration: 15s }    # the node holding the request
    - { at: 56s,  fault: kill, target: camus3, duration: 15s }    # rolling: one of them leads the schema partition
    - { at: 76s,  fault: kill, target: camus1, duration: 15s }
    - { at: 130s, fault: sql,  target: camus1, expect: any, sql: "ALTER TABLE workload_accounts ADD COLUMN tier INT64 NOT NULL DEFAULT (7)" }
schema_verify:
  require_resume: true
  indexes: [ { table: workload_accounts, name: acct_balance, columns: [balance], include: [version] } ]
  columns: [ { table: workload_accounts, name: tier, value: "7" } ]
  foreign_keys: [ { table: workload_holds, name: fk_holds_account, column: account_id, parent: workload_accounts } ]
  assertions: [ { sql: "SELECT COUNT(*) FROM workload_holds WHERE amount < 20", equals: "0" } ]
```

There is no HTTP surface that names the schema leader, so a scenario that must kill the coordinator kills every node
in turn while the change runs, and `require_resume` proves one of the kills landed. Re-sending the cut statement after
the faults heal (`expect: any`) and requiring the element `present` proves a cut change either finished or rolled back
cleanly enough to run again. See `scenarios/schema-preflight.yml`, `schema-add-column-kill.yml` and
`schema-index-kill.yml`.

### Bank-transfer invariant workload

`workload.kind: bank` swaps the shard-disjoint baseline for **transfers between two rows across the
whole keyspace** — real write/write contention, absorbed with a bounded retry. Because every transfer
conserves total balance and commits atomically, `SUM(balance)` must be unchanged after the run; a
changed sum is direct evidence of an atomicity break. This invariant is checked post-run and is
**never waived** — unlike conflicts, an atomicity violation is a correctness failure a chaos run must
catch. See `scenarios/bank-kill.yml`.

### Delete workload (`tokens`)

`workload.kind: tokens` is the bank transfer for deletes. A table of its own, `workload_tokens`, holds
`workload.tokens` rows (default 2,000), one per slot. Each write is a **move**: it reads a slot's row through
the non-unique slot index, `DELETE`s it (the delete must match the row just read; a racing move that got there
first sends this one back to `BEGIN`), and `INSERT`s the successor at the next generation, in one transaction.
The successor's key is either a fresh key, the key just deleted (reuse inside the transaction), or the key
the slot's previous move deleted (reuse of a key whose delete an earlier transaction committed). Each read is
a serializable read-only transaction (CamusDB's snapshot, whatever isolation the moves run at) that looks one
slot up and must find exactly one row. At read committed a statement reads the latest committed version of each
key, so an index lookup and its row fetch may straddle a move; a snapshot may not.

The table holds exactly `tokens` rows at every committed state, so the checks are exact and never waived:

- **In the window.** The scan probe counts `workload_tokens` alongside the dataset on every gateway, inside a
  snapshot (the dataset counts are unchanged); a count
  above the token count (`over`) is a deleted row seen next to its successor, a count below it a lost one. A
  read-only lookup that finds no row or two fails the run.
- **After the run**, against the workload's journal (`token-ledger.csv`): every slot holds exactly one row (two
  is a committed delete that came back, or a double spend; none is a lost insert); its generation lies between
  the moves that committed and those plus the moves whose commit had no verdict (below is a lost committed
  move, above a move the client saw fail that applied); no two moves committed from one generation; and the
  slot index holds exactly the primary key's rows.

The index is deliberately not unique: two moves of one generation that both commit leave two rows, so the only
thing preventing a double spend is the engine's conflict detection on the deleted row. The seeded dataset is
created and reconciled as usual and left untouched. The verdict goes to `reconciliation.json` (`Tokens`) and the
scenario notes. See `scenarios/tokens-preflight.yml`, `tokens-faults.yml`, `tokens-split.yml` and
`tokens-leader-churn.yml` (leaders paused and killed every 25 s, the scan probe every second).

### Many-table workload (`fanout`) and range auto-split

`workload.kind: fanout` is the bank transfer spread over many tables. `workload.tables: N` cuts the
seeded rows into N contiguous blocks, one table each, and every transfer moves its two legs between
two *different* tables. Two things follow:

- **Every partition carries write traffic.** A table (and each of its eligible secondary indexes) is
  one key space, so the single-table dataset lives on one partition under hash routing and on one
  range under key-range routing. N tables occupy N key spaces.
- **The conserved sum is a whole-dataset sum.** Reconciliation reads `SUM(balance)` per table and
  compares the total, because a transfer's two legs land in different tables. The invariant is
  otherwise identical to `bank`, and still never waived.

`tables` must not exceed `rows` (an empty table is created and never used, so the run would test less
than it claims), and `fanout` needs at least 2. The same `tables` value is passed to the seeding
`init` and the measured `run`, so they cannot disagree about the schema.

This is what makes **range auto-split** reachable: a range only divides when it holds enough keys, or
when its partition is saturated. Turn it on in the `cluster:` block —

```yml
cluster:
  key_range_sharding: true   # a hash-routed space has no range descriptor to split
  partitions: 4              # a child range needs a partition to move to (2 is the minimum)
  leader_balancer: true      # nothing else moves the child leader off the hot node
  kahuna:
    range_split_threshold: 2000       # count branch: keys in a range before it is a candidate
    range_split_min_range_size: 250   # the threshold must be at least twice this
    range_split_load_threshold: 200   # load branch: sustained log ops/sec for a hot partition
    enable_load_reports: true
```

Both branches are off unless a threshold is set, and a node whose preconditions are unmet logs one
warning per cause and **starts anyway** — so a misconfigured split scenario looks like a working one
until `SHOW ENGINE STATS` reports `kahuna.range.splits` of zero. Check that counter before reporting
a split run as a pass. See `scenarios/split-preflight.yml` (short, fault-free, proves the path),
`scenarios/split-optimistic-45m.yml` (the fanout soak), and
`scenarios/bank-optimistic-split-45m.yml` (the bank soak with only the split settings changed, so it
compares directly against the bank runs). CamusDB's `docs/key-range-sharding.md` documents the
engine side.

**Vacuity checks.** `checks.require_split: true` fails a run as VACUOUS when no partition appeared in
the placement samples taken while the workload ran (a split allocates a new partition for its child
range), and `checks.require_replica_move: true` fails it when no partition's placement generation rose.
Splits during the seeding `init` do not count. Use them on any scenario whose claim is "correct while
ranges split" or "correct while replicas move": a three-node cluster at replication factor 3 hosts every
range on every node and never moves a replica, and a missed split precondition starts anyway. See
`scenarios/append-split.yml`, `bank-split.yml`, `append-placement.yml` and `bank-placement.yml`, which run
in the nightly.

### Elle history checking (`append`)

`workload.kind: append` runs Elle's list-append workload over SQL. Each transaction reads and appends
to a few contended lists in a table of its own (`elle_append`). The workload writes every attempt to
`run/history.edn` as a Jepsen history: an invoke line, then an `ok`, `fail` or `info` line. After the
cluster is torn down, elle-cli checks the history for dependency cycles. That finds lost updates, write
skew, read skew, and aborted or dirty reads, which no conserved sum can see.

```bash
tools/elle/fetch.sh     # once: downloads the pinned elle-cli jar (needs no local Java)
dotnet run --project Caraxes -- run --scenario scenarios/append-smoke.yml
```

The check runs `java` from the `eclipse-temurin:21-jdk` image, so the host needs Docker only. It
must be a JDK image: the JRE image has no `jdk.random` module, and elle-cli cannot load without it.
Cycle diagrams are off by default (`elle.plots: false`), because they need Graphviz, which the image
does not have. It writes `elle-result.json`
(elle-cli's full analysis) and, for an invalid history, the anomaly explanations and cycle plots under
`run/elle/`. The scenario passes only when Elle's `valid?` is `true`. `false` fails it. `unknown` (a
cycle search that timed out) also fails it, because elle-cli exits 0 for `unknown`.

```yaml
workload:
  kind: append
  append_keys: 8                  # lists active at one time; fewer = more contention (default 10)
  append_max_writes_per_key: 64   # appends before a slot moves to a new key (default 64)
  append_max_txn_length: 4        # most steps per transaction (default 4)
  read_percent: 20                # read-only list-append transactions
  write_percent: 80
elle:
  consistency_models: ""          # default: serializable, or read-committed for read_committed runs
  anomalies: ""                   # extra anomalies to search for, e.g. G1a,G1b
  cycle_search_timeout_ms: 0      # 0 = elle-cli default (1000); raise it if the verdict is unknown
  heap_mb: 0                      # JVM -Xmx; 0 = JVM default
  plots: false                    # cycle diagrams; needs an image with Graphviz
```

The default model is `serializable`, not `strict-serializable`. A read-only snapshot can lag a write
that already returned. Set `consistency_models: strict-serializable` when real-time order is the claim
under test. The seeded dataset is still created and reconciled, and the append shape leaves it
untouched. See `scenarios/append-*.yml`.

### Performance evidence (per-node metrics, cluster facts, comparability)

A reliability scenario asks whether the cluster stayed correct. A **capacity** scenario asks how fast
it went, and that question needs evidence a single throughput number cannot carry: which node did the
work, what was still growing when the window closed, and which build produced the figure. Every
scenario collects it by default.

**Per-node metric time series.** The workload scrapes each node's `/metrics` for the whole run and
writes `node-metrics.csv` next to the other artifacts. Counter deltas are cut to the measured window,
so seeding and warm-up are excluded, and each node keeps its own series — which is what turns "one
leader carried every write" into a readable finding. `bottleneck-report.md` gains sections for
per-node work distribution, batch density, commit path, and backlog growth. Needs the cluster's
`diagnostics: true` (the default); with diagnostics off the collection is skipped and says so.

**Cluster facts.** Each node is asked what it is running — the Kahuna, Kommander and Nixie assembly
versions it actually loaded, whether it was ready, the configuration it resolved, and where the
workload's ranges sat — into `cluster-facts.json`, reduced to one `durabilityFingerprint`. Two runs
whose fingerprints differ were not on the same build or durability settings and must not be compared.

**Load-generator headroom.** `client-resources.json` records the generator's own CPU, allocation, GC
pauses, thread-pool backlog and in-flight requirement. A generator that ran out of CPU produces a flat
curve that reads exactly like a saturated cluster. The thread-pool backlog is judged on how long it
lasted, not on its highest single one-second reading: the workload warns when the queue stayed above
100 pending items for 5 s without a break, or sat above 100 in more than 5% of samples (p95). The
peak is still reported, marked as a single sample. One spike in a two-hour soak is the normal shape of
a burst of continuations after a fault heals, and it no longer fails `require_client_headroom`.

```yaml
workload:
  node_metrics: true      # per-node time series (default true)
  metrics_interval: 5s    # scrape interval, minimum 1s
  cluster_facts: true     # versions, readiness, config, ranges (default true)
settle_seconds: 30        # wait for leadership to resolve AFTER seeding, before measuring; 0 skips
checks:
  require_client_headroom: true   # fail if the generator may have been the limiter (capacity runs)
  require_cluster_facts: true     # fail if the run cannot say which build it measured
```

Both new checks default to **false**, so an existing reliability scenario keeps its verdict. Turn them
on for a capacity run, where a client-bound number or an unattributable build makes the result
worthless.

`settle_seconds` runs after seeding rather than after startup: a node reports ready long before
leadership resolves, and seeding creates the very tables whose ranges then have to be placed. It is
best effort — an unsettled cluster still runs, with the caveat recorded in the verdict.

**Establishing a baseline.** One run is a number; a baseline is a number with a known spread, and
without the spread there is no telling a real improvement from two runs of an unchanged system. Use
`--tag` so repetitions keep their artifacts instead of overwriting each other, then aggregate:

```bash
for i in 1 2 3; do
  dotnet run --project Caraxes -- run --scenario scenarios/capacity-baseline.yml --tag r$i
done

CamusDB.Workload baseline --runs \
  runs/scenarios/capacity-baseline-r1/artifacts/run,\
  runs/scenarios/capacity-baseline-r2/artifacts/run,\
  runs/scenarios/capacity-baseline-r3/artifacts/run
```

It reports the median, the min/max spread and the coefficient of variation, refuses a set whose runs
were not the same experiment, excludes an invalid run from the statistics rather than letting it drag
the median, and names the write p99 to freeze as the latency budget. Exit 1 means no baseline was
established — fewer than three usable runs, or a spread above 10%.

**Comparing two runs.** `CamusDB.Workload compare` refuses a pair whose workload shape, client
version or build fingerprint differs, rather than reporting a meaningless ratio:

```bash
CamusDB.Workload compare \
  --baseline runs/scenarios/capacity-baseline-r2/artifacts/run \
  --candidate runs/scenarios/<later>/artifacts/run \
  --require-ratio 10.0 --require-ops 2100 --p99-budget-ms <frozen p99>
```

Exit 3 means the runs are not comparable; exit 1 means a gate failed. `scenarios/capacity-baseline.yml`
is the fault-free, fully instrumented baseline to reproduce before any tuning.

## Matrix sweeps

`caraxes matrix --matrix <yml>` runs a cartesian sweep and writes a cross-cell `matrix-report.md`:

```bash
dotnet run --project Caraxes -- matrix --matrix scenarios/matrix-resilience.yml
```

A matrix has a base `cluster`/`workload`/`checks` and an `axes` block; cells are the product of the
axes (`locking`, `nodes`, `sharding`, `parallelism`, and named `nemesis` presets). Every cell shares
one image (built once) and runs sequentially through the ordinary scenario path, so a cell behaves
exactly like a hand-written scenario. The report lines up each cell's verdict, max recovery time, and
latency inflation. Exit code is non-zero if any cell failed.

## Leader-balance test

`caraxes leader-balance --spec <cluster>` tests the Raft leader balancer directly:

```bash
dotnet run --project Caraxes -- leader-balance --spec scenarios/leader-balance.yml
```

It measures how partition leadership is spread (each node self-reports the partitions it leads via a
`LeaderLocal` flag on `GET /v1/cluster/placement`), kills the node that leads the most partitions so
its leaderships pile onto the survivors, restarts it, and watches whether the balancer moves
leadership back to the rejoined node. It passes when the rejoined node regains at least half its fair
share and the overall spread returns to near-even. With the balancer off the rejoined node stays at
zero, so this is a real test of the balancer, not just of re-election. Writes `leader-balance.md` and
a per-poll `leaders.jsonl`.

`up` regenerates the cluster's dev certificate when the SAN parameters changed (node count, subnet),
rebuilds the image, generates per-node `config.yml` + a compose file under `runs/<name>/`, starts the
fleet, and polls `GET /v1/cluster/health` until every node reports ready.

## Cluster spec

See `scenarios/*.yml`. Every knob has a default; `name` is the only required key. Notable options:
`nodes`, `partitions`, `replication_factor`, `placement_rebalancer`, `leader_balancer`, `zones`
(one per node), `locking` / `isolation` (CamusDB transaction defaults), `key_range_sharding`,
`distributed_query_execution`, `max_query_parallelism`, `subnet` (first three octets of the
cluster's /24), `camusdb_repo`, and a raw `kahuna:` passthrough for engine knobs the spec does not
model.

## Roadmap

Phased build-out (see the project plan): cluster orchestration (done) → workload integration (done)
→ nemesis fault injection + membership changes (done) → invariant workloads, verdict engine, and a
scenario matrix (done) → disk faults (`fill-disk`, `slow-disk` done; device-mapper corruption pending) →
Elle history checking (list-append done; predicate and index workloads pending).

## Complementary verifications

Caraxes tests CamusDB end to end. The layers below it have their own Jepsen suites:

- [Kahuna Jepsen](https://github.com/kahunakv/kahuna-jepsen) — Jepsen tests for Kahuna, the
  distributed key/value and locking layer under CamusDB.
- [Kommander Jepsen](https://github.com/kahunakv/kommander-jepsen) — Jepsen tests for Kommander,
  the Raft consensus library under Kahuna.

## Blog

- [A Dragon to Attack Our Own Database](https://camusdb.github.io/blog/a-dragon-to-attack-our-own-database/) — the story behind Caraxes.
