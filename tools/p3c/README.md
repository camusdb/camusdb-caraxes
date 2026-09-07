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
