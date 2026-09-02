/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Text.RegularExpressions;
using Caraxes.Core.Cluster;
using Caraxes.Core.Nemesis;

namespace Caraxes.Core.Scenario;

/// <summary>
/// A self-contained test scenario: the cluster to stand up and the workload to drive against it.
/// Nemesis fault timelines and pass/fail rules arrive in later phases; a Phase 2 scenario is a
/// fault-free baseline that proves the cluster+workload path end to end.
/// </summary>
public sealed class ScenarioSpec
{
    public string Name { get; set; } = "";

    /// <summary>The cluster definition, embedded so a scenario is one self-contained file.</summary>
    public ClusterSpec Cluster { get; set; } = new();

    public WorkloadSpec Workload { get; set; } = new();

    /// <summary>Optional fault schedule driven concurrently with the workload. Absent = a fault-free
    /// baseline run.</summary>
    public NemesisSpec? Nemesis { get; set; }

    /// <summary>Pass/fail rules for the resilience the fault correlation measures. Defaulted when omitted.</summary>
    public ChecksSpec Checks { get; set; } = new();

    /// <summary>Tear the cluster down after the run. Default true; set false to leave it up for
    /// inspection (the run still completes and artifacts are collected either way).</summary>
    public bool Teardown { get; set; } = true;

    /// <summary>
    /// Seconds to wait, after seeding and before the measured window opens, for every partition to
    /// have a resolvable leader. 0 skips the wait.
    ///
    /// <para>A node answers <c>/v1/cluster/health</c> as ready before leadership has settled, and
    /// seeding itself creates the tables whose ranges then have to be placed. Measuring across that
    /// settlement charges the run for an election it did not cause, which is exactly the kind of
    /// noise that makes two runs of the same configuration disagree. Waiting costs wall-clock outside
    /// the measured window and nothing else.</para>
    ///
    /// <para>The wait is a best effort: if leadership has not settled by the deadline the run
    /// proceeds and says so, because a scenario that never starts reports nothing at all.</para>
    /// </summary>
    public int SettleSeconds { get; set; } = 30;

    /// <summary>
    /// Copy every node's container log into the run artifacts before the cluster is torn down.
    /// Default true.
    ///
    /// <para>Defaults on because a container log is destroyed with its container, and the run that
    /// most needs it is the one that already failed. Some diagnostic witnesses are log lines rather
    /// than counters — printed once at a leadership change, never scraped into the metric series —
    /// so a torn-down fleet turns a reproduced failure into an unattributable one. Capture costs a
    /// few seconds and some disk; losing the evidence costs another soak.</para>
    ///
    /// <para>Set false only when the logs are certain to be worthless and disk is tight. Capture is
    /// best effort either way: a failure to read a log is reported as a note and never fails the
    /// run or skips teardown.</para>
    /// </summary>
    public bool CaptureNodeLogs { get; set; } = true;

    /// <summary>
    /// Last N lines captured per node when <see cref="CaptureNodeLogs"/> is on. Default 0 = the
    /// whole log.
    ///
    /// <para>Prefer the whole log. A tail keeps the end of the run, but the lines that explain a
    /// late failure — a promotion fingerprint, a fence decision — are usually printed when the
    /// event happened, which for a ten-minute fault run is nowhere near the end. Bound this only
    /// when disk actually forces it.</para>
    /// </summary>
    public int NodeLogTail { get; set; }

    /// <summary>
    /// Seconds to keep scraping every node's metrics endpoint <b>after the workload has stopped</b>,
    /// so deferred work can be watched draining. 0 (the default) collects nothing and costs nothing.
    ///
    /// <para>The plan requires that deferred settlement "reach a plateau under sustained load, and
    /// drain to an idle bound after the load stops". Without this the second half is unmeasurable
    /// rather than merely unmeasured: in-run metrics are scraped by the workload container, so every
    /// series ends at the exact moment the drain question begins. Set it longer than the engine's
    /// retention horizon — completion receipts are retained for ten minutes, so a window shorter
    /// than that cannot show them being reclaimed.</para>
    ///
    /// <para>Costs wall-clock after the measured window and nothing during it, so it can never
    /// affect the numbers the run reports.</para>
    /// </summary>
    public int DrainObservationSeconds { get; set; }

    /// <summary>Seconds between post-load scrapes. Only meaningful with
    /// <see cref="DrainObservationSeconds"/>; a drain is a slow curve, so this is coarse by default.</summary>
    public int DrainObservationIntervalSeconds { get; set; } = 15;

    private static readonly Regex NamePattern = new("^[a-z0-9][a-z0-9-]*$", RegexOptions.Compiled);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || !NamePattern.IsMatch(Name))
            throw new ScenarioException(
                $"'name' must be a non-empty lowercase [a-z0-9-] identifier, got '{Name}'");

        if (SettleSeconds < 0)
            throw new ScenarioException($"'settle_seconds' must be >= 0, got {SettleSeconds}");

        if (DrainObservationSeconds < 0)
            throw new ScenarioException(
                $"'drain_observation_seconds' must be >= 0, got {DrainObservationSeconds}; use 0 to skip the drain window");

        if (DrainObservationSeconds > 0 && DrainObservationIntervalSeconds < 1)
            throw new ScenarioException(
                $"'drain_observation_interval_seconds' must be >= 1, got {DrainObservationIntervalSeconds}");

        if (NodeLogTail < 0)
            throw new ScenarioException(
                $"'node_log_tail' must be >= 0, got {NodeLogTail}; use 0 to capture the whole log");

        Cluster.Validate();
        Workload.Validate();
        Nemesis?.Validate();
        Checks.Validate();

        // Cross-block, so it lives here rather than in WorkloadSpec: the workload names a node and
        // only the cluster block knows how many there are. Caught at read time because the
        // alternative is a container that starts, fails every request against a DNS name that does
        // not resolve, and reports it as the cluster being unreachable.
        if (!string.IsNullOrWhiteSpace(Workload.Gateway) && !Cluster.NodeNames.Contains(Workload.Gateway))
            throw new ScenarioException(
                $"'workload.gateway' must name a node of this cluster ({string.Join(", ", Cluster.NodeNames)}) " +
                $"or be empty to use the whole endpoint pool, got '{Workload.Gateway}'");
    }

    /// <summary>The workload locking, inheriting the cluster default when the workload left it blank.</summary>
    public string EffectiveLocking => string.IsNullOrEmpty(Workload.Locking) ? Cluster.Locking : Workload.Locking;

    /// <summary>The workload isolation, inheriting the cluster default when the workload left it blank.</summary>
    public string EffectiveIsolation => string.IsNullOrEmpty(Workload.Isolation) ? Cluster.Isolation : Workload.Isolation;
}

/// <summary>An invalid or unreadable scenario; the message names the offending key.</summary>
public sealed class ScenarioException : Exception
{
    public ScenarioException(string message) : base(message)
    {
    }
}
