using P3c;

// Reads the artifacts a Caraxes run leaves behind and answers the three questions the Phase 3
// close-out campaign asks of them (CamusDB Vorpal feature cfe716da).
//
// Modes:
//   extract <runDir>                     - one run as JSON: stack, throughput, stage costs, counters
//   compare <cell> [arm ...]             - per-replicate arm deltas for a cell (default base cand onep)
//   queue   <runDir> [runDir ...]        - per-kind KV write queue delay, and the task-4 decision rule
//   hops    <runDir> [runDir ...]        - session-registration hops: forwarded share, hops per write,
//                                          the measured forward cost, and the write latency it explains
//   soak    <runDir> [runDir ...]        - 45-minute soak windows: average, first five, last five, decay
//   rebase  <base1> <cand1> <cand2> <base2> - the matched ABBA ratio for the sustained bank re-baseline
//   regime  <runDir> [runDir ...]        - per-5-minute Raft/read/host-I/O regime check: did one run hold ONE regime?
//
// Why per-replicate and not medians: this host moves the cost of a durable Raft write by up to 4.5x
// between runs, which is larger than any effect being measured. Arms of one replicate run back to
// back, so only they are comparable; `compare` prints each replicate separately and refuses to
// compare one whose arms straddle regimes.

if (args.Length == 0)
{
    Console.Error.WriteLine(
        "usage: p3c extract <runDir> | compare <cell> [arm ...] | queue <runDir> ... "
        + "| hops <runDir> ... | soak <runDir> ... | rebase <base1> <cand1> <cand2> <base2> "
        + "| regime <runDir> ...");
    return 1;
}

switch (args[0])
{
    case "extract":
        Extract.Run(args[1]);
        return 0;

    case "compare":
        Compare.Run(args[1], args.Length > 2 ? args[2..] : ["base", "cand", "onep"]);
        return 0;

    case "queue":
        foreach (string dir in args[1..])
            Queue.Run(dir);
        return 0;

    case "hops":
        Hops.Run(args[1..]);
        return 0;

    case "soak":
        Soak.Run(args[1..]);
        return 0;

    case "rebase":
        Soak.Rebase(args[1..]);
        return 0;

    case "regime":
        foreach (string dir in args[1..])
        {
            if (Regime.Analyze(dir) is Regime.Report report)
                Regime.Print(report);
            else
                Console.Error.WriteLine($"{dir}: no node-metrics.csv / run-meta.json");
            Console.WriteLine();
        }
        return 0;

    default:
        Console.Error.WriteLine($"unknown mode '{args[0]}'");
        return 1;
}
