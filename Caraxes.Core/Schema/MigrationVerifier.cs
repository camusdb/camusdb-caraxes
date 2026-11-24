/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Diagnostics;
using System.Text.Json;
using Caraxes.Core.Cluster;
using Caraxes.Core.Nemesis;

namespace Caraxes.Core.Schema;

/// <summary>What the verification of the migrations found. <see cref="Failures"/> fail the run.</summary>
public sealed class MigrationVerifyResult
{
    public List<string> Nodes { get; } = [];

    public List<MigrationOutcome> Outcomes { get; } = [];

    public int Whole { get; set; }

    public int Absent { get; set; }

    /// <summary>Seconds until every node agreed on every migration, or null when they never did.</summary>
    public double? ConvergedAfterSeconds { get; set; }

    /// <summary>Seconds until a schema change was accepted on every node, or null when one refused to the end.</summary>
    public double? AcceptsSchemaChangesAfterSeconds { get; set; }

    public List<string> Failures { get; } = [];

    public bool Passed => Failures.Count == 0;
}

/// <summary>
/// Checks, on every live node of the quiescent cluster, what each <c>migrate</c> event left. The parts of one
/// migration (a table, a column, an index and a row, see <see cref="Migration"/>) must be all there or all gone, the
/// same on every node, and as the answer of the COMMIT said. The index must hold one entry for each row. Then each
/// node must accept a schema change: a change set that stays pending, or a DDL lock that stays held, refuses it.
/// </summary>
public sealed class MigrationVerifier
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly string database;

    private readonly IReadOnlyList<NodePlan> nodes;

    private readonly TimeSpan convergeTimeout;

    private readonly TimeSpan queryTimeout;

    public MigrationVerifier(string database, IReadOnlyList<NodePlan> nodes, TimeSpan convergeTimeout, TimeSpan queryTimeout)
    {
        this.database = database;
        this.nodes = nodes;
        this.convergeTimeout = convergeTimeout;
        this.queryTimeout = queryTimeout;
    }

    /// <summary>The parts of one migration on one node: how many of them are there, or null when a read failed.</summary>
    private sealed record Parts(bool Table, bool Column, bool Index, bool Row, string Detail)
    {
        public bool IsWhole => Table && Column && Index && Row;

        public bool IsAbsent => !Table && !Column && !Index && !Row;

        public override string ToString() =>
            $"table {Mark(Table)}, column {Mark(Column)}, index {Mark(Index)}, row {Mark(Row)}{(Detail.Length > 0 ? $" ({Detail})" : "")}";

        private static string Mark(bool present) => present ? "present" : "absent";
    }

    public async Task<MigrationVerifyResult> VerifyAsync(IReadOnlyList<MigrationOutcome> outcomes, string reportPath, CancellationToken cancellationToken)
    {
        MigrationVerifyResult result = new();
        result.Outcomes.AddRange(outcomes.OrderBy(o => o.Number));

        foreach (NodePlan node in nodes)
        {
            if (await AnswersAsync(node, cancellationToken).ConfigureAwait(false))
                result.Nodes.Add(node.Name);
        }

        List<NodePlan> live = [.. nodes.Where(n => result.Nodes.Contains(n.Name))];
        if (live.Count == 0)
            result.Failures.Add("no node answers SQL; no migration could be verified");
        else
            await ConvergeAsync(live, result, cancellationToken).ConfigureAwait(false);

        if (live.Count > 0)
            await CheckAcceptsSchemaChangesAsync(live, result, cancellationToken).ConfigureAwait(false);

        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task ConvergeAsync(List<NodePlan> live, MigrationVerifyResult result, CancellationToken cancellationToken)
    {
        Stopwatch timer = Stopwatch.StartNew();
        List<string> problems;
        int whole;

        while (true)
        {
            problems = [];
            whole = 0;

            foreach (MigrationOutcome outcome in result.Outcomes)
            {
                List<(NodePlan Node, Parts? Parts)> seen = [];
                foreach (NodePlan node in live)
                    seen.Add((node, await ReadPartsAsync(node, outcome.Number, cancellationToken).ConfigureAwait(false)));

                string? problem = Judge(outcome, seen);
                if (problem is not null)
                    problems.Add(problem);
                else if (seen[0].Parts!.IsWhole)
                    whole++;
            }

            if (problems.Count == 0 || timer.Elapsed >= convergeTimeout)
                break;

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        result.Whole = whole;
        result.Absent = result.Outcomes.Count - whole - problems.Count;

        if (problems.Count == 0)
            result.ConvergedAfterSeconds = timer.Elapsed.TotalSeconds;
        else
            result.Failures.AddRange(problems.Select(p => $"{p} (after {timer.Elapsed.TotalSeconds:0} s)"));
    }

    private static string? Judge(MigrationOutcome outcome, List<(NodePlan Node, Parts? Parts)> seen)
    {
        string All() => string.Join("; ", seen.Select(s => $"{s.Node.Name}: {s.Parts?.ToString() ?? "unreadable"}"));

        if (seen.Any(s => s.Parts is null))
            return $"migration {outcome.Number}: a node could not be read: {All()}";

        if (seen.Any(s => !s.Parts!.IsWhole && !s.Parts.IsAbsent))
            return $"migration {outcome.Number} ({outcome.End}) is applied in part: {All()}";

        if (seen.Select(s => s.Parts!.IsWhole).Distinct().Count() > 1)
            return $"migration {outcome.Number} ({outcome.End}) is on some nodes only: {All()}";

        bool whole = seen[0].Parts!.IsWhole;
        return outcome.End switch
        {
            MigrationEnd.Committed when !whole => $"migration {outcome.Number}: COMMIT answered ok and the migration is gone: {All()}",
            MigrationEnd.NotCommitted when whole => $"migration {outcome.Number}: it was refused or rolled back and is there: {All()}",
            _ => null,
        };
    }

    private async Task<Parts?> ReadPartsAsync(NodePlan node, int n, CancellationToken cancellationToken)
    {
        try
        {
            SqlAnswer table = await QueryAsync(node, $"SELECT v FROM {Migration.Table(n)}", cancellationToken).ConfigureAwait(false);
            SqlAnswer column = await QueryAsync(node, $"SELECT {Migration.Column(n)} FROM {Migration.LogTable} WHERE id = {n}", cancellationToken).ConfigureAwait(false);
            SqlAnswer row = await QueryAsync(node, $"SELECT id FROM {Migration.LogTable} WHERE id = {n}", cancellationToken).ConfigureAwait(false);
            SqlAnswer index = await QueryAsync(
                node, $"SELECT COUNT(*) FROM {Migration.WorkloadTable}@{{FORCE_INDEX={Migration.Index(n)}}} WHERE balance > -1000000000", cancellationToken).ConfigureAwait(false);

            if (IsTransient(table) || IsTransient(column) || IsTransient(row) || IsTransient(index))
                return null;

            string detail = "";
            if (table.Ok && table.Rows.Count != 1)
                detail = $"{table.Rows.Count} row(s) in {Migration.Table(n)}";

            if (index.Ok)
            {
                SqlAnswer all = await QueryAsync(node, $"SELECT COUNT(*) FROM {Migration.WorkloadTable}", cancellationToken).ConfigureAwait(false);
                if (!all.Ok)
                    return null;

                string indexed = SqlRest.Canonical(index.Rows[0][0]), stored = SqlRest.Canonical(all.Rows[0][0]);
                if (indexed != stored)
                    detail += $" index {indexed} of {stored} rows";
            }

            // A part that is there and wrong counts as absent, so the migration shows as applied in part.
            return new Parts(
                table.Ok && table.Rows.Count == 1,
                column.Ok,
                index.Ok && !detail.Contains("index", StringComparison.Ordinal),
                row.Ok && row.Rows.Count == 1,
                detail.Trim());
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>A refusal that says nothing about the schema: a fenced table, or a leader on its way.</summary>
    private static bool IsTransient(SqlAnswer answer) => !answer.Ok && SqlFault.RetryableCodes.Contains(answer.Code);

    private async Task CheckAcceptsSchemaChangesAsync(List<NodePlan> live, MigrationVerifyResult result, CancellationToken cancellationToken)
    {
        Stopwatch timer = Stopwatch.StartNew();

        foreach (NodePlan node in live)
        {
            string last = "";

            while (true)
            {
                try
                {
                    SqlAnswer answer = await SqlRest.ExecuteAsync(
                        node, database, $"CREATE TABLE IF NOT EXISTS migration_after_{node.Name} (id INT64 PRIMARY KEY)", queryTimeout, cancellationToken).ConfigureAwait(false);
                    if (answer.Ok)
                        break;
                    last = answer.Describe();
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    last = ex.Message;
                }

                if (timer.Elapsed >= convergeTimeout)
                {
                    result.Failures.Add($"{node.Name} still refuses a schema change after {timer.Elapsed.TotalSeconds:0} s: {last}");
                    return;
                }

                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        result.AcceptsSchemaChangesAfterSeconds = timer.Elapsed.TotalSeconds;
    }

    private Task<SqlAnswer> QueryAsync(NodePlan node, string sql, CancellationToken cancellationToken) =>
        SqlRest.ExecuteAsync(node, database, sql, queryTimeout, cancellationToken);

    private async Task<bool> AnswersAsync(NodePlan node, CancellationToken cancellationToken)
    {
        try
        {
            return (await SqlRest.ExecuteAsync(node, database, "SELECT 1", TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false)).Ok;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
