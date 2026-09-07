namespace P3c;

/// <summary>
/// Per-replicate arm comparison for one cell. Deltas are within a replicate, never across them: the
/// arms of a replicate ran back to back, so they share a durability regime, and replicates do not.
/// </summary>
public static class Compare
{
    /// <summary>
    /// Above this ratio between the arms' per-write Raft cost, the replicate is reporting the host
    /// rather than the code, and its deltas are printed as untrustworthy rather than quietly averaged in.
    /// </summary>
    private const double RegimeRatioLimit = 1.5;

    public static void Run(string cell, string[] arms)
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                   "camusdb-caraxes", "runs", "scenarios");

        Dictionary<string, Dictionary<string, RunArtifacts>> byReplicate = [];
        foreach (string arm in arms)
        {
            for (int rep = 1; rep <= 9; rep++)
            {
                string dir = Path.Combine(root, $"p3c-{cell}-{arm}-r{rep}");
                if (RunArtifacts.TryLoad(dir) is not RunArtifacts run)
                    continue;

                if (!byReplicate.TryGetValue($"r{rep}", out Dictionary<string, RunArtifacts>? slot))
                    byReplicate[$"r{rep}"] = slot = [];
                slot[arm] = run;
            }
        }

        if (byReplicate.Count == 0)
        {
            Console.WriteLine($"no runs found for cell '{cell}' under {root}");
            return;
        }

        Console.WriteLine($"# cell: {cell}\n");
        foreach ((string rep, Dictionary<string, RunArtifacts> present) in byReplicate.OrderBy(p => p.Key, StringComparer.Ordinal))
            foreach (string arm in arms.Where(present.ContainsKey))
            {
                RunArtifacts run = present[arm];
                Console.WriteLine($"  {run.Name,-34} passed={run.Passed,-5} ops/s={run.Ops,8:N1}  " +
                                  $"raft_write_mean_ms={run.RaftWriteMeanMs?.ToString("N2") ?? "—"}");
            }

        int width = Metrics.Rows.Max(r => r.Label.Length) + 2;
        Console.WriteLine("\nper-replicate deltas (arms of one replicate ran back to back)");

        foreach ((string rep, Dictionary<string, RunArtifacts> present) in byReplicate.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            double[] rafts = [.. present.Values.Select(r => r.RaftWriteMeanMs).OfType<double>()];
            bool mixed = rafts.Length > 1 && rafts.Max() / rafts.Min() > RegimeRatioLimit;
            string regime = mixed ? "mixed regimes — NOT comparable" : "one regime";
            string costs = string.Join(", ", arms.Where(present.ContainsKey)
                .Select(a => $"{a}={present[a].RaftWriteMeanMs?.ToString("N2") ?? "—"}"));

            Console.WriteLine($"\n  {rep}  ({regime}; raft write mean ms: {costs})");

            string baseArm = arms.FirstOrDefault(present.ContainsKey) ?? "";
            if (baseArm.Length == 0)
                continue;

            foreach ((string label, Func<RunArtifacts, double?> read) in Metrics.Rows)
            {
                if (read(present[baseArm]) is not double b || b == 0)
                    continue;

                string line = $"    {label.PadRight(width)} {baseArm}={b,12:N1}";
                foreach (string arm in arms.Skip(1).Where(present.ContainsKey))
                    if (read(present[arm]) is double v)
                        line += $"   {arm}={v,12:N1} {(v - b) / b * 100,+7:N1}%";

                Console.WriteLine(line);
            }
        }
    }
}
