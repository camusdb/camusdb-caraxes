using System.Globalization;

namespace P3c;

/// <summary>
/// One row of unit costs per run, across every run whose scenario name starts with a prefix, oldest window first
/// (feature <c>da82959a</c> task 2). The fs8 → fs12 soaks lost a fifth of their throughput over five runs that each
/// changed several things; a table of what each unit of work cost is what says which run changed what.
///
/// <para>Each run's figures come from its <c>unit-costs.json</c> when that was written by the current schema, and are
/// computed and cached there otherwise, so the first pass over the runs directory is the backfill.</para>
/// </summary>
public static class Trend
{
    public static int Run(IReadOnlyList<string> args)
    {
        string? prefix = null;
        string runsDir = Path.Combine("runs", "scenarios");
        DateTime? since = null;

        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--runs" when i + 1 < args.Count:
                    runsDir = args[++i];
                    break;
                case "--since" when i + 1 < args.Count:
                    since = DateTime.Parse(args[++i], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
                    break;
                default:
                    prefix ??= args[i];
                    break;
            }
        }

        if (prefix is null)
        {
            Console.Error.WriteLine("usage: p3c trend <scenario-prefix> [--since yyyy-MM-dd] [--runs <runs/scenarios>]");
            return 1;
        }

        IReadOnlyList<UnitCosts.Costs> rows = Collect(runsDir, prefix, since);
        if (rows.Count == 0)
        {
            Console.Error.WriteLine($"no run under {runsDir} with a scenario name starting '{prefix}'{(since is null ? "" : $" since {since:yyyy-MM-dd}")}");
            return 1;
        }
        Print(rows);
        return 0;
    }

    public static IReadOnlyList<UnitCosts.Costs> Collect(string runsDir, string prefix, DateTime? since)
    {
        List<string> dirs = [];
        foreach (string dir in Directory.EnumerateDirectories(runsDir))
        {
            string? scenario = UnitCosts.ReadScenarioName(dir);
            string meta = Path.Combine(dir, "artifacts", "run", "run-meta.json");
            if (scenario is null || !scenario.StartsWith(prefix, StringComparison.Ordinal) || !File.Exists(meta))
                continue;
            if (since is DateTime s && UnitCosts.ReadMeta(meta).Start < s)
                continue;
            dirs.Add(dir);
        }

        UnitCosts.Costs?[] costs = new UnitCosts.Costs?[dirs.Count];
        Parallel.For(0, dirs.Count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i => costs[i] = UnitCosts.LoadOrCompute(dirs[i]));
        return [.. costs.OfType<UnitCosts.Costs>().OrderBy(c => c.MeasureStartUtc)];
    }

    /// <summary>A markdown table (pastes into a Vorpal record as is). A cell that moved more than
    /// <see cref="UnitCosts.FlagMove"/> from the row above carries a <c>*</c>.</summary>
    public static void Print(IReadOnlyList<UnitCosts.Costs> rows)
    {
        List<string> header = ["window start (UTC)", "run", "stack", "CamusDB", "min", "ops/s", .. UnitCosts.Rows.Select(r => r.Label)];
        Console.WriteLine("| " + string.Join(" | ", header) + " |");
        Console.WriteLine("|" + string.Join("|", header.Select((_, i) => i < 4 ? "---" : "---:")) + "|");

        UnitCosts.Costs? previous = null;
        foreach (UnitCosts.Costs c in rows)
        {
            List<string> cells =
            [
                c.MeasureStartUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                c.Run,
                c.Stack?.Replace("Kahuna ", "").Replace("Kommander ", "") ?? "-",
                c.CamusDbCommit ?? "-",
                (c.MeasureSeconds / 60).ToString(CultureInfo.InvariantCulture),
                c.OpsPerSecond is double o ? o.ToString("F0", CultureInfo.InvariantCulture) : "-",
            ];
            foreach ((_, string format, Func<UnitCosts.Costs, double?> read) in UnitCosts.Rows)
            {
                if (read(c) is not double v)
                {
                    cells.Add("-");
                    continue;
                }
                bool moved = previous is not null && read(previous) is double b && b > 0 && Math.Abs(v / b - 1) > UnitCosts.FlagMove;
                cells.Add(v.ToString(format, CultureInfo.InvariantCulture) + (moved ? "*" : ""));
            }
            Console.WriteLine("| " + string.Join(" | ", cells) + " |");
            previous = c;
        }

        Console.WriteLine();
        Console.WriteLine($"stack = Kahuna / Kommander; `*` = moved more than {UnitCosts.FlagMove:P0} from the row above. "
            + "Entries and proposals per commit (one-phase + two-phase decisions), requests per write-transaction attempt and "
            + "statement means (outcome ok) are cluster-wide; CPU, allocation and GC are the write leader's; device KB/op is "
            + "host-io.csv's written bytes per op and is blank on tmpfs runs.");
    }
}
