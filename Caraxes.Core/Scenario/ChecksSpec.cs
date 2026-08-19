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

    public void Validate()
    {
        if (MaxRecoverySeconds <= 0)
            throw new ScenarioException($"'checks.max_recovery_seconds' must be > 0, got {MaxRecoverySeconds}");
    }
}
