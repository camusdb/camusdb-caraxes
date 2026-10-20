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
using Caraxes.Core.Scenario;

namespace Caraxes.Core.Schema;

/// <summary>What the schema verification found. <see cref="Failures"/> fail the run; <see cref="Notes"/>
/// are the evidence either way.</summary>
public sealed class SchemaVerifyResult
{
    public List<string> Nodes { get; } = [];

    public List<string> Notes { get; } = [];

    public List<string> Failures { get; } = [];

    /// <summary>Seconds from the start of the stage until every node agreed on every element and the
    /// agreement held; null when it never did.</summary>
    public double? ConvergedAfterSeconds { get; set; }

    /// <summary>The agreed state of each element (<c>index acct_balance</c> → present).</summary>
    public Dictionary<string, string> States { get; } = [];

    public List<SqlStatementOutcome> Statements { get; } = [];

    public bool Passed => Failures.Count == 0;
}

/// <summary>
/// Runs <see cref="SchemaVerifySpec"/> against the live, quiescent cluster: waits for every node to agree
/// on every listed element, holds each one to its expected state, and then compares what each element
/// built with the data, node by node. See <see cref="SchemaVerifySpec"/> for what each check proves.
/// </summary>
public sealed class SchemaVerifier
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private const int SampleLimit = 5;

    private readonly SchemaVerifySpec spec;

    private readonly string database;

    private readonly IReadOnlyList<NodePlan> nodes;

    private readonly TimeSpan queryTimeout;

    public SchemaVerifier(SchemaVerifySpec spec, string database, IReadOnlyList<NodePlan> nodes)
    {
        this.spec = spec;
        this.database = database;
        this.nodes = nodes;
        queryTimeout = TimeSpan.FromSeconds(spec.QueryTimeoutSeconds);
    }

    /// <summary>One element the stage tracks, with the probe that says whether a node can read it.</summary>
    private sealed record Element(string Label, string Expect, Func<NodePlan, CancellationToken, Task<Presence>> Probe);

    /// <summary>A node's view of one element: readable, not readable, or unknown (the probe itself failed,
    /// which says nothing about the element and is retried).</summary>
    private readonly record struct Presence(bool? Present, string Detail)
    {
        public override string ToString() => Present switch { true => "present", false => "absent", _ => $"unknown ({Detail})" };
    }

    public async Task<SchemaVerifyResult> VerifyAsync(
        IReadOnlyList<SqlStatementOutcome> statements, string reportPath, CancellationToken cancellationToken)
    {
        SchemaVerifyResult result = new();
        result.Statements.AddRange(statements);
        foreach (SqlStatementOutcome s in statements)
            result.Notes.Add($"sql on {s.Node} (expect {s.Expect}): {s.Describe()}");

        List<NodePlan> live = [];
        foreach (NodePlan node in nodes)
        {
            if (await AnswersAsync(node, cancellationToken).ConfigureAwait(false))
                live.Add(node);
            else
                result.Notes.Add($"{node.Name} does not answer SQL; not verified (a node the run took away for good)");
        }

        result.Nodes.AddRange(live.Select(n => n.Name));
        if (live.Count == 0)
        {
            result.Failures.Add("no node answers SQL; nothing could be verified");
            Write(result, reportPath);
            return result;
        }

        List<Element> elements = Elements();
        if (elements.Count > 0)
            await ConvergeAsync(elements, live, result, cancellationToken).ConfigureAwait(false);

        if (result.Failures.Count == 0)
        {
            foreach (NodePlan node in live)
            {
                foreach (IndexExpectation index in spec.Indexes.Where(i => result.States.GetValueOrDefault(IndexLabel(i)) == "present"))
                    await CheckIndexAsync(node, index, result, cancellationToken).ConfigureAwait(false);
                foreach (ColumnExpectation column in spec.Columns.Where(c => result.States.GetValueOrDefault(ColumnLabel(c)) == "present"))
                    await CheckColumnAsync(node, column, result, cancellationToken).ConfigureAwait(false);
                foreach (ForeignKeyExpectation fk in spec.ForeignKeys.Where(f => result.States.GetValueOrDefault(ForeignKeyLabel(f)) == "present"))
                    await CheckForeignKeyAsync(node, fk, result, cancellationToken).ConfigureAwait(false);
                foreach (SqlAssertion assertion in spec.Assertions)
                    await CheckAssertionAsync(node, assertion, result, cancellationToken).ConfigureAwait(false);
            }
        }

        Write(result, reportPath);
        return result;
    }

    private async Task<bool> AnswersAsync(NodePlan node, CancellationToken cancellationToken)
    {
        try
        {
            SqlAnswer answer = await SqlRest.ExecuteAsync(node, database, "SELECT 1", TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            return answer.Ok;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static string IndexLabel(IndexExpectation i) => $"index {i.Table}.{i.Name}";

    private static string ColumnLabel(ColumnExpectation c) => $"column {c.Table}.{c.Name}";

    private static string ForeignKeyLabel(ForeignKeyExpectation f) => $"foreign key {f.Table}.{f.Name}";

    private List<Element> Elements()
    {
        List<Element> elements = [];
        foreach (IndexExpectation i in spec.Indexes)
            elements.Add(new Element(IndexLabel(i), i.Expect, (node, ct) => ProbeAsync(
                node, $"SELECT {i.Columns[0]} FROM {i.Table}@{{FORCE_INDEX={i.Name}}} LIMIT 1", ["CADB0405", "CADB0011"], null, ct)));
        foreach (ColumnExpectation c in spec.Columns)
            elements.Add(new Element(ColumnLabel(c), c.Expect, (node, ct) => ProbeAsync(
                node, $"SELECT {c.Name} FROM {c.Table}@{{FORCE_INDEX=pk}} LIMIT 1", ["CADB0404", "CADB0011"], null, ct)));
        foreach (ForeignKeyExpectation f in spec.ForeignKeys)
            elements.Add(new Element(ForeignKeyLabel(f), f.Expect, (node, ct) => ProbeAsync(
                node, $"SHOW CREATE TABLE {f.Table}", ["CADB0011"], answer => RendersConstraint(answer, f.Name), ct)));
        return elements;
    }

    /// <summary>Whether <c>SHOW CREATE TABLE</c> renders the constraint. Only a public constraint is
    /// rendered, which is exactly the "readable" state the other probes test.</summary>
    public static bool RendersConstraint(SqlAnswer answer, string name)
    {
        string needle = $"CONSTRAINT `{name}` FOREIGN KEY";
        return answer.Rows.SelectMany(r => r)
            .Any(v => v.ValueKind == JsonValueKind.String && v.GetString()!.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Runs a probe query: an ok answer means present (or what <paramref name="decide"/> says), a
    /// refusal with one of <paramref name="absentCodes"/> means absent, anything else is unknown.</summary>
    private async Task<Presence> ProbeAsync(
        NodePlan node, string sql, string[] absentCodes, Func<SqlAnswer, bool>? decide, CancellationToken cancellationToken)
    {
        try
        {
            SqlAnswer answer = await SqlRest.ExecuteAsync(node, database, sql, queryTimeout, cancellationToken).ConfigureAwait(false);
            if (answer.Ok)
                return new Presence(decide?.Invoke(answer) ?? true, "");
            if (absentCodes.Contains(answer.Code))
                return new Presence(false, answer.Code);
            return new Presence(null, answer.Describe());
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new Presence(null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Polls every element on every node until all nodes agree on each one and the agreement has not
    /// moved for <see cref="SchemaVerifySpec.StableSeconds"/>, then holds each element to its
    /// <c>expect</c>. Not converging within the bound is a failure that names each disagreement.
    /// </summary>
    private async Task ConvergeAsync(
        List<Element> elements, List<NodePlan> live, SchemaVerifyResult result, CancellationToken cancellationToken)
    {
        Stopwatch clock = Stopwatch.StartNew();
        TimeSpan bound = TimeSpan.FromSeconds(spec.ConvergeTimeoutSeconds);
        TimeSpan stable = TimeSpan.FromSeconds(spec.StableSeconds);
        string? lastAgreed = null;
        TimeSpan agreedSince = TimeSpan.Zero;
        Dictionary<string, Dictionary<string, Presence>> views = [];

        while (true)
        {
            views = [];
            foreach (Element e in elements)
            {
                Dictionary<string, Presence> perNode = [];
                foreach (NodePlan node in live)
                    perNode[node.Name] = await e.Probe(node, cancellationToken).ConfigureAwait(false);
                views[e.Label] = perNode;
            }

            string? agreed = Agreement(elements, views);
            if (agreed is not null)
            {
                if (agreed != lastAgreed)
                {
                    lastAgreed = agreed;
                    agreedSince = clock.Elapsed;
                }

                if (clock.Elapsed - agreedSince >= stable)
                    break;
            }
            else
            {
                lastAgreed = null;
            }

            if (clock.Elapsed >= bound)
            {
                foreach (Element e in elements)
                {
                    Dictionary<string, Presence> perNode = views[e.Label];
                    if (perNode.Values.Select(p => p.Present).Distinct().Count() != 1 || perNode.Values.Any(p => p.Present is null))
                        result.Failures.Add(
                            $"{e.Label}: the nodes did not agree within {spec.ConvergeTimeoutSeconds} s — " +
                            string.Join(", ", perNode.Select(kv => $"{kv.Key} {kv.Value}")));
                }

                if (result.Failures.Count == 0)
                    result.Failures.Add(
                        $"the schema kept changing for {spec.ConvergeTimeoutSeconds} s and never held still for {spec.StableSeconds} s " +
                        "(a coordinator job still moving an element?)");
                return;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        result.ConvergedAfterSeconds = Math.Round(clock.Elapsed.TotalSeconds, 1);
        foreach (Element e in elements)
        {
            bool present = views[e.Label].Values.First().Present!.Value;
            string state = present ? "present" : "absent";
            result.States[e.Label] = state;
            if (e.Expect != "either" && e.Expect != state)
                result.Failures.Add($"{e.Label}: {state} on every node, expected {e.Expect}");
            else
                result.Notes.Add($"{e.Label}: {state} on all {live.Count} node(s)" + (e.Expect == "either" ? " (either was allowed)" : ""));
        }
    }

    /// <summary>A fingerprint of the agreed state, or null while any element is unknown on a node or the
    /// nodes disagree about it.</summary>
    private static string? Agreement(List<Element> elements, Dictionary<string, Dictionary<string, Presence>> views)
    {
        List<string> parts = [];
        foreach (Element e in elements)
        {
            List<bool?> states = views[e.Label].Values.Select(p => p.Present).Distinct().ToList();
            if (states.Count != 1 || states[0] is null)
                return null;
            parts.Add($"{e.Label}={states[0]}");
        }

        return string.Join(";", parts);
    }

    /// <summary>
    /// Reads the table through the primary key and through the index and compares them row by row, then
    /// looks up a sample of rows through the index at their current key. The index read projects only
    /// key and included columns, so a covering index answers it from its own entries.
    /// </summary>
    private async Task CheckIndexAsync(NodePlan node, IndexExpectation index, SchemaVerifyResult result, CancellationToken cancellationToken)
    {
        string label = $"{node.Name}: {IndexLabel(index)}";
        List<string> columns = new[] { index.PrimaryKey }.Concat(index.Columns).Concat(index.Include).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string projection = string.Join(", ", columns);

        SqlAnswer? byPk = await QueryAsync(node, $"SELECT {projection} FROM {index.Table}@{{FORCE_INDEX=pk}}", label, result, cancellationToken).ConfigureAwait(false);
        SqlAnswer? byIndex = await QueryAsync(node, $"SELECT {projection} FROM {index.Table}@{{FORCE_INDEX={index.Name}}}", label, result, cancellationToken).ConfigureAwait(false);
        if (byPk is null || byIndex is null)
            return;

        IndexComparison cmp = IndexComparison.Compare(byPk.Rows, byIndex.Rows);
        List<string> problems = cmp.Problems(SampleLimit);

        if (index.Unique)
        {
            int keyCount = index.Columns.Count;
            List<string> dupKeys = byPk.Rows
                .GroupBy(r => string.Join("|", r.Skip(1).Take(keyCount).Select(SqlRest.Canonical)))
                .Where(g => g.Count() > 1)
                .Select(g => $"({g.Key}) x{g.Count()}")
                .ToList();
            if (dupKeys.Count > 0)
                problems.Add($"{dupKeys.Count} key value(s) held by more than one row under a unique index: {string.Join(", ", dupKeys.Take(SampleLimit))}");
        }

        int probed = 0;
        int lost = 0;
        List<string> lostSamples = [];
        foreach (JsonElement[] row in Sample(byPk.Rows, spec.ProbeRows))
        {
            List<string>? predicates = KeyPredicates(index, row);
            if (predicates is null)
                continue;

            string id = SqlRest.Canonical(row[0]);
            SqlAnswer? hit = await QueryAsync(
                node, $"SELECT {index.PrimaryKey} FROM {index.Table}@{{FORCE_INDEX={index.Name}}} WHERE {string.Join(" AND ", predicates)}",
                label, result, cancellationToken).ConfigureAwait(false);
            if (hit is null)
                return;

            probed++;
            if (!hit.Rows.Any(r => r.Length > 0 && SqlRest.Canonical(r[0]) == id))
            {
                lost++;
                if (lostSamples.Count < SampleLimit)
                    lostSamples.Add($"{id} at ({string.Join(", ", predicates)})");
            }
        }

        if (lost > 0)
            problems.Add($"{lost} of {probed} point lookup(s) through the index did not find the row at its current key: {string.Join("; ", lostSamples)}");

        if (problems.Count == 0)
        {
            result.Notes.Add($"{label}: {cmp.PrimaryRows:N0} row(s) by primary key, {cmp.IndexRows:N0} through the index, identical; {probed} point lookup(s) found their row");
            return;
        }

        foreach (string problem in problems)
            result.Failures.Add($"{label}: {problem}");
    }

    private static List<string>? KeyPredicates(IndexExpectation index, JsonElement[] row)
    {
        List<string> predicates = [];
        for (int k = 0; k < index.Columns.Count; k++)
        {
            string? literal = SqlRest.Literal(row[1 + k]);
            if (literal is null)
                return null;
            predicates.Add($"{index.Columns[k]} = {literal}");
        }
        return predicates;
    }

    /// <summary>Up to <paramref name="count"/> rows spread evenly over the read, so the sample covers
    /// the whole key space rather than its first page.</summary>
    public static IEnumerable<JsonElement[]> Sample(IReadOnlyList<JsonElement[]> rows, int count)
    {
        if (count <= 0 || rows.Count == 0)
            yield break;

        int step = Math.Max(1, rows.Count / count);
        for (int i = 0, taken = 0; i < rows.Count && taken < count; i += step, taken++)
            yield return rows[i];
    }

    private async Task CheckColumnAsync(NodePlan node, ColumnExpectation column, SchemaVerifyResult result, CancellationToken cancellationToken)
    {
        string label = $"{node.Name}: {ColumnLabel(column)}";
        SqlAnswer? read = await QueryAsync(
            node, $"SELECT {column.PrimaryKey}, {column.Name} FROM {column.Table}@{{FORCE_INDEX=pk}}", label, result, cancellationToken).ConfigureAwait(false);
        if (read is null)
            return;

        List<string> problems = ColumnCheck.Problems(read.Rows, column, SampleLimit);
        if (problems.Count == 0)
        {
            result.Notes.Add($"{label}: {read.Rows.Count:N0} row(s), every one " +
                             (column.Value is null ? "" : $"holds {column.Value}") +
                             (column.Distinct ? " distinct" : "") + (column.NotNull ? " and none NULL" : ""));
            return;
        }

        foreach (string problem in problems)
            result.Failures.Add($"{label}: {problem}");
    }

    private async Task CheckForeignKeyAsync(NodePlan node, ForeignKeyExpectation fk, SchemaVerifyResult result, CancellationToken cancellationToken)
    {
        string label = $"{node.Name}: {ForeignKeyLabel(fk)}";
        SqlAnswer? children = await QueryAsync(node, $"SELECT {fk.Column} FROM {fk.Table}@{{FORCE_INDEX=pk}}", label, result, cancellationToken).ConfigureAwait(false);
        SqlAnswer? parents = await QueryAsync(node, $"SELECT {fk.ParentColumn} FROM {fk.Parent}@{{FORCE_INDEX=pk}}", label, result, cancellationToken).ConfigureAwait(false);
        if (children is null || parents is null)
            return;

        HashSet<string> keys = parents.Rows.Select(r => SqlRest.Canonical(r[0])).ToHashSet();
        List<string> orphans = children.Rows
            .Select(r => r[0])
            .Where(v => v.ValueKind != JsonValueKind.Null)
            .Select(SqlRest.Canonical)
            .Where(v => !keys.Contains(v))
            .ToList();

        if (orphans.Count == 0)
        {
            result.Notes.Add($"{label}: {children.Rows.Count:N0} child row(s), none orphaned ({parents.Rows.Count:N0} parent row(s))");
            return;
        }

        result.Failures.Add(
            $"{label}: {orphans.Count} child row(s) reference no {fk.Parent} row: {string.Join(", ", orphans.Distinct().Take(SampleLimit))}");
    }

    private async Task CheckAssertionAsync(NodePlan node, SqlAssertion assertion, SchemaVerifyResult result, CancellationToken cancellationToken)
    {
        string label = $"{node.Name}: assertion '{SqlFault.Abbreviate(assertion.Sql)}'";
        SqlAnswer? answer = await QueryAsync(node, assertion.Sql, label, result, cancellationToken).ConfigureAwait(false);
        if (answer is null)
            return;

        string? got = answer.Rows.Count > 0 && answer.Rows[0].Length > 0 ? SqlRest.Canonical(answer.Rows[0][0]) : null;
        if (got == assertion.EqualsValue)
            result.Notes.Add($"{label} = {got}");
        else
            result.Failures.Add($"{label} returned {got ?? "no row"}, expected {assertion.EqualsValue}");
    }

    /// <summary>Runs a verification query, retrying a transport failure twice. A refusal, or a third
    /// transport failure, is a failure of the check (null): a check that could not read must not read
    /// as one that passed.</summary>
    private async Task<SqlAnswer?> QueryAsync(NodePlan node, string sql, string label, SchemaVerifyResult result, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                SqlAnswer answer = await SqlRest.ExecuteAsync(node, database, sql, queryTimeout, cancellationToken).ConfigureAwait(false);
                if (answer.Ok)
                    return answer;
                result.Failures.Add($"{label}: '{SqlFault.Abbreviate(sql)}' was refused: {answer.Describe()}");
                return null;
            }
            catch (Exception ex) when (attempt < 3 && (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                result.Failures.Add($"{label}: '{SqlFault.Abbreviate(sql)}' did not complete: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }
    }

    private static void Write(SchemaVerifyResult result, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            passed = result.Passed,
            nodes = result.Nodes,
            convergedAfterSeconds = result.ConvergedAfterSeconds,
            states = result.States,
            statements = result.Statements,
            failures = result.Failures,
            notes = result.Notes,
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>
/// The row-by-row comparison of a primary-key read and an index read of one table. Both reads project
/// the primary key first. A row missing from the index read is a lost index entry; a row the index
/// returns twice, or that the primary key does not hold, is a stale one; a row whose values differ is an
/// entry the index did not update (for a covering index the values come from the entry itself).
/// </summary>
public sealed class IndexComparison
{
    public int PrimaryRows { get; private init; }

    public int IndexRows { get; private init; }

    public List<string> DuplicatePrimary { get; } = [];

    public List<string> Missing { get; } = [];

    public List<string> Extra { get; } = [];

    public List<string> Duplicated { get; } = [];

    public List<string> Mismatched { get; } = [];

    public static IndexComparison Compare(IReadOnlyList<JsonElement[]> byPk, IReadOnlyList<JsonElement[]> byIndex)
    {
        IndexComparison cmp = new() { PrimaryRows = byPk.Count, IndexRows = byIndex.Count };

        Dictionary<string, string> primary = [];
        foreach (JsonElement[] row in byPk)
        {
            string id = SqlRest.Canonical(row[0]);
            if (!primary.TryAdd(id, Values(row)))
                cmp.DuplicatePrimary.Add(id);
        }

        Dictionary<string, int> seen = [];
        foreach (JsonElement[] row in byIndex)
        {
            string id = SqlRest.Canonical(row[0]);
            int count = seen.GetValueOrDefault(id) + 1;
            seen[id] = count;
            if (count == 2)
                cmp.Duplicated.Add(id);

            if (!primary.TryGetValue(id, out string? expected))
                cmp.Extra.Add(id);
            else if (count == 1 && expected != Values(row))
                cmp.Mismatched.Add($"{id}: index ({Values(row)}) vs primary key ({expected})");
        }

        foreach (string id in primary.Keys)
            if (!seen.ContainsKey(id))
                cmp.Missing.Add(id);

        return cmp;
    }

    private static string Values(JsonElement[] row) => string.Join(", ", row.Skip(1).Select(SqlRest.Canonical));

    public List<string> Problems(int samples)
    {
        List<string> problems = [];
        Add(problems, DuplicatePrimary, "id(s) the primary key itself returned more than once", samples);
        Add(problems, Missing, "row(s) missing from the index", samples);
        Add(problems, Extra, "index entr(ies) for rows the primary key does not hold", samples);
        Add(problems, Duplicated, "row(s) the index returned more than once", samples);
        Add(problems, Mismatched, "row(s) whose values through the index differ from the primary key", samples);
        return problems;
    }

    private static void Add(List<string> problems, List<string> found, string what, int samples)
    {
        if (found.Count > 0)
            problems.Add($"{found.Count} {what}: {string.Join("; ", found.Take(samples))}");
    }
}

/// <summary>The per-row checks of an added column over a <c>(primary key, column)</c> read.</summary>
public static class ColumnCheck
{
    public static List<string> Problems(IReadOnlyList<JsonElement[]> rows, ColumnExpectation column, int samples)
    {
        List<string> problems = [];
        List<string> nulls = [];
        List<string> wrong = [];
        Dictionary<string, int> values = [];

        foreach (JsonElement[] row in rows)
        {
            string id = SqlRest.Canonical(row[0]);
            JsonElement value = row[1];
            if (value.ValueKind == JsonValueKind.Null)
            {
                nulls.Add(id);
                continue;
            }

            string text = SqlRest.Canonical(value);
            if (column.Value is not null && text != column.Value)
                wrong.Add($"{id}={text}");
            values[text] = values.GetValueOrDefault(text) + 1;
        }

        if (column.NotNull && nulls.Count > 0)
            problems.Add($"{nulls.Count} row(s) hold NULL: {string.Join(", ", nulls.Take(samples))}");
        if (wrong.Count > 0)
            problems.Add($"{wrong.Count} row(s) do not hold {column.Value}: {string.Join(", ", wrong.Take(samples))}");
        if (column.Distinct)
        {
            List<KeyValuePair<string, int>> repeated = values.Where(kv => kv.Value > 1).ToList();
            if (repeated.Count > 0)
                problems.Add($"{repeated.Count} value(s) held by more than one row: {string.Join(", ", repeated.Take(samples).Select(kv => $"{kv.Key} x{kv.Value}"))}");
        }

        return problems;
    }
}
