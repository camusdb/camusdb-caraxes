namespace P3c;

/// <summary>
/// The per-kind KV write queue delay on a production (RocksDB, synchronous WAL) cell, and the rule
/// CamusDB task cfe716da/4 used to retire item 4 of b01a198d.
/// </summary>
public static class Queue
{
    /// <summary>
    /// Reopen the scheduler question only if decisions wait this much longer than prepares. Agreed in
    /// advance so the measurement decides rather than the reading of it.
    /// </summary>
    private const double ReopenThresholdPercent = 25;

    public static void Run(string runDir)
    {
        RunArtifacts run = RunArtifacts.Load(runDir);
        Console.WriteLine($"\n== {run.Name}");
        Console.WriteLine($"  {"kind",-12}{"class/type",-22}{"count",12}{"mean ms",10}{"p50",8}{"p99",8}");

        double? preparP99 = null, decisionP99 = null;
        foreach ((string kind, string labels) in Metrics.WorkKinds)
        {
            double? count = run.Metrics.Count(Metrics.SubmissionQueueDelay, labels);
            if (count is not > 0)
                continue;

            double? mean = run.Metrics.Mean(Metrics.SubmissionQueueDelay, labels);
            double? p50 = run.Metrics.Quantile(Metrics.SubmissionQueueDelay, 0.50, labels);
            double? p99 = run.Metrics.Quantile(Metrics.SubmissionQueueDelay, 0.99, labels);

            if (kind == "prepare") preparP99 = p99;
            if (kind == "decision") decisionP99 = p99;

            Console.WriteLine($"  {kind,-12}{labels.Replace("class=", "").Replace(",type=", "/"),-22}" +
                              $"{count,12:N0}{mean,10:N2}{p50,8:N1}{p99,8:N1}");
        }

        Report(run, "completion delay", Metrics.CompletionDelay);
        Report(run, "raft duration", Metrics.RaftDuration);
        foreach (string cls in (string[])["class=ordinary", "class=terminal"])
            Report(run, $"queue age {cls[6..]}", Metrics.QueueAge, cls);

        // The Kommander WAL figure the decision rests on: if it writes one operation per batch, the
        // coalescing item 4 wanted upstream cannot happen downstream of it.
        double? batches = run.Metrics.Sum("raft_wal_batches_total");
        double? operations = run.Metrics.Sum("raft_wal_operations_total");
        if (batches is > 0 && operations is not null)
            Console.WriteLine($"\n  WAL operations per batch: {operations / batches:N2}  " +
                              $"({operations:N0} operations / {batches:N0} batches)");

        if (preparP99 is double prepare and > 0 && decisionP99 is double decision)
        {
            double delta = (decision - prepare) / prepare * 100;
            Console.WriteLine($"\n  DECISION RULE: decision p99 {decision:N1} ms vs prepare p99 {prepare:N1} ms " +
                              $"-> {delta:+0.0;-0.0}% (reopen only above +{ReopenThresholdPercent:N0}%)");
        }
    }

    private static void Report(RunArtifacts run, string label, string metric, string labels = "")
    {
        if (run.Metrics.Count(metric, labels) is not > 0)
            return;

        Console.WriteLine($"  {label,-12}{"",-22}{run.Metrics.Count(metric, labels),12:N0}" +
                          $"{run.Metrics.Mean(metric, labels),10:N2}" +
                          $"{run.Metrics.Quantile(metric, 0.50, labels),8:N1}" +
                          $"{run.Metrics.Quantile(metric, 0.99, labels),8:N1}");
    }
}
