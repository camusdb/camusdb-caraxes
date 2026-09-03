# snapreader — standalone Kahuna store-snapshot forensics

Decodes a Kahuna node data directory copied out of a container (`store-snapshot/` in a run
archive) without the engine. Used to root-cause the `split-nemesis-indeterminate` atomicity
break (kahuna Vorpal feature `f93ea4dd`).

Modes:

- `rows <kvDbPath> <indexesCsv>` — every revision entry (`~N` and `~CURRENT`) of the rows whose
  embedded global index is listed. Prints key, index, balance, version, revision, state, and the
  last-modified HLC physical time. CompiledRow offsets: index int32@14, balance int64@26,
  version int64@34.
- `wal <walDbPath> <keySubstrCsv>` — committed `kv`-type Raft entries touching the given keys,
  with revision, decoded balance/version, and the writing transaction id.
- `walrec <walDbPath> [txnPhysCsv]` — `txnrecord`-type Raft entries (INIT/COMMIT/ABORT commands
  with participants).
- `records <snapshotDir> [txnPhysCsv]` — `transactionrecord_v1_p*.snapshot` decode: decision,
  participants, createdAt/decidedAt, anchor key.
- `cfs` / `scan` / `get` — raw RocksDB exploration.

Uses the same `RocksDB` NuGet package as Kommander, so it reads the current format_version.
Always work on a COPY of the snapshot; the tool opens read-only but keep the evidence pristine.
Byte-format reference: the storage layout report in the `f93ea4dd` investigation (kv CF keys are
`{key}~{rev}` / `{key}~CURRENT`; WAL keys are `{pid:010d}:{logId:020d}` in `shard{pid%8}`).
