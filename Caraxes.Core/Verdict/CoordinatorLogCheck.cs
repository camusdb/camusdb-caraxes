/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Text.RegularExpressions;

namespace Caraxes.Core.Verdict;

/// <summary>
/// What the node logs say about online-schema coordinator jobs. <see cref="LogsFound"/> is false when no
/// node log was captured, in which case nothing was checked. Each entry is <c>node: Table.Element → State
/// (attempt N)</c>.
/// </summary>
public sealed record CoordinatorLogResult(bool LogsFound, IReadOnlyList<string> Resumed, IReadOnlyList<string> Abandoned);

/// <summary>
/// Finds CamusDB's online-schema coordinator in the captured node logs. The coordinator runs on the schema
/// leader and persists each job, so a leader that dies mid-change leaves the job for the next one, which
/// logs <c>Resuming coordinator job for T.E → State on database D (attempt N)</c>. That line is the only
/// proof a fault actually interrupted a schema change: a change that finished before the fault landed
/// tested nothing about recovery. A job that fails <c>MaxResumeAttempts</c> times is deleted with
/// <c>Abandoning coordinator job …</c>, which can leave its element in an intermediate state, so it fails
/// the run.
/// </summary>
public static class CoordinatorLogCheck
{
    private static readonly Regex Resumed = new(
        @"Resuming coordinator job for (\S+) → (\S+) on database (\S+) \(attempt (\d+)\)", RegexOptions.Compiled);

    private const string AbandonedMarker = "Abandoning coordinator job";

    private static readonly Regex NodeFromFile = new(@"^node-log-(.+)\.txt$", RegexOptions.Compiled);

    public static CoordinatorLogResult Scan(string outputDir)
    {
        string[] logs = Directory.Exists(outputDir) ? Directory.GetFiles(outputDir, "node-log-*.txt") : [];
        if (logs.Length == 0)
            return new CoordinatorLogResult(false, [], []);

        Array.Sort(logs, StringComparer.Ordinal);
        List<string> resumed = [];
        List<string> abandoned = [];

        foreach (string log in logs)
        {
            Match fileMatch = NodeFromFile.Match(Path.GetFileName(log));
            string node = fileMatch.Success ? fileMatch.Groups[1].Value : Path.GetFileName(log);

            foreach (string line in File.ReadLines(log))
            {
                Match m = Resumed.Match(line);
                if (m.Success)
                    resumed.Add($"{node}: {m.Groups[1].Value} → {m.Groups[2].Value} (attempt {m.Groups[4].Value})");
                else if (line.Contains(AbandonedMarker, StringComparison.Ordinal))
                    abandoned.Add($"{node}: {line.Trim()}");
            }
        }

        return new CoordinatorLogResult(true, resumed, abandoned);
    }
}
