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

    private static readonly Regex NamePattern = new("^[a-z0-9][a-z0-9-]*$", RegexOptions.Compiled);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || !NamePattern.IsMatch(Name))
            throw new ScenarioException(
                $"'name' must be a non-empty lowercase [a-z0-9-] identifier, got '{Name}'");

        Cluster.Validate();
        Workload.Validate();
        Nemesis?.Validate();
        Checks.Validate();
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
