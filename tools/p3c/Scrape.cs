using System.Globalization;
using System.Text.RegularExpressions;

namespace P3c;

/// <summary>One Prometheus series: the metric name plus its labels, rendered canonically.</summary>
public readonly record struct SeriesKey(string Name, string Labels)
{
    public override string ToString() => Labels.Length == 0 ? Name : $"{Name}[{Labels}]";
}

/// <summary>
/// The per-node Prometheus text scrapes a run leaves in <c>metrics-camus*.txt</c>, summed across nodes.
/// Summing is right for this campaign: every counter here is per node, and the question is always what
/// the cluster did, not which node did it — except where a mode looks at nodes separately.
/// </summary>
public sealed class Scrape
{
    // name{label="value",...} value   — the exposition format, one sample per line.
    private static readonly Regex SampleLine =
        new(@"^([a-zA-Z_:][a-zA-Z0-9_:]*)(?:\{(.*)\})? +(\S+)$", RegexOptions.Compiled);

    private static readonly Regex LabelPair =
        new(@"(\w+)=""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);

    private readonly Dictionary<SeriesKey, double> totals = [];

    public IReadOnlyDictionary<SeriesKey, double> Totals => totals;

    /// <summary>Loads every node scrape in a run's artifact directory and sums matching series.</summary>
    public static Scrape LoadRun(string runDir, params string[] prefixes)
    {
        Scrape scrape = new();
        foreach (string path in Directory.EnumerateFiles(Path.Combine(runDir, "artifacts", "run"), "metrics-camus*.txt").Order())
            scrape.AddFile(path, prefixes);
        return scrape;
    }

    /// <summary>Loads one node's scrape, so a mode can compare nodes rather than sum them.</summary>
    public static Scrape LoadFile(string path, params string[] prefixes)
    {
        Scrape scrape = new();
        scrape.AddFile(path, prefixes);
        return scrape;
    }

    private void AddFile(string path, string[] prefixes)
    {
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            Match m = SampleLine.Match(line);
            if (!m.Success)
                continue;

            string name = m.Groups[1].Value;
            if (prefixes.Length > 0 && !prefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
                continue;

            if (!double.TryParse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                continue;

            SeriesKey key = new(name, Canonical(m.Groups[2].Value));
            totals[key] = totals.TryGetValue(key, out double sum) ? sum + value : value;
        }
    }

    /// <summary>
    /// Labels as a sorted <c>k=v,k=v</c> string, with the OpenTelemetry scope labels dropped — they are
    /// on every series and carry nothing this campaign asks about.
    /// </summary>
    private static string Canonical(string labels)
    {
        if (labels.Length == 0)
            return "";

        List<string> pairs = [];
        foreach (Match pair in LabelPair.Matches(labels))
        {
            string key = pair.Groups[1].Value;
            if (key is "otel_scope_name" or "otel_scope_version")
                continue;
            pairs.Add($"{key}={pair.Groups[2].Value}");
        }

        pairs.Sort(StringComparer.Ordinal);
        return string.Join(',', pairs);
    }

    public double? Value(string name, string labels = "")
        => totals.TryGetValue(new SeriesKey(name, labels), out double v) ? v : null;

    /// <summary>Sum of every series whose name starts with <paramref name="prefix"/>, or null if none.</summary>
    public double? Sum(string prefix)
    {
        double total = 0;
        bool found = false;
        foreach ((SeriesKey key, double value) in totals)
        {
            if (!key.Name.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            if (key.Name.EndsWith("_bucket", StringComparison.Ordinal))
                continue;
            total += value;
            found = true;
        }
        return found ? total : null;
    }

    /// <summary>Mean of a histogram: <c>_sum / _count</c>, null when it never observed anything.</summary>
    public double? Mean(string baseName, string labels = "")
    {
        double? count = Value(baseName + "_count", labels);
        double? sum = Value(baseName + "_sum", labels);
        return count is > 0 && sum is not null ? sum / count : null;
    }

    public double? Count(string baseName, string labels = "") => Value(baseName + "_count", labels);

    /// <summary>
    /// A quantile from the cumulative <c>_bucket</c> series, interpolated inside the bucket it lands in.
    /// The buckets are summed across nodes first, so this is the cluster's distribution.
    /// </summary>
    public double? Quantile(string baseName, double q, string labels = "")
    {
        List<(double Le, double Count)> buckets = [];
        string bucketName = baseName + "_bucket";

        foreach ((SeriesKey key, double value) in totals)
        {
            if (key.Name != bucketName)
                continue;

            string? le = null;
            List<string> rest = [];
            foreach (string pair in key.Labels.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (pair.StartsWith("le=", StringComparison.Ordinal))
                    le = pair[3..];
                else
                    rest.Add(pair);
            }

            if (le is null || string.Join(',', rest) != labels)
                continue;

            buckets.Add((ParseLe(le), value));
        }

        if (buckets.Count == 0)
            return null;

        buckets.Sort((a, b) => a.Le.CompareTo(b.Le));
        double total = buckets[^1].Count;
        if (total <= 0)
            return null;

        double target = q * total, prevLe = 0, prevCount = 0;
        foreach ((double le, double count) in buckets)
        {
            if (count >= target)
            {
                if (double.IsPositiveInfinity(le))
                    return prevLe;
                return count == prevCount ? le : prevLe + (le - prevLe) * (target - prevCount) / (count - prevCount);
            }
            prevLe = le;
            prevCount = count;
        }

        return null;
    }

    private static double ParseLe(string le)
        => le is "+Inf" or "Inf"
            ? double.PositiveInfinity
            : double.Parse(le, NumberStyles.Float, CultureInfo.InvariantCulture);
}
