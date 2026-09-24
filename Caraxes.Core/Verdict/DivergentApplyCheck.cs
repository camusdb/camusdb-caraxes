/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Text.RegularExpressions;

namespace Caraxes.Core.Verdict;

/// <summary>
/// What the node logs say about same-revision divergent applies. <see cref="LogsFound"/> is false when no node log was
/// captured, in which case nothing was checked and the counts mean nothing.
/// </summary>
public sealed record DivergentApplyResult(
    bool LogsFound,
    int Lines,
    IReadOnlyList<string> Keys,
    IReadOnlyDictionary<string, int> LinesByNode,
    IReadOnlyList<string> Samples);

/// <summary>
/// Finds Kahuna's same-revision collision witness in the captured node logs.
///
/// <para>Kahuna logs <c>Same-revision divergent apply for key … at revision … overwrites a different value already
/// recorded at this revision</c> when two committed log entries write different values to one key at one revision. The
/// second record then replaces the first, so one acknowledged write is gone, and Kahuna continues. Each replica applies
/// the same entry, so one collision normally shows up once per node.</para>
///
/// <para>This is a correctness signal that needs no workload model: an Elle history, a bank ledger or a conserved sum
/// may or may not notice the lost write, but the line itself says a committed write was overwritten. It was first seen
/// in an Elle run under a pause fault, where the history showed an acknowledged append that no later read contained.</para>
/// </summary>
public static class DivergentApplyCheck
{
    /// <summary>The fixed part of Kahuna's error line. Matched as a substring so a log-format prefix does not matter.</summary>
    public const string Marker = "Same-revision divergent apply for key";

    private const int MaxSamples = 10;

    private static readonly Regex KeyPattern = new(@"Same-revision divergent apply for key (\S+) at revision", RegexOptions.Compiled);

    private static readonly Regex NodeFromFile = new(@"^node-log-(.+)\.txt$", RegexOptions.Compiled);

    /// <summary>Scans every <c>node-log-{node}.txt</c> in <paramref name="outputDir"/>.</summary>
    public static DivergentApplyResult Scan(string outputDir)
    {
        string[] logs = Directory.Exists(outputDir)
            ? Directory.GetFiles(outputDir, "node-log-*.txt")
            : [];

        if (logs.Length == 0)
            return new DivergentApplyResult(false, 0, [], new Dictionary<string, int>(), []);

        Array.Sort(logs, StringComparer.Ordinal);

        int lines = 0;
        SortedSet<string> keys = new(StringComparer.Ordinal);
        Dictionary<string, int> byNode = new(StringComparer.Ordinal);
        List<string> samples = [];

        foreach (string log in logs)
        {
            Match fileMatch = NodeFromFile.Match(Path.GetFileName(log));
            string node = fileMatch.Success ? fileMatch.Groups[1].Value : Path.GetFileName(log);

            foreach (string line in File.ReadLines(log))
            {
                if (!line.Contains(Marker, StringComparison.Ordinal))
                    continue;

                lines++;
                byNode[node] = byNode.GetValueOrDefault(node) + 1;

                Match key = KeyPattern.Match(line);
                if (key.Success)
                    keys.Add(key.Groups[1].Value);

                if (samples.Count < MaxSamples)
                    samples.Add($"{node}: {line.Trim()}");
            }
        }

        return new DivergentApplyResult(true, lines, [.. keys], byNode, samples);
    }

    /// <summary>Writes every matched line, prefixed with its node, to <paramref name="path"/>.</summary>
    public static void WriteMatches(string outputDir, string path)
    {
        using StreamWriter writer = new(path);
        foreach (string log in Directory.GetFiles(outputDir, "node-log-*.txt").OrderBy(f => f, StringComparer.Ordinal))
        {
            string node = Path.GetFileNameWithoutExtension(log)["node-log-".Length..];
            foreach (string line in File.ReadLines(log))
            {
                if (line.Contains(Marker, StringComparison.Ordinal))
                    writer.WriteLine($"{node}: {line.Trim()}");
            }
        }
    }
}
