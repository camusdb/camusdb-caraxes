# Caraxes

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

Fault kinds: `kill` (SIGKILL, restart on heal), `stop` (graceful), `pause` (SIGSTOP freeze),
`partition` (iptables-isolate a node from all peers), `slow` / `loss` (tc netem latency / packet
loss), `fill-disk` (exhaust a node's `/data` until ENOSPC — needs a size-capped tmpfs data mount, see
below), and `remove-node` (drain via `/v1/cluster/leave`, a one-way scale-down). Targets are a node
name, `random` (seeded, reproducible), or `zone:<name>` (every node in a failure zone — the fault
applies to each, so `kill` of a zone kills the whole zone together). Every healable fault still in
effect when the workload finishes is healed on the way out, so the cluster is never left broken.

**Disk faults.** `fill-disk` needs a size cap to be meaningful, so set `data_tmpfs_mb: <MiB>` on the
cluster — each node's `/data` then becomes a RAM-backed, size-capped tmpfs the fault can exhaust. This
is for disk-pressure behavior, not durability (tmpfs is lost on restart). Device-mapper latency and
corruption faults (`dm-delay` / `dm-flakey`) need a Linux host and are a follow-up. See
`scenarios/disk-full.yml` and `scenarios/zone-failure.yml`.

Two schedule forms — an explicit `events:` timeline or a seeded `random:` soak:

```yaml
nemesis:
  seed: 7
  events:
    - { at: 20s, fault: kill, target: random, duration: 20s }
```

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
  require_recovery: true            # a fault that never recovers fails the run
  require_progress_under_fault: true # a total outage (0 ops) during a fault fails the run
```

The correlation is written to `analysis.md` and `scenario.json`.

### Bank-transfer invariant workload

`workload.kind: bank` swaps the shard-disjoint baseline for **transfers between two rows across the
whole keyspace** — real write/write contention, absorbed with a bounded retry. Because every transfer
conserves total balance and commits atomically, `SUM(balance)` must be unchanged after the run; a
changed sum is direct evidence of an atomicity break. This invariant is checked post-run and is
**never waived** — unlike conflicts, an atomicity violation is a correctness failure a chaos run must
catch. See `scenarios/bank-kill.yml`.

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
curve that reads exactly like a saturated cluster.

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
scenario matrix (done) → disk faults and Elle-style history checking.
