/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Diagnostics;
using Caraxes.Core.Cluster;
using Caraxes.Core.Schema;

namespace Caraxes.Core.Nemesis;

/// <summary>What one <c>sql</c> event did: the statement, the node it was sent to, what the scenario
/// expected of it, and how it ended. <see cref="Answered"/> is false when no node answered (the
/// receiving node died, the request outlived its bound, or the run ended), which for a schema change
/// is the ambiguous case: the coordinator may still finish it on the next schema leader.</summary>
public sealed record SqlStatementOutcome(
    string Sql,
    string Node,
    string Expect,
    DateTime IssuedUtc,
    double Seconds,
    bool Answered,
    bool Ok,
    string Code,
    string Message,
    int AffectedRows,
    int Attempts = 1)
{
    /// <summary>Whether the outcome is one the scenario allowed.</summary>
    public bool MetExpectation => Expect switch
    {
        "ok" => Ok,
        "error" => Answered && !Ok,
        _ => true,
    };

    public string Describe()
    {
        string head = !Answered
            ? $"CUT after {Seconds:0.0} s (no answer: {Message})"
            : Ok
                ? $"ok in {Seconds:0.0} s ({AffectedRows:N0} row(s))"
                : $"REFUSED {Code} after {Seconds:0.0} s: {Message}";
        string tries = Attempts > 1 ? $" [attempt {Attempts}; the earlier ones were refused as retryable]" : "";
        return $"{head}{tries} — {SqlFault.Abbreviate(Sql)}";
    }
}

/// <summary>
/// Runs one SQL statement against the target node while the workload runs: an online schema change
/// (<c>CREATE INDEX</c>, <c>ALTER TABLE ... ADD COLUMN</c>, <c>ADD CONSTRAINT ... FOREIGN KEY</c>,
/// <c>DROP INDEX</c>) or a bulk write (<c>INSERT ... SELECT</c>, a large <c>DELETE</c>) scheduled like
/// a fault, so it can overlap a kill, a partition or a pause and the correlator grades its cost.
///
/// <para>The window. The injection is the statement itself, sent to answered; the heal is a no-op that
/// closes the window <c>duration</c> later. A schema change on a large table runs for as long as its
/// backfill, so the window covers the whole staged rollout, and another event can be timed to land
/// inside it. Two <c>sql</c> events on one node must not overlap: windows are keyed by kind and node.</para>
///
/// <para>Retries. A statement refused with a retryable code (<c>CADB0502</c> conflict, <c>CADB0504</c>
/// must-retry) is sent again after a short backoff, up to <c>sql_retries</c> times, as an operator or a
/// migration tool would: the serializable retry contract says no write of the refused attempt survived.
/// The attempt count goes to the outcome, because a schema change that needs ten tries on a busy table
/// is a finding even when the tenth succeeds.</para>
///
/// <para>The outcome. A refused statement or one that no node answered is not a nemesis error: the
/// attempt happened, and what it left behind is the question. The outcome goes to the timeline and to
/// <see cref="NemesisContext.SqlStatements"/>, where the verdict holds it to the event's
/// <c>expect</c> (<c>ok</c>, the default; <c>error</c> for a statement that must be refused; <c>any</c>
/// for one a scheduled fault may cut) and <c>schema_verify</c> checks the schema it left.</para>
/// </summary>
public sealed class SqlFault : IFault
{
    public static readonly IReadOnlyList<string> Expectations = ["ok", "error", "any"];

    /// <summary>Codes the serializable retry contract says to replay from the start.</summary>
    public static readonly IReadOnlyList<string> RetryableCodes = ["CADB0502", "CADB0504"];

    private readonly NemesisContext context;

    private readonly string sql;

    private readonly string expect;

    private readonly TimeSpan timeout;

    private readonly int retries;

    private string? lastOutcome;

    public SqlFault(NemesisContext context, string sql, string expect, TimeSpan timeout, int retries = 0)
    {
        this.context = context;
        this.sql = sql;
        this.expect = expect;
        this.timeout = timeout;
        this.retries = retries;
    }

    public string Kind => "sql";

    public bool Healable => true;

    public string Describe(NodePlan target) => lastOutcome ?? $"sql on {target.Name}: {Abbreviate(sql)}";

    public async Task InjectAsync(NodePlan target, ClusterPlan plan, CancellationToken cancellationToken)
    {
        DateTime issued = DateTime.UtcNow;
        Stopwatch clock = Stopwatch.StartNew();
        SqlStatementOutcome outcome;
        int attempt = 0;
        try
        {
            while (true)
            {
                attempt++;
                SqlAnswer answer = await SqlRest.ExecuteAsync(target, context.Database, sql, timeout, cancellationToken).ConfigureAwait(false);
                if (!answer.Ok && RetryableCodes.Contains(answer.Code) && attempt <= retries)
                {
                    await Task.Delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                outcome = new SqlStatementOutcome(
                    sql, target.Name, expect, issued, clock.Elapsed.TotalSeconds, true, answer.Ok, answer.Code, answer.Message,
                    answer.AffectedRows, attempt);
                break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The run ended with the statement in flight. Recorded before the runner writes its own
            // cancellation line, so the verification still knows the statement was attempted.
            context.RecordSql(new SqlStatementOutcome(
                sql, target.Name, expect, issued, clock.Elapsed.TotalSeconds, false, false, "", "the run ended first", 0, attempt));
            throw;
        }
        catch (Exception ex)
        {
            outcome = new SqlStatementOutcome(
                sql, target.Name, expect, issued, clock.Elapsed.TotalSeconds, false, false, "", $"{ex.GetType().Name}: {ex.Message}", 0, attempt);
        }

        context.RecordSql(outcome);
        lastOutcome = outcome.MetExpectation ? outcome.Describe() : $"{outcome.Describe()} [expected {expect}]";
    }

    /// <summary>250 ms doubling to a 4 s ceiling: long enough for the conflicting transfers to commit,
    /// short enough that a few retries stay inside a fault's window.</summary>
    public static TimeSpan Backoff(int attempt) => TimeSpan.FromMilliseconds(Math.Min(4000, 250 * (1 << Math.Min(attempt - 1, 4))));

    /// <summary>Nothing to undo; the heal only closes the window.</summary>
    public Task HealAsync(NodePlan target, ClusterPlan plan, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>One line of the statement for the timeline: whitespace collapsed, cut at 160 characters.</summary>
    public static string Abbreviate(string sql)
    {
        string flat = string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= 160 ? flat : flat[..160] + "…";
    }
}
