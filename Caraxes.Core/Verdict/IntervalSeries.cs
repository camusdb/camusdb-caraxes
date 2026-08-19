/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Globalization;
using System.Text.Json;

namespace Caraxes.Core.Verdict;

/// <summary>One second of the measured workload, parsed from <c>intervals.csv</c>. <see cref="Second"/>
/// is 0 at the measured window's start; <see cref="AbsoluteUtc"/> is filled once the series is anchored
/// against <c>run-meta.json</c>, so a wall-clock fault timeline can be laid over it.</summary>
public sealed class IntervalPoint
{
    public int Second { get; init; }

    public long Offered { get; init; }

    public long Started { get; init; }

    public long Completed { get; init; }

    public long Failed { get; init; }

    public long InFlight { get; init; }

    public long ScheduleDrops { get; init; }

    public double ReadP99Ms { get; init; }

    public double WriteP99Ms { get; init; }

    public DateTime AbsoluteUtc { get; set; }

    /// <summary>Failed as a fraction of started this second; 0 when nothing was started.</summary>
    public double ErrorRate => Started > 0 ? (double)Failed / Started : 0;
}

/// <summary>
/// The measured run's per-second series with its wall-clock anchor, parsed from <c>intervals.csv</c>
/// and <c>run-meta.json</c>. The anchor is what makes fault correlation possible: without it, the
/// series and the nemesis timeline live on two unrelated clocks.
/// </summary>
public sealed class IntervalSeries
{
    public IReadOnlyList<IntervalPoint> Points { get; }

    /// <summary>Wall-clock UTC of <see cref="IntervalPoint.Second"/> 0 (measured-window start).</summary>
    public DateTime MeasureStartUtc { get; }

    private IntervalSeries(IReadOnlyList<IntervalPoint> points, DateTime measureStartUtc)
    {
        Points = points;
        MeasureStartUtc = measureStartUtc;
    }

    /// <summary>Loads the series from a workload output directory, or null when either artifact is
    /// missing or unparseable (a crashed run leaves no usable series to correlate).</summary>
    public static IntervalSeries? Load(string outputDir)
    {
        string csv = Path.Combine(outputDir, "intervals.csv");
        string meta = Path.Combine(outputDir, "run-meta.json");
        if (!File.Exists(csv) || !File.Exists(meta))
            return null;

        DateTime? measureStart = ReadMeasureStart(meta);
        if (measureStart is null)
            return null;

        List<IntervalPoint> points = ParseCsv(csv, measureStart.Value);
        return new IntervalSeries(points, measureStart.Value);
    }

    private static DateTime? ReadMeasureStart(string metaPath)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(metaPath));
            if (doc.RootElement.TryGetProperty("measureStartUtc", out JsonElement el) &&
                DateTime.TryParse(el.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out DateTime parsed))
                return parsed.ToUniversalTime();
        }
        catch (Exception)
        {
            // Fall through to null: an unparseable anchor means the series cannot be aligned.
        }

        return null;
    }

    private static List<IntervalPoint> ParseCsv(string path, DateTime measureStart)
    {
        List<IntervalPoint> points = [];
        string[] lines = File.ReadAllLines(path);

        // Header maps names to indices so a column reorder in the workload does not silently shift data.
        if (lines.Length == 0)
            return points;

        string[] header = lines[0].Split(',');
        int Idx(string name) => Array.FindIndex(header, h => h.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));

        int iSecond = Idx("second"), iOffered = Idx("offered"), iStarted = Idx("started"),
            iCompleted = Idx("completed"), iFailed = Idx("failed"), iInFlight = Idx("in_flight"),
            iDrops = Idx("schedule_drops"), iReadP99 = Idx("read_p99_ms"), iWriteP99 = Idx("write_p99_ms");

        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                continue;

            string[] f = lines[i].Split(',');

            long L(int idx) => idx >= 0 && idx < f.Length && long.TryParse(f[idx], NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : 0;
            double D(int idx) => idx >= 0 && idx < f.Length && double.TryParse(f[idx], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;

            int second = (int)L(iSecond);
            points.Add(new IntervalPoint
            {
                Second = second,
                Offered = L(iOffered),
                Started = L(iStarted),
                Completed = L(iCompleted),
                Failed = L(iFailed),
                InFlight = L(iInFlight),
                ScheduleDrops = L(iDrops),
                ReadP99Ms = D(iReadP99),
                WriteP99Ms = D(iWriteP99),
                AbsoluteUtc = measureStart.AddSeconds(second),
            });
        }

        return points;
    }
}
