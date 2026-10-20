/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using Caraxes.Core.Backup;
using Caraxes.Core.Cluster;

namespace Caraxes.Core.Nemesis;

/// <summary>A node the <c>add-node</c> fault brought into the cluster during the run, with what seeded it.</summary>
public sealed record JoinedNode(
    NodePlan Node,
    string SeededFrom,
    string? SeedLeafBackupId,
    RestoreOutcome? SeedRestore,
    IReadOnlyList<string> Seeds,
    double JoinSeconds,
    int HostedPartitions);

/// <summary>
/// What a fault may need beyond the node it targets: the shared probes, the orchestrator (to write a
/// joiner's config, copy a restore, name the backup mount), the restore bound, and the registry of
/// nodes added during the run, which the verification reads after the workload. Process and network
/// faults ignore all of it.
/// </summary>
public sealed class NemesisContext
{
    public NemesisContext(
        HttpProbes probes, ClusterOrchestrator? orchestrator = null, TimeSpan? restoreTimeout = null, string database = "caraxes")
    {
        Probes = probes;
        Orchestrator = orchestrator;
        RestoreTimeout = restoreTimeout ?? TimeSpan.FromHours(1);
        Database = database;
    }

    public HttpProbes Probes { get; }

    /// <summary>The workload's database, which the <c>sql</c> fault's statements run in.</summary>
    public string Database { get; }

    private readonly Lock sqlGate = new();

    private readonly List<SqlStatementOutcome> sqlStatements = [];

    /// <summary>Every <c>sql</c> event's outcome, in completion order. Read by the verdict and the
    /// schema verification after the workload.</summary>
    public IReadOnlyList<SqlStatementOutcome> SqlStatements
    {
        get
        {
            lock (sqlGate)
                return sqlStatements.ToList();
        }
    }

    public void RecordSql(SqlStatementOutcome outcome)
    {
        lock (sqlGate)
            sqlStatements.Add(outcome);
    }

    public ClusterOrchestrator? Orchestrator { get; }

    /// <summary>Bound on the restore that seeds a joiner; the node aborts a restore whose request is cut off.</summary>
    public TimeSpan RestoreTimeout { get; }

    /// <summary>Nodes added during the run, in join order. Appended by the fault once the node is a
    /// Voter, read by the verification.</summary>
    public List<JoinedNode> JoinedNodes { get; } = [];

    /// <summary>Every node container the <c>add-node</c> fault started, joined or not, so its log is
    /// captured with the fleet's: the log of a joiner that never became a Voter is the evidence.</summary>
    public List<NodePlan> LaunchedNodes { get; } = [];
}
