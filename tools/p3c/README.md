# p3c — Phase 3 close-out run analysis

Reads the artifacts a Caraxes run leaves behind and answers the three questions the *Cluster throughput
10x — Phase 3 close-out* campaign asks of them (CamusDB Vorpal feature `cfe716da`). Standalone; it
touches nothing but `runs/scenarios/<run>/`.

Modes:

- `extract <runDir>` — one run as JSON: the stack fingerprint and placement note, throughput and
  latency, the finalizer stage costs, every durable-transaction counter, and the post-load tail of the
  retention gauges. This is what a Vorpal record should cite rather than a number retyped by hand.
- `compare <cell> [arm ...]` — per-replicate deltas between arms of a cell (default `base cand onep`),
  e.g. `p3c compare fanout` or `p3c compare grp-accounts cand onep`.
- `queue <runDir> ...` — the per-kind KV write queue delay (init, prepare, decision, materialize,
  settle), the completion delay, the Kommander WAL operations-per-batch figure, and the decision rule
  that retired item 4 of `b01a198d`.
- `soak <runDir> ...` — 45-minute soak windows per run (average, first five, last five, decay, failures,
  peak RSS) followed by the per-window regime check below.
- `rebase <base1> <cand1> <cand2> <base2>` — the matched ABBA ratio for the sustained `bank`
  re-baseline, reported per ordering and refused when the orderings disagree in direction or by more than
  25% in magnitude, when the arms straddle a 1.5x durability regime, or when any run failed the regime
  check.
- `regime <runDir> ...` — per-5-minute regime check of one run: the write leader's Raft batch mean and
  batch rate, its read latency, the follower repair events in the node logs (`batch landed over a gap`,
  `min-log-index mismatch`, backfill pacing), and — when the harness recorded `host-io.csv` — the host
  device's utilisation, read rate and fsync cost. A run whose window Raft means span more than 1.5x, or
  whose leader read mean grew more than 3x first-to-last, held two regimes and is not admissible for a
  ratio. Feature `80af367a`: all four `bank-rebase` soaks stepped 3-4x mid-run and every whole-run check
  passed them.

## Why comparisons are per replicate

This host moves the cost of one durable Raft write by up to **4.5x between runs** — the `fanout` cell
held two regimes, 20.5–21.4 ms and 4.5–4.7 ms, and the `bank` cell drifted monotonically from 6.55 to
12.55 ms across its three replicates. That is larger than any effect the campaign measures.

So the arms of one replicate run back to back and only they are compared. `compare` prints
`raft write mean ms` beside every arm and labels a replicate **mixed regimes — NOT comparable** when its
arms differ by more than 1.5x on that number. Taking a median across regimes instead would have reported
"+327% from the one-phase flag", which is a fact about a disk.

## Driving the runs

`p3c-ab.sh <cell> <reps> <arm> [arm ...]` runs the arms of each replicate back to back, waiting for the
host to go quiet between runs so the harness's `require_quiet_host` gate grades an ambient sample rather
than the tail of the previous run:

```sh
./tools/p3c/p3c-ab.sh grp-accounts 3 cand onep
dotnet run --project tools/p3c -- compare grp-accounts cand onep
```

Scenarios live in `scenarios/p3c-*.yml`; each states its arm, its image and what it expects to see
before it runs.


### Regime rules (as of 2026-09-09)

`regime <runDir>` refuses a run as a ratio input when any of these fails: leader Raft batch mean spread across
5-minute windows > 1.5x; leader query mean last/first window > 3.0x; client completed ops/s best/worst **minute**
> 1.5x, or any failed write (the Raft-mean rule cannot see a collapse that starves batches while the round stays
constant); device ballast (`precondition.json`) finishing after `measureStartUtc`. It prints `host-io.csv`
columns (fsync probe p50, util, reads) per window when the harness recorded them.

**Finding, not a rule (2026-09-10):** `RAFT-LOG RETENTION GROWING` when the worst node's
`raft_wal_shard_live_sst_bytes` (Kommander ≥ 1.5.8) grows more than 3x from the first measured minute to the last
and ends above 64 MB. Kommander 1.6.0 reclaims the Raft log by dropping whole files below a persisted floor that
advances at most `max_entries_per_compaction` per pass, one pass per `compact_every_operations` WAL *batches*; when
that cannot keep up with ingest the gauge climbs linearly (k175 arm: 277 → 1,153 MB inside the 10-minute window)
and the node retains gigabytes of dead log per hour. Retention does not make the throughput window a mixture, so
the line is printed beside the verdict rather than folded into it.

**Rule (2026-09-10):** `WAL ZOMBIE` — a node whose `raft_wal_batches_total` advanced by less than 1% of the busiest
node's per-minute count for two consecutive minutes while that node kept writing (Kahuna feature caf52e10: an
OutOfMemoryException swallowed inside the WAL write left a follower reachable and "healthy" with its queue pinned at
4,096 and zero batches for four minutes). Disqualifying: the run continued on a reduced quorum. Fires on
`bank-rebase-cand-p1-w128-hold-h2-v1` (camus2 from minute 6), quiet on healthy runs.

**Steady tail (2026-09-11):** `regime` also prints the longest suffix of 5-minute windows whose Raft means lie within 1.5x of
each other, with its mean completed ops/s and Raft mean. On the host NVMe the drive's regime step lands inside a 45-minute
window at today's bytes/op (~15-35 GB written → minute 4-10), so the whole-run average is a mixture; the tail is the clean
part, and two arms are compared on their tails when both are long enough (≥ 6 windows). A tail equal to the whole run is the
normal tmpfs case.

### In-window scan-visibility probe

`COUNT_PROBE=1` on `tools/p3c/retention-tmpfs.sh` starts `tools/p3c/count-probe.sh` once the leader shows resident durable
records (load on): `SELECT COUNT(*)` via REST on every gateway every 5 s for `COUNT_PROBE_SECONDS` (default 540), csv in
`runs/count-probe-<tag>.csv`, summary (exact / SHORT / errors) appended to the driver log. A read_committed scan must return the
row count every time (CamusDB feature e31cf9bc; Kahuna 1.7.8 fixed the drop). `tools/p3c/scan-diff-probe.sh` diffs `SELECT id`
scans against a baseline id set and point-reads every missing id, to prove which rows a scan skipped.

### Durable-2PC retention summary

`tools/p3c/retention-summary.sh <run-dir> [tmpfs-footprint.csv]` prints per-minute `kahuna_durable_tx_resident_records`
/ `_receipts` / estimated bytes / early reclaims, the long-lived heap generations from
`dotnet_gc_last_collection_heap_size_bytes`, committed heap, per-node WAL batches per minute and queue depth (the
zombie signature by eye), OutOfMemory / FailFast / WAL-saturated / over-budget log-line counts, and — with the tmpfs
driver's footprint csv — cgroup memory minus the tmpfs data footprint. Label rows are summed per timestamp (the
gauges carry partition labels), then the last sample of each minute is kept.

### Write-probe bytes per operation

`tools/p3c/writeprobe-bytes.sh <runs/writeprobe-<tag>> <runs/scenarios/<run>>` prints, for one write-probe arm: host
device MB/s over the measured window (`host-io.csv`) divided by achieved ops/s → KB/op; each node's `/proc/1/io`
write rate over the same window (`io.csv` from `write-probe.sh`); and, from the last RocksDB `LOG` stats dump of
the Raft-log database, WAL ingest, flush count (and how many were Write-Buffer-Manager-forced), compactions vs
trivial moves, stalls, and the shard CF's per-level Write / Moved / W-Amp columns. Compaction "write" in RocksDB's
table includes the L0 flush (L0 write = flush), so rewrite beyond flush is the L1+ Write column, not the Sum.
