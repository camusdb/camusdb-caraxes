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

/// <summary>How a migration ended, as its client saw it.</summary>
public enum MigrationEnd
{
    /// <summary>COMMIT answered ok: the migration must be whole on every node.</summary>
    Committed,

    /// <summary>ROLLBACK answered ok, or the server refused a statement or the COMMIT with a definite code: nothing may stay.</summary>
    NotCommitted,

    /// <summary>No answer to the COMMIT, or one that resolves nothing: whole or absent, the same on every node.</summary>
    Unknown,
}

/// <summary>What one <c>migrate</c> event did.</summary>
public sealed record MigrationOutcome(int Number, string Node, MigrationEnd End, double Seconds, int Attempts, string Detail)
{
    public string Describe() => $"migration {Number} on {Node}: {End} in {Seconds:0.0} s, {Attempts} attempt(s){(Detail.Length > 0 ? $" ({Detail})" : "")}";
}

/// <summary>
/// The fixed shape of migration N, shared by the fault that runs it and by <see cref="MigrationVerifier"/>: an index on
/// the table the workload writes, a new table with a row, a new column, and a row that uses it. One statement of each
/// kind that a transaction with schema changes commits in a different way.
/// </summary>
public static class Migration
{
    public const string WorkloadTable = "workload_accounts";

    /// <summary>Its row N is written by migration N, so it says whether the rows of the transaction committed.</summary>
    public const string LogTable = "migration_log";

    public static string Table(int n) => $"migration_{n}";

    public static string Column(int n) => $"m{n}";

    public static string Index(int n) => $"migration_ix_{n}";

    public static string CreateLogTable => $"CREATE TABLE IF NOT EXISTS {LogTable} (id INT64 PRIMARY KEY)";

    public static IReadOnlyList<string> Statements(int n) =>
    [
        $"CREATE INDEX {Index(n)} ON {WorkloadTable} (balance)",
        $"CREATE TABLE {Table(n)} (id INT64 PRIMARY KEY, v INT64)",
        $"INSERT INTO {Table(n)} (id, v) VALUES (1, {n})",
        $"ALTER TABLE {LogTable} ADD COLUMN {Column(n)} INT64",
        $"INSERT INTO {LogTable} (id, {Column(n)}) VALUES ({n}, {n})",
    ];
}

/// <summary>
/// Runs one schema migration in one explicit transaction on the target node while the workload runs, then commits it
/// or, with <c>rollback: true</c>, rolls it back. A kill or a leader move can be timed to land inside it.
///
/// <para>Retries. A migration that the server refuses with a retryable code is run again from BEGIN, up to
/// <c>sql_retries</c> times, as a migration tool does. A COMMIT that answers "not resolved" (<c>CADB0509</c>) is sent
/// again on the same transaction, never replayed. A COMMIT that nobody answers ends the event with
/// <see cref="MigrationEnd.Unknown"/>: the question of the run is what it left, and
/// <see cref="MigrationVerifier"/> answers it.</para>
/// </summary>
public sealed class MigrateFault : IFault
{
    private const string UnresolvedCommit = "CADB0509";

    private readonly NemesisContext context;

    private readonly int number;

    private readonly bool rollback;

    private readonly TimeSpan timeout;

    private readonly int retries;

    private string? lastOutcome;

    public MigrateFault(NemesisContext context, int number, bool rollback, TimeSpan timeout, int retries)
    {
        this.context = context;
        this.number = number;
        this.rollback = rollback;
        this.timeout = timeout;
        this.retries = retries;
    }

    public string Kind => "migrate";

    public bool Healable => true;

    public string Describe(NodePlan target) => lastOutcome ?? $"migration {number} on {target.Name}";

    public async Task InjectAsync(NodePlan target, ClusterPlan plan, CancellationToken cancellationToken)
    {
        Stopwatch clock = Stopwatch.StartNew();
        MigrationEnd end = MigrationEnd.NotCommitted;
        string detail = "";
        int attempt = 0;

        try
        {
            while (true)
            {
                attempt++;
                (end, detail, bool again) = await RunOnceAsync(target, cancellationToken).ConfigureAwait(false);
                if (!again || attempt > retries)
                    break;

                await Task.Delay(SqlFault.Backoff(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            MigrationOutcome outcome = new(number, target.Name, end, clock.Elapsed.TotalSeconds, attempt, detail);
            context.RecordMigration(outcome);
            lastOutcome = outcome.Describe();
        }
    }

    /// <summary>One run from BEGIN. <c>Again</c> says that the server asked for a replay.</summary>
    private async Task<(MigrationEnd End, string Detail, bool Again)> RunOnceAsync(NodePlan target, CancellationToken cancellationToken)
    {
        SqlSession session;

        // Nothing of the migration was sent for a commit yet, so a lost node or a refusal leaves nothing.
        try
        {
            SqlAnswer log = await SqlRest.ExecuteAsync(target, context.Database, Migration.CreateLogTable, timeout, cancellationToken).ConfigureAwait(false);
            if (!log.Ok)
                return (MigrationEnd.NotCommitted, log.Describe(), SqlFault.RetryableCodes.Contains(log.Code));

            session = await SqlSession.StartAsync(target, context.Database, timeout, cancellationToken).ConfigureAwait(false);

            foreach (string sql in Migration.Statements(number))
            {
                SqlAnswer answer = await session.ExecuteAsync(sql, cancellationToken).ConfigureAwait(false);
                if (answer.Ok)
                    continue;

                await TryRollbackAsync(session, cancellationToken).ConfigureAwait(false);
                return (MigrationEnd.NotCommitted, $"{answer.Describe()} — {SqlFault.Abbreviate(sql)}", SqlFault.RetryableCodes.Contains(answer.Code));
            }

            if (rollback)
            {
                SqlAnswer rolledBack = await session.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return (MigrationEnd.NotCommitted, $"rolled back on purpose: {rolledBack.Describe()}", false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return (MigrationEnd.NotCommitted, $"{ex.GetType().Name}: {ex.Message}", false);
        }

        for (int attempt = 1; ; attempt++)
        {
            SqlAnswer commit;
            try
            {
                commit = await session.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return (MigrationEnd.Unknown, $"COMMIT: {ex.GetType().Name}: {ex.Message}", false);
            }

            if (commit.Ok)
                return (MigrationEnd.Committed, "", false);

            if (commit.Code != UnresolvedCommit)
                return (SqlFault.RetryableCodes.Contains(commit.Code) ? MigrationEnd.NotCommitted : MigrationEnd.Unknown,
                    $"COMMIT: {commit.Describe()}", SqlFault.RetryableCodes.Contains(commit.Code));

            if (attempt > retries)
                return (MigrationEnd.Unknown, $"COMMIT: {commit.Describe()}", false);

            await Task.Delay(SqlFault.Backoff(attempt), CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task TryRollbackAsync(SqlSession session, CancellationToken cancellationToken)
    {
        try
        {
            await session.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The server ends the transaction of a client that is gone.
        }
    }

    /// <summary>Nothing to undo; the heal only closes the window.</summary>
    public Task HealAsync(NodePlan target, ClusterPlan plan, CancellationToken cancellationToken) => Task.CompletedTask;
}
