/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Text.RegularExpressions;

namespace Caraxes.Core.Scenario;

/// <summary>
/// The post-run proof for a scenario that changed the schema under load. A schema change that
/// returned is not a schema change that worked: the cluster has to agree on it, and the structure it
/// built has to match the data. Each listed element is checked on every node, after the workload
/// stops and while the cluster is still up:
///
/// <list type="number">
/// <item><b>Convergence.</b> Every node reports the element in the same state (readable or not), and
/// the state holds for <see cref="StableSeconds"/>, so a coordinator job that a fault interrupted has
/// been resumed or rolled back before anything is read. The state must match the element's
/// <c>expect</c>: <c>present</c>, <c>absent</c>, or <c>either</c> for a change a fault may have cut.</item>
/// <item><b>Contents.</b> An index read through <c>@{FORCE_INDEX=name}</c> holds exactly the rows the
/// primary key holds, each with the same values, and a sample of point lookups on the index finds each
/// row at its current key. A column holds its default in every row the workload never wrote. A foreign
/// key has no orphan. An assertion query returns its expected value.</item>
/// </list>
///
/// <para>The coordinator's own evidence is read from the node logs: a job it gave up on
/// (<c>Abandoning coordinator job</c>) fails the run, and <see cref="RequireResume"/> fails a run in
/// which no job was resumed, because then the fault missed the schema change it was aimed at.</para>
/// </summary>
public sealed class SchemaVerifySpec
{
    /// <summary>How long to wait for every node to agree on every element. Default 300.</summary>
    public int ConvergeTimeoutSeconds { get; set; } = 300;

    /// <summary>How long the agreed state must hold before the contents are read. A resumed job can
    /// move an element seconds after the nodes first agree. Default 30.</summary>
    public int StableSeconds { get; set; } = 30;

    /// <summary>Point lookups per index per node: rows sampled evenly over the primary key, each looked
    /// up through the index at its current key. A full index scan cannot see an entry left at a stale
    /// key when the index is not covering (the row is fetched through the entry and carries its current
    /// values); a lookup at the current key can. Default 200; 0 turns them off.</summary>
    public int ProbeRows { get; set; } = 200;

    /// <summary>Bound on one verification query. Default 300.</summary>
    public int QueryTimeoutSeconds { get; set; } = 300;

    /// <summary>Fail the run as VACUOUS when no node logged a resumed coordinator job: a scenario whose
    /// claim is "correct when the schema leader dies mid-change" did not test that claim if the change
    /// finished before the fault. Default false.</summary>
    public bool RequireResume { get; set; }

    /// <summary>The most domain and internal errors the workload may record. An online schema change is
    /// meant to be invisible to the application apart from retryable conflicts, so a write refused with a
    /// non-retryable code during one (a NOT NULL check on a column that is not yet readable, a schema pin
    /// that moved) is a failure even when the ledger reconciles. Default 0; -1 turns the check off.</summary>
    public int MaxWorkloadErrors { get; set; }

    public List<IndexExpectation> Indexes { get; set; } = [];

    public List<ColumnExpectation> Columns { get; set; } = [];

    public List<ForeignKeyExpectation> ForeignKeys { get; set; } = [];

    public List<SqlAssertion> Assertions { get; set; } = [];

    public static readonly IReadOnlyList<string> States = ["present", "absent", "either"];

    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    public void Validate()
    {
        if (ConvergeTimeoutSeconds < 1)
            throw new ScenarioException($"'schema_verify.converge_timeout_seconds' must be >= 1, got {ConvergeTimeoutSeconds}");
        if (StableSeconds < 0)
            throw new ScenarioException($"'schema_verify.stable_seconds' must be >= 0, got {StableSeconds}");
        if (ProbeRows < 0)
            throw new ScenarioException($"'schema_verify.probe_rows' must be >= 0, got {ProbeRows}");
        if (MaxWorkloadErrors < -1)
            throw new ScenarioException($"'schema_verify.max_workload_errors' must be >= -1 (-1 is off), got {MaxWorkloadErrors}");
        if (QueryTimeoutSeconds < 1)
            throw new ScenarioException($"'schema_verify.query_timeout_seconds' must be >= 1, got {QueryTimeoutSeconds}");

        if (Indexes.Count + Columns.Count + ForeignKeys.Count + Assertions.Count == 0 && !RequireResume)
            throw new ScenarioException("'schema_verify' lists nothing to verify; give it indexes, columns, foreign_keys or assertions");

        foreach (IndexExpectation i in Indexes)
        {
            Names("indexes", i.Table, i.Name);
            Name("indexes", i.PrimaryKey);
            if (i.Columns.Count == 0)
                throw new ScenarioException($"'schema_verify.indexes' {i.Name}: list the key columns in 'columns'");
            foreach (string c in i.Columns.Concat(i.Include))
                Name("indexes", c);
            State("indexes", i.Name, i.Expect);
        }

        foreach (ColumnExpectation c in Columns)
        {
            Names("columns", c.Table, c.Name);
            Name("columns", c.PrimaryKey);
            State("columns", c.Name, c.Expect);
        }

        foreach (ForeignKeyExpectation f in ForeignKeys)
        {
            Names("foreign_keys", f.Table, f.Name);
            Name("foreign_keys", f.Column);
            Name("foreign_keys", f.Parent);
            Name("foreign_keys", f.ParentColumn);
            State("foreign_keys", f.Name, f.Expect);
        }

        foreach (SqlAssertion a in Assertions)
        {
            if (string.IsNullOrWhiteSpace(a.Sql))
                throw new ScenarioException("'schema_verify.assertions': every assertion needs 'sql'");
            if (a.EqualsValue is null)
                throw new ScenarioException($"'schema_verify.assertions' '{a.Sql}': give the expected first value in 'equals'");
        }
    }

    private static void Names(string list, string table, string name)
    {
        Name(list, table);
        Name(list, name);
    }

    private static void Name(string list, string name)
    {
        if (string.IsNullOrEmpty(name) || !Identifier.IsMatch(name))
            throw new ScenarioException($"'schema_verify.{list}': '{name}' is not a plain identifier");
    }

    private static void State(string list, string name, string expect)
    {
        if (!States.Contains(expect))
            throw new ScenarioException(
                $"'schema_verify.{list}' {name}: expect must be one of {string.Join(", ", States)}, got '{expect}'");
    }
}

/// <summary>An index the run built (or dropped). <see cref="Columns"/> are its key columns in order,
/// <see cref="Include"/> its stored columns; both are read through the index and compared with the
/// primary key's copy of the row.</summary>
public sealed class IndexExpectation
{
    public string Table { get; set; } = "";

    public string Name { get; set; } = "";

    public List<string> Columns { get; set; } = [];

    public List<string> Include { get; set; } = [];

    /// <summary>The table's primary-key column, which both reads project first to line rows up. Default <c>id</c>.</summary>
    public string PrimaryKey { get; set; } = "id";

    /// <summary>A unique index: the key columns must also be unique over the primary key's rows.</summary>
    public bool Unique { get; set; }

    public string Expect { get; set; } = "present";
}

/// <summary>A column the run added. With <see cref="Value"/>, every row must hold it (the workload never
/// writes the column, so the backfill and the inserts' default are the only writers).</summary>
public sealed class ColumnExpectation
{
    public string Table { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>The table's primary-key column, which names the rows a finding is about. Default <c>id</c>.</summary>
    public string PrimaryKey { get; set; } = "id";

    /// <summary>The value every row must hold, as the query returns it (<c>7</c>, <c>eu</c>). Null skips
    /// the comparison.</summary>
    public string? Value { get; set; }

    /// <summary>No row may hold NULL. Default true.</summary>
    public bool NotNull { get; set; } = true;

    /// <summary>Every row holds a different value: a function or sequence default (<c>gen_uuid_v7()</c>,
    /// an identity column) runs once per row.</summary>
    public bool Distinct { get; set; }

    public string Expect { get; set; } = "present";
}

/// <summary>A foreign key the run added: every non-NULL <see cref="Column"/> of <see cref="Table"/>
/// must name a row of <see cref="Parent"/>.</summary>
public sealed class ForeignKeyExpectation
{
    public string Table { get; set; } = "";

    public string Name { get; set; } = "";

    public string Column { get; set; } = "";

    public string Parent { get; set; } = "";

    public string ParentColumn { get; set; } = "id";

    public string Expect { get; set; } = "present";
}

/// <summary>A query run on every node whose first value must equal <see cref="EqualsValue"/> — for what
/// the typed checks do not cover, such as no row left behind by a bulk delete.</summary>
public sealed class SqlAssertion
{
    public string Sql { get; set; } = "";

    [YamlDotNet.Serialization.YamlMember(Alias = "equals")]
    public string? EqualsValue { get; set; }
}
