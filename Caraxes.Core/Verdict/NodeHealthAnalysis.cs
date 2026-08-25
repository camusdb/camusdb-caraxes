/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Globalization;

namespace Caraxes.Core.Verdict;

/// <summary>A contiguous run of failed <c>/ping</c> probes for one node, graded against the fault
/// timeline. An excused outage sits inside a fault window that targets the node (plus recovery
/// grace); an unexcused one means the node died on its own and must fail the run.</summary>
public sealed record NodeOutage(
    string Node,
    DateTime StartUtc,
    DateTime EndUtc,
    int Samples,
    bool Excused);

/// <summary>
/// Grades the <c>node-health.csv</c> series a <see cref="Cluster.NodeMonitor"/> produced. This is
/// the check that closes the dead-but-<c>Up</c> gap: a node process aborted mid-run while its
/// container stayed <c>Up</c>, no fault was active, and the scenario still reported PASS. Docker
/// state is therefore ignored here — only answered probes count as alive.
/// </summary>
public static class NodeHealthAnalysis
{
    /// <summary>
    /// Builds per-node outages from the probe series and excuses those a fault explains. A single
    /// failed probe is kept in the outage list but never unexcused on its own
    /// (<paramref name="minUnexcusedSamples"/> defaults to 2): one probe can lose a 5-second
    /// timeout race against a busy node, while two misses at the sampling cadence mean the node
    /// was dark for at least a full interval. A fault window excuses a sample when the sample
    /// falls between the injection and the heal plus <paramref name="recoveryGraceSeconds"/>, and
    /// the fault targets this node or the whole cluster (null target). An unhealed window (a
    /// crash fault) excuses everything after its injection.
    /// </summary>
    public static IReadOnlyList<NodeOutage> Analyze(
        string healthCsvPath,
        IReadOnlyList<FaultWindow> faultWindows,
        double recoveryGraceSeconds,
        int minUnexcusedSamples = 2)
    {
        if (!File.Exists(healthCsvPath))
            return [];

        Dictionary<string, List<(DateTime Ts, bool Reachable)>> byNode = [];
        foreach (string line in File.ReadLines(healthCsvPath).Skip(1))
        {
            string[] parts = line.Split(',');
            if (parts.Length != 3 ||
                !DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime ts))
                continue;

            (byNode.TryGetValue(parts[1], out var list) ? list : byNode[parts[1]] = []).Add((ts.ToUniversalTime(), parts[2] == "true"));
        }

        List<NodeOutage> outages = [];
        foreach ((string node, List<(DateTime Ts, bool Reachable)> samples) in byNode)
        {
            samples.Sort((a, b) => a.Ts.CompareTo(b.Ts));

            int i = 0;
            while (i < samples.Count)
            {
                if (samples[i].Reachable)
                {
                    i++;
                    continue;
                }

                int start = i;
                while (i < samples.Count && !samples[i].Reachable)
                    i++;

                int count = i - start;
                bool excused = true;
                for (int j = start; j < start + count; j++)
                    excused &= IsExcused(node, samples[j].Ts, faultWindows, recoveryGraceSeconds);

                outages.Add(new NodeOutage(
                    node,
                    samples[start].Ts,
                    samples[start + count - 1].Ts,
                    count,
                    excused || count < minUnexcusedSamples));
            }
        }

        return outages.OrderBy(o => o.StartUtc).ToList();
    }

    private static bool IsExcused(
        string node, DateTime ts, IReadOnlyList<FaultWindow> windows, double graceSeconds)
    {
        foreach (FaultWindow w in windows)
        {
            if (w.Target is not null && w.Target != node)
                continue;

            DateTime end = w.EndUtc?.AddSeconds(graceSeconds) ?? DateTime.MaxValue;
            if (ts >= w.StartUtc && ts <= end)
                return true;
        }

        return false;
    }
}
