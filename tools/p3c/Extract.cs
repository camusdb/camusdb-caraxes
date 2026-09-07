using System.Text.Json;
using System.Text.Json.Nodes;

namespace P3c;

/// <summary>One run rendered as JSON, for a record that has to survive the run directory.</summary>
public static class Extract
{
    public static void Run(string runDir)
    {
        RunArtifacts run = RunArtifacts.Load(runDir);

        JsonObject stages = [];
        foreach ((string label, Func<RunArtifacts, double?> read) in Metrics.Rows)
            if (read(run) is double value)
                stages[label] = value;

        JsonObject counters = [];
        foreach ((SeriesKey key, double value) in run.Metrics.Totals.OrderBy(t => t.Key.ToString(), StringComparer.Ordinal))
            if (!key.Name.EndsWith("_bucket", StringComparison.Ordinal)
                && !key.Name.EndsWith("_sum", StringComparison.Ordinal)
                && !key.Name.EndsWith("_count", StringComparison.Ordinal))
                counters[key.ToString()] = value;

        JsonObject retention = [];
        foreach ((string key, (double first, double last, double max)) in run.GaugeTail(Metrics.RetentionGauges))
            retention[key] = new JsonObject
            {
                ["first"] = first,
                ["last"] = last,
                ["max"] = max,
                ["grew"] = last > first,
            };

        JsonObject row = new()
        {
            ["run"] = run.Name,
            ["scenario"] = run.Scenario.GetProperty("scenario").GetString(),
            ["camusdb_commit"] = run.Scenario.TryGetProperty("camusdbGitCommit", out JsonElement commit) ? commit.GetString() : null,
            ["fingerprint"] = run.Note("cluster fingerprint"),
            ["placement"] = run.Note("placement held"),
            ["passed"] = run.Passed,
            ["completed"] = run.Completed,
            ["failed"] = run.Failed,
            ["conflicts"] = run.Conflicts,
            ["indeterminate"] = run.Indeterminate,
            ["measured"] = stages,
            ["counters"] = counters,
            ["retention_tail"] = retention,
        };

        if (run.Reconciliation is JsonElement recon)
            row["reconciliation"] = JsonNode.Parse(recon.GetRawText());

        Console.WriteLine(row.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
