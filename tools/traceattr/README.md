# traceattr — attribute a node's EventPipe trace by thread role, state and component

Built for Kommander feature `fc4dabe7` task 2 (the leader trace of the Raft round). `tools/p3c/leader-trace.sh`
takes the traces; this reads them.

```sh
cd tools/traceattr
dotnet run -c Release -- <run>/diagnostics/leader-camus3.nettrace                       # running samples, all roles
dotnet run -c Release -- <trace> --state lock --callers Monitor.Enter                   # who is blocked on which monitor
dotnet run -c Release -- <trace> --role wal-scheduler --frames 40                       # one thread role
dotnet run -c Release -- <trace> --grep Kommander.Consensus                             # stacks through a namespace
```

- **Role** is decided by a thread's bottom frames: `raft-executor`, `wal-scheduler`, `read-scheduler`,
  `socket-engine`, `threadpool`, ...
- **State** is decided by its top frames: `running`, `lock` (a contended monitor), `gcpoll` (trapped at a
  suspension) or `wait` (parked). A `FileSystemWatcher` thread blocked in `read(2)` shows as one `running`
  thread in every trace; subtract it.
- Converting a first run caches `<trace>.etlx` beside the trace. Delete it when you are done.

## Read it with the sampler's distortion in mind

`dotnet-sampled-thread-time` suspends the runtime for every sample. On a node with ~200 threads that is
**~31% of wall time** (the tool prints it as "non-GC EE suspensions"). The traced node therefore runs
slower. Its per-thread CPU is dilated by ~1/(1 − that fraction), and lock convoys look worse than they
are, because a holder can be suspended inside its critical section. Use the trace for *which stack* and
*relative share*. For absolute CPU and scheduling latency, use the unperturbed schedstat sampler below.

## schedstat sampler (no instrumentation, no perturbation)

```sh
tools/traceattr/schedstat-sample.sh out.csv <cluster-name> 600 &     # every 10 s: per-thread cpu, run-queue wait, timeslices
python3 tools/traceattr/schedstat-summary.py out.csv <from-unix-s> <to-unix-s>
```

It reads `/proc/<pid>/task/*/schedstat` of each node container from the host. Thread-pool threads come and
go, so the summary adds up deltas over consecutive 10 s pairs. It agrees with the node's own
`dotnet_process_cpu_time` to within 1%. **runq/slice** is the mean time a wakeup waits for a CPU, which
is the per-hop scheduling cost of each thread class.

`metrics-delta.py before.txt after.txt <seconds> <regex>` gives the rate of any counter between two
`/metrics` scrapes. The trace driver scrapes the traced node before and after each trace.
