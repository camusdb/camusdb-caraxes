/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Text.Json;
using NUnit.Framework;
using Caraxes.Core.Nemesis;
using Caraxes.Core.Scenario;
using Caraxes.Core.Schema;
using Caraxes.Core.Verdict;

namespace Caraxes.Tests;

[TestFixture]
public sealed class SchemaTests
{
    private static ScenarioSpec Read(string tail) => ScenarioSpecReader.Read($"""
        name: s
        cluster:
          name: c
        {tail}
        """);

    private static List<JsonElement[]> Rows(string json) => SqlRest.Parse($$"""{"status":"ok","rows":{{json}}}""").Rows.ToList();

    [TestCase("CREATE INDEX i ON t (a)", "/execute-sql-ddl")]
    [TestCase("  alter table t add column c int64", "/execute-sql-ddl")]
    [TestCase("DROP INDEX i ON t", "/execute-sql-ddl")]
    [TestCase("TRUNCATE TABLE t", "/execute-sql-ddl")]
    [TestCase("INSERT INTO t SELECT * FROM u", "/execute-sql-non-query")]
    [TestCase("delete from t where a < 5", "/execute-sql-non-query")]
    [TestCase("SELECT COUNT(*) FROM t", "/execute-sql-query")]
    [TestCase("SHOW CREATE TABLE t", "/execute-sql-query")]
    public void RouteFollowsTheLeadingKeyword(string sql, string route)
    {
        Assert.That(SqlRest.RouteFor(sql), Is.EqualTo(route));
    }

    [Test]
    public void ParsesTheThreeEnvelopes()
    {
        SqlAnswer query = SqlRest.Parse("""{"status":"ok","total":2,"columns":[{"name":"id","type":1},{"name":"balance","type":2}],"rows":[["a",5],["b",-3]]}""");
        Assert.That(query.Ok, Is.True);
        Assert.That(query.Columns, Is.EqualTo(new[] { "id", "balance" }));
        Assert.That(query.Rows.Select(r => SqlRest.Canonical(r[1])), Is.EqualTo(new[] { "5", "-3" }));

        SqlAnswer ddl = SqlRest.Parse("""{"status":"ok","rows":20000}""");
        Assert.That(ddl.Ok, Is.True);
        Assert.That(ddl.AffectedRows, Is.EqualTo(20000));
        Assert.That(ddl.Rows, Is.Empty);

        SqlAnswer refused = SqlRest.Parse("""{"status":"failed","code":"CADB0405","message":"Key 'x' doesn't exist in table 't'"}""");
        Assert.That(refused.Ok, Is.False);
        Assert.That(refused.Describe(), Is.EqualTo("CADB0405 Key 'x' doesn't exist in table 't'"));
    }

    [Test]
    public void LiteralQuotesStringsAndRefusesNull()
    {
        JsonElement[] row = Rows("""[[7, "o'k", null, true]]""")[0];
        Assert.That(SqlRest.Literal(row[0]), Is.EqualTo("7"));
        Assert.That(SqlRest.Literal(row[1]), Is.EqualTo("'o''k'"));
        Assert.That(SqlRest.Literal(row[2]), Is.Null);
        Assert.That(SqlRest.Literal(row[3]), Is.EqualTo("true"));
        Assert.That(SqlRest.Canonical(row[2]), Is.EqualTo("NULL"));
    }

    [Test]
    public void IndexComparison_IdenticalReadsHaveNoProblems()
    {
        List<JsonElement[]> pk = Rows("""[["a",1,10],["b",2,20],["c",3,30]]""");
        List<JsonElement[]> ix = Rows("""[["c",3,30],["a",1,10],["b",2,20]]""");
        IndexComparison cmp = IndexComparison.Compare(pk, ix);
        Assert.That(cmp.Problems(5), Is.Empty);
        Assert.That(cmp.PrimaryRows, Is.EqualTo(3));
    }

    [Test]
    public void IndexComparison_NamesEachKindOfDivergence()
    {
        List<JsonElement[]> pk = Rows("""[["a",1,10],["b",2,20],["c",3,30]]""");
        // b lost its entry; c's entry is stale (old version); a is returned twice; z is not a row.
        List<JsonElement[]> ix = Rows("""[["a",1,10],["a",1,10],["c",3,29],["z",9,90]]""");
        IndexComparison cmp = IndexComparison.Compare(pk, ix);

        Assert.That(cmp.Missing, Is.EqualTo(new[] { "b" }));
        Assert.That(cmp.Duplicated, Is.EqualTo(new[] { "a" }));
        Assert.That(cmp.Extra, Is.EqualTo(new[] { "z" }));
        Assert.That(cmp.Mismatched, Has.Count.EqualTo(1));
        Assert.That(cmp.Mismatched[0], Does.StartWith("c: index (3, 29) vs primary key (3, 30)"));
        Assert.That(cmp.Problems(5), Has.Count.EqualTo(4));
    }

    [Test]
    public void ColumnCheck_FindsNullsWrongValuesAndRepeats()
    {
        List<JsonElement[]> rows = Rows("""[["a",7],["b",null],["c",8],["d",7]]""");

        List<string> fixedValue = ColumnCheck.Problems(rows, new ColumnExpectation { Name = "tier", Value = "7" }, 5);
        Assert.That(fixedValue, Has.Count.EqualTo(2));
        Assert.That(fixedValue[0], Does.StartWith("1 row(s) hold NULL: b"));
        Assert.That(fixedValue[1], Does.StartWith("1 row(s) do not hold 7: c=8"));

        List<string> distinct = ColumnCheck.Problems(rows, new ColumnExpectation { Name = "u", NotNull = false, Distinct = true }, 5);
        Assert.That(distinct, Is.EqualTo(new[] { "1 value(s) held by more than one row: 7 x2" }));
    }

    [Test]
    public void ConstraintIsFoundInShowCreateTable()
    {
        SqlAnswer show = SqlRest.Parse("""
            {"status":"ok","columns":[{"name":"table"},{"name":"create table"}],
             "rows":[["holds","CREATE TABLE `holds` (\n  CONSTRAINT `fk_holds_account` FOREIGN KEY (`account_id`) REFERENCES `workload_accounts` (`id`)\n)"]]}
            """);
        Assert.That(SchemaVerifier.RendersConstraint(show, "fk_holds_account"), Is.True);
        Assert.That(SchemaVerifier.RendersConstraint(show, "fk_other"), Is.False);
    }

    [Test]
    public void SampleSpreadsOverTheWholeRead()
    {
        List<JsonElement[]> rows = Enumerable.Range(0, 100).Select(i => Rows($"[[{i}]]")[0]).ToList();
        List<string> picked = SchemaVerifier.Sample(rows, 4).Select(r => SqlRest.Canonical(r[0])).ToList();
        Assert.That(picked, Is.EqualTo(new[] { "0", "25", "50", "75" }));
        Assert.That(SchemaVerifier.Sample(rows, 0), Is.Empty);
        Assert.That(SchemaVerifier.Sample(rows.Take(3).ToList(), 10).Count(), Is.EqualTo(3));
    }

    [TestCase("ok", true, true, true)]
    [TestCase("ok", true, false, false)]
    [TestCase("ok", false, false, false)]
    [TestCase("error", true, false, true)]
    [TestCase("error", false, false, false)]
    [TestCase("error", true, true, false)]
    [TestCase("any", false, false, true)]
    public void StatementOutcomeMeetsItsExpectation(string expect, bool answered, bool ok, bool met)
    {
        SqlStatementOutcome o = new("CREATE INDEX i ON t (a)", "camus1", expect, DateTime.UtcNow, 1.5, answered, ok, "", "", 0);
        Assert.That(o.MetExpectation, Is.EqualTo(met));
    }

    [Test]
    public void SqlEvent_ParsesWithDefaults()
    {
        ScenarioSpec spec = Read("""
            nemesis:
              events:
                - { at: 30s, fault: sql, target: camus1, sql: "CREATE INDEX i ON t (a)" }
                - { at: 60s, fault: sql, target: leader, sql: "DELETE FROM t", expect: any, sql_timeout_seconds: 60 }
            """);
        NemesisEvent first = spec.Nemesis!.Events[0];
        Assert.That(first.Sql, Is.EqualTo("CREATE INDEX i ON t (a)"));
        Assert.That(first.Expect, Is.EqualTo("ok"));
        Assert.That(first.SqlTimeoutSeconds, Is.EqualTo(1800));
        Assert.That(spec.Nemesis.Events[1].Expect, Is.EqualTo("any"));
        Assert.That(FaultFactory.Create(first, new NemesisContext(new Caraxes.Core.Cluster.HttpProbes())).Kind, Is.EqualTo("sql"));
    }

    [TestCase("{ fault: sql, target: camus1 }", "give the statement")]
    [TestCase("{ fault: kill, target: camus1, sql: \"SELECT 1\" }", "only meaningful for the sql fault")]
    [TestCase("{ fault: sql, target: camus1, sql: \"SELECT 1\", expect: maybe }", "expect must be one of")]
    [TestCase("{ fault: sql, target: camus1, sql: \"SELECT 1\", sql_timeout_seconds: 0 }", "sql_timeout_seconds")]
    public void SqlEvent_IsValidated(string evt, string message)
    {
        NemesisException? e = Assert.Throws<NemesisException>(() => Read($"""
            nemesis:
              events:
                - {evt}
            """));
        Assert.That(e!.Message, Does.Contain(message));
    }

    [Test]
    public void RandomNemesisCannotDrawSql()
    {
        NemesisException? e = Assert.Throws<NemesisException>(() => Read("""
            nemesis:
              random: { faults: [kill, sql] }
            """));
        Assert.That(e!.Message, Does.Contain("cannot draw 'sql'"));
    }

    [Test]
    public void SchemaVerify_ParsesWithDefaults()
    {
        ScenarioSpec spec = Read("""
            schema_verify:
              require_resume: true
              indexes:
                - { table: workload_accounts, name: acct_balance, columns: [balance], include: [version], expect: either }
              columns:
                - { table: workload_accounts, name: tier, value: "7" }
              foreign_keys:
                - { table: holds, name: fk_holds_account, column: account_id, parent: workload_accounts }
              assertions:
                - { sql: "SELECT COUNT(*) FROM holds WHERE amount < 5", equals: "0" }
            """);
        SchemaVerifySpec v = spec.SchemaVerify!;
        Assert.That(v.ConvergeTimeoutSeconds, Is.EqualTo(300));
        Assert.That(v.StableSeconds, Is.EqualTo(30));
        Assert.That(v.ProbeRows, Is.EqualTo(200));
        Assert.That(v.RequireResume, Is.True);
        Assert.That(v.Indexes[0].Include, Is.EqualTo(new[] { "version" }));
        Assert.That(v.Indexes[0].PrimaryKey, Is.EqualTo("id"));
        Assert.That(v.Indexes[0].Expect, Is.EqualTo("either"));
        Assert.That(v.Columns[0].Expect, Is.EqualTo("present"));
        Assert.That(v.Columns[0].NotNull, Is.True);
        Assert.That(v.ForeignKeys[0].ParentColumn, Is.EqualTo("id"));
        Assert.That(v.Assertions[0].EqualsValue, Is.EqualTo("0"));
    }

    [TestCase("schema_verify: { probe_rows: 5 }", "lists nothing to verify")]
    [TestCase("schema_verify: { indexes: [ { table: t, name: i } ] }", "list the key columns")]
    [TestCase("schema_verify: { indexes: [ { table: t, name: \"i; DROP\", columns: [a] } ] }", "not a plain identifier")]
    [TestCase("schema_verify: { columns: [ { table: t, name: c, expect: gone } ] }", "expect must be one of")]
    [TestCase("schema_verify: { assertions: [ { sql: \"SELECT 1\" } ] }", "give the expected first value")]
    [TestCase("schema_verify: { columns: [ { table: t, name: c } ], wait: 3 }", "unknown schema_verify option")]
    [TestCase("schema_verify: { columns: [ { table: t, name: c, defualt: 1 } ] }", "not valid YAML")]
    public void SchemaVerify_IsValidated(string block, string message)
    {
        ScenarioException? e = Assert.Throws<ScenarioException>(() => Read(block));
        Assert.That(e!.Message, Does.Contain(message));
    }

    [Test]
    public void CoordinatorLogCheck_CountsResumesAndAbandons()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"caraxes-coord-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            Assert.That(CoordinatorLogCheck.Scan(dir).LogsFound, Is.False);

            File.WriteAllLines(Path.Combine(dir, "node-log-camus2.txt"),
            [
                "info: Resuming coordinator job for workload_accounts.acct_balance → Public on database caraxes (attempt 1)",
                "info: unrelated",
            ]);
            File.WriteAllLines(Path.Combine(dir, "node-log-camus3.txt"),
            [
                "fail: Abandoning coordinator job workload_accounts.tier → Public on database caraxes after 5 resume attempts",
            ]);

            CoordinatorLogResult r = CoordinatorLogCheck.Scan(dir);
            Assert.That(r.LogsFound, Is.True);
            Assert.That(r.Resumed, Is.EqualTo(new[] { "camus2: workload_accounts.acct_balance → Public (attempt 1)" }));
            Assert.That(r.Abandoned, Has.Count.EqualTo(1));
            Assert.That(r.Abandoned[0], Does.StartWith("camus3: fail: Abandoning coordinator job"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

[TestFixture]
public sealed class WorkloadErrorCountsTests
{
    [Test]
    public void CountsOnlyServerRefusals()
    {
        using JsonDocument doc = JsonDocument.Parse("""
            {"total":345,"byCode":{
              "CADB0301":{"count":71,"samples":["Write:DomainError — Column 'tier' cannot be null"],
                          "classes":[{"count":71,"sample":"Write:DomainError — Column 'tier' cannot be null"}]},
              "CADB0099":{"count":89,"classes":[{"count":80,"sample":"Write:DomainError — already pinned"},{"count":9,"sample":"Read:DomainError — pinned"}]},
              "CADB0000":{"count":17,"classes":[{"count":17,"sample":"Read:DomainError — Endpoint https://camus2:5096: Error reading next message"}]},
              "CANCELED":{"count":168,"classes":[{"count":168,"sample":"Write:Indeterminate — TaskCanceledException"}]},
              "CADB0405":{"count":3,"samples":["Read:InternalError — old shape without classes"]}}}
            """);
        WorkloadErrorCounts e = WorkloadErrorCounts.Parse(doc.RootElement);
        Assert.That(e.Total, Is.EqualTo(71 + 89 + 3));
        Assert.That(e.Unattributed, Is.EqualTo(17 + 168));
        Assert.That(e.ByCode.Keys, Is.EqualTo(new[] { "CADB0099", "CADB0301", "CADB0405" }));
        Assert.That(e.ByCode["CADB0099"], Is.EqualTo((89L, "Write:DomainError — already pinned")));
        Assert.That(WorkloadErrorCounts.Read("/nonexistent/errors.json").Total, Is.EqualTo(0));
    }
}

[TestFixture]
public sealed class SqlFaultRetryTests
{
    [Test]
    public void BackoffDoublesToACeiling()
    {
        Assert.That(Enumerable.Range(1, 7).Select(a => (int)SqlFault.Backoff(a).TotalMilliseconds),
            Is.EqualTo(new[] { 250, 500, 1000, 2000, 4000, 4000, 4000 }));
    }

    [Test]
    public void RetriesDefaultToTenAndAreValidated()
    {
        ScenarioSpec spec = ScenarioSpecReader.Read("""
            name: s
            cluster:
              name: c
            nemesis:
              events:
                - { fault: sql, target: camus1, sql: "CREATE INDEX i ON t (a)" }
            """);
        Assert.That(spec.Nemesis!.Events[0].SqlRetries, Is.EqualTo(10));
        Assert.Throws<NemesisException>(() => ScenarioSpecReader.Read("""
            name: s
            cluster:
              name: c
            nemesis:
              events:
                - { fault: sql, target: camus1, sql: "SELECT 1", sql_retries: -1 }
            """));
    }

    [Test]
    public void DescribeNamesTheAttempt()
    {
        SqlStatementOutcome o = new("CREATE INDEX i ON t (a)", "camus1", "ok", DateTime.UtcNow, 3.2, true, true, "", "", 0, 4);
        Assert.That(o.Describe(), Does.Contain("[attempt 4; the earlier ones were refused as retryable]"));
    }
}
