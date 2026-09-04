# memdecomp — split a run's memory growth into components, from artifacts already on disk

Every memory figure in the cluster throughput campaign comes from `docker stats`, which Caraxes
records in `memory-samples.csv`. On Linux that figure is cgroup memory usage minus inactive file
cache. It therefore counts **active page cache, kernel slab and tmpfs** as well as the node
process. A growth curve read from it cannot say whether the process grew or the cache did.

The node also exports the .NET runtime meter through `/metrics`, and `CamusDB.Workload` samples
it every few seconds into `artifacts/run/node-metrics.csv`. That file already carries the
process working set, the GC committed size, the GC heap size per generation and the
fragmentation size. Joining the two files splits the docker figure into:

| component | formula | what it is |
|---|---|---|
| `cache+k` | docker − working set | page cache, kernel, anything outside the process |
| `native` | working set − GC committed | RocksDB, WAL buffers, stacks, JIT, GC bookkeeping |
| `gc_slack` | GC committed − GC heap | committed regions the GC holds but does not use |
| `live` | GC heap − fragmentation | best estimate of live managed data without a gcdump |

The tool prints those per node and per 5-minute bucket inside the measured window, next to
the completed operations from `intervals.csv`, and then the growth of each component per
completed operation. The component whose KiB/op matches the docker figure owns the growth.

## Usage

```sh
cd tools/memdecomp
dotnet run -- ../../runs/scenarios/bank-soak-p1-w128-s1
dotnet run -- ../../runs/scenarios/bank-soak-head-p1-w128-s1 --bucket 600 --csv
```

Required inside the run directory:

- `memory-samples.csv` (Caraxes `NodeMonitor`)
- `artifacts/run/run-meta.json`, `artifacts/run/node-metrics.csv`, `artifacts/run/intervals.csv`

## Reading the result

- `cache+k` owns the growth: the growth is not in the process. A gcdump cannot find it. Look at
  RocksDB SST reads through the page cache and at the WAL write stream.
- `live` owns the growth: something managed accumulates. A loaded gcdump names it.
- `native` owns the growth: look at RocksDB and the WAL, not at the GC.
- `gc_slack` owns the growth: the GC holds committed regions it does not use. A heap hard limit
  or a conserve-memory setting is the lever.

## Metric names

The .NET 9+ runtime meter names are looked up first (`dotnet_process_memory_working_set`,
`dotnet_gc_last_collection_memory_committed_size`, `dotnet_gc_last_collection_heap_size`,
`dotnet_gc_last_collection_heap_fragmentation_size`, `dotnet_gc_heap_total_allocated`,
`dotnet_gc_collections`, `dotnet_gc_pause_time`). The pre-.NET 9 `process_runtime_dotnet_*`
names are the fallback. The tool prints which family it resolved for each quantity. If a
quantity reads `n/a`, check a raw `metrics-<node>.txt` scrape in the run directory for the
exported name and add its stem to `Resolve` in `Program.cs`.

The tool never reads the engine, RocksDB or docker. It is safe to run on any copy of a run.
