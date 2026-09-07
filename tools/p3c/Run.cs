using System.Globalization;
using System.Text.Json;

namespace P3c;

/// <summary>
/// One Caraxes run directory: its verdict, its workload summary, its reconciliation, and the metric
/// series the collector sampled while it ran.
/// </summary>
public sealed class RunArtifacts
{
    public string Name { get; }
    public string Dir { get; }
    public string ArtifactDir { get; }
    public JsonElement Scenario { get; }
    public JsonElement Summary { get; }
    public JsonElement? Reconciliation { get; }
    public Scrape Metrics { get; }

    private RunArtifacts(string dir, JsonElement scenario, JsonElement summary, JsonElement? reconciliation, Scrape metrics)
    {
        Dir = dir;
        Name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
        ArtifactDir = Path.Combine(dir, "artifacts", "run");
        Scenario = scenario;
        Summary = summary;
        Reconciliation = reconciliation;
        Metrics = metrics;
    }

    public static RunArtifacts? TryLoad(string dir)
    {
        string artifacts = Path.Combine(dir, "artifacts", "run");
        string scenarioPath = Path.Combine(dir, "scenario.json");
        string summaryPath = Path.Combine(artifacts, "summary.json");
        if (!File.Exists(scenarioPath) || !File.Exists(summaryPath))
            return null;

        string reconPath = Path.Combine(artifacts, "reconciliation.json");
        return new RunArtifacts(
            dir,
            JsonDocument.Parse(File.ReadAllText(scenarioPath)).RootElement.Clone(),
            JsonDocument.Parse(File.ReadAllText(summaryPath)).RootElement.Clone(),
            File.Exists(reconPath) ? JsonDocument.Parse(File.ReadAllText(reconPath)).RootElement.Clone() : null,
            Scrape.LoadRun(dir, "kahuna_", "camus_", "raft_wal_"));
    }

    public static RunArtifacts Load(string dir)
        => TryLoad(dir) ?? throw new FileNotFoundException($"not a run directory (no scenario.json / summary.json): {dir}");

    public double Ops => Summary.GetProperty("AchievedOpsPerSec").GetDouble();
    public long Completed => Summary.GetProperty("Completed").GetInt64();
    public long Conflicts => Summary.GetProperty("Conflicts").GetInt64();
    public long Indeterminate => Summary.GetProperty("Indeterminate").GetInt64();
    public long Failed => Summary.GetProperty("Failed").GetInt64();

    public double Latency(string kind, string percentile)
        => Summary.GetProperty(kind).GetProperty(percentile).GetDouble();

    public bool Passed => Scenario.GetProperty("verdict").GetProperty("Passed").GetBoolean();

    public IEnumerable<string> Notes
    {
        get
        {
            foreach (JsonElement note in Scenario.GetProperty("verdict").GetProperty("Notes").EnumerateArray())
                yield return note.GetString() ?? "";
        }
    }

    public string? Note(string contains)
        => Notes.FirstOrDefault(n => n.Contains(contains, StringComparison.Ordinal));

    /// <summary>
    /// Mean cost of one durable Raft write. The admissibility number for every comparison in this
    /// campaign: it is the host's durability path rather than anything the code under test decides, and
    /// two runs that disagree about it are not comparable however close their throughput looks.
    /// </summary>
    public double? RaftWriteMeanMs => Metrics.Mean("kahuna_kv_write_raft_duration_milliseconds");

    /// <summary>
    /// The last <paramref name="tail"/> samples of a gauge, per node, from the per-second series. Used to
    /// ask whether a population was still growing when the collector stopped.
    /// </summary>
    public Dictionary<string, (double First, double Last, double Max)> GaugeTail(IReadOnlySet<string> metrics, int tail = 12)
    {
        string path = Path.Combine(ArtifactDir, "node-metrics.csv");
        Dictionary<string, List<(long Ts, double Value)>> series = [];
        if (!File.Exists(path))
            return [];

        bool first = true;
        foreach (string line in File.ReadLines(path))
        {
            if (first) { first = false; continue; }

            string[] parts = line.Split(',');
            if (parts.Length < 5 || !metrics.Contains(parts[2]))
                continue;
            if (!long.TryParse(parts[0], out long ts) ||
                !double.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                continue;

            string key = $"{parts[2]}@{parts[1]}";
            if (!series.TryGetValue(key, out List<(long, double)>? points))
                series[key] = points = [];
            points.Add((ts, value));
        }

        Dictionary<string, (double, double, double)> result = [];
        foreach ((string key, List<(long Ts, double Value)> points) in series)
        {
            points.Sort((a, b) => a.Ts.CompareTo(b.Ts));
            List<(long Ts, double Value)> window = points.TakeLast(tail).ToList();
            result[key] = (window[0].Value, window[^1].Value, window.Max(p => p.Value));
        }
        return result;
    }
}
