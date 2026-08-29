/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

namespace Caraxes.Core.Scenario;

/// <summary>
/// Per-scenario pass/fail rules layered on top of the always-on checks (workload validity,
/// reconciliation, and — fault-free only — no internal errors). These grade the resilience the fault
/// correlation measures. Every rule has a default, so a scenario that omits the block still gets a
/// sensible resilience bar.
/// </summary>
public sealed class ChecksSpec
{
    /// <summary>Every healed fault must let the workload's error rate return to near-zero within this
    /// many seconds of the heal. The default is generous enough for a Raft re-election plus client
    /// endpoint-pool requarantine, tight enough to catch a cluster that limps for a minute.</summary>
    public double MaxRecoverySeconds { get; set; } = 45;

    /// <summary>Require every healed fault to be observed recovering before the run ends. When false,
    /// a fault whose recovery was not observed (the run ended first) is reported but not failed.</summary>
    public bool RequireRecovery { get; set; } = true;

    /// <summary>The workload must keep completing at least some operations during every fault window
    /// (availability under fault). A window with zero completed ops means a total outage.</summary>
    public bool RequireProgressUnderFault { get; set; } = true;

    /// <summary>Every node must keep answering <c>/ping</c> throughout the measured window, except
    /// while a fault targeting it (or the whole cluster) is active plus recovery grace. Container
    /// state is deliberately not consulted: a node process has aborted while its container stayed
    /// <c>Up</c>, and that run must FAIL, not pass on green docker state.</summary>
    public bool RequireNodeHealth { get; set; } = true;

    /// <summary>
    /// Fail the run when the load generator's own resource check flagged it — CPU-bound, pausing for
    /// GC, backed up in its thread pool, or pinned at its in-flight cap.
    ///
    /// <para>Default false, because a reliability scenario asks whether the cluster stayed correct
    /// under fault, and a generator working hard does not invalidate that answer. Turn it on for a
    /// <b>capacity</b> scenario, where the whole claim is that the number measured is the cluster's:
    /// there, a flagged generator means the run measured itself.</para>
    /// </summary>
    public bool RequireClientHeadroom { get; set; }

    /// <summary>
    /// Fail the run when every node reported ready and every fact was captured is not true — that is,
    /// when a node could not be asked what it was running, or answered that it was not ready.
    ///
    /// <para>Default false so a scenario on an older node image, whose <c>/v1/version</c> does not
    /// exist yet, still runs. Turn it on for a run whose result has to be reproducible later, where a
    /// missing build fingerprint means the number can never be compared against anything.</para>
    /// </summary>
    public bool RequireClusterFacts { get; set; }

    /// <summary>An independent copy, field for field. Memberwise for the reason given on
    /// <see cref="WorkloadSpec.Clone"/>: a hand-written list silently drops the next field added.</summary>
    public ChecksSpec Clone() => (ChecksSpec)MemberwiseClone();

    public void Validate()
    {
        if (MaxRecoverySeconds <= 0)
            throw new ScenarioException($"'checks.max_recovery_seconds' must be > 0, got {MaxRecoverySeconds}");
    }
}
