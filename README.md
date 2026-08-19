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
