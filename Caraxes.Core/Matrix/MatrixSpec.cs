/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Text.RegularExpressions;
using Caraxes.Core.Cluster;
using Caraxes.Core.Nemesis;
using Caraxes.Core.Scenario;

namespace Caraxes.Core.Matrix;

/// <summary>
/// A cartesian sweep of scenarios: a base cluster/workload template plus axes of values to vary. The
/// expander produces one <see cref="ScenarioSpec"/> per combination, so a single file runs
/// {locking} × {node counts} × {sharding} × {parallelism} × {fault presets} and the report lines them
/// up side by side. Every axis is optional; an unset axis contributes the base value alone.
/// </summary>
public sealed class MatrixSpec
{
    public string Name { get; set; } = "";

    /// <summary>Base cluster; its name is the prefix for every cell's cluster name.</summary>
    public ClusterSpec Cluster { get; set; } = new();

    public WorkloadSpec Workload { get; set; } = new();

    public ChecksSpec Checks { get; set; } = new();

    public MatrixAxes Axes { get; set; } = new();

    public bool Teardown { get; set; } = true;

    /// <summary>Seconds each cell waits for partition leadership to settle before measuring; see
    /// <see cref="ScenarioSpec.SettleSeconds"/>. Applies to every cell.</summary>
    public int SettleSeconds { get; set; } = 30;

    private static readonly Regex NamePattern = new("^[a-z0-9][a-z0-9-]*$", RegexOptions.Compiled);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || !NamePattern.IsMatch(Name))
            throw new ScenarioException($"'name' must be a non-empty lowercase [a-z0-9-] identifier, got '{Name}'");

        Cluster.Validate();
        Workload.Validate();
        Checks.Validate();
        Axes.Validate();
    }
}

/// <summary>The dimensions to sweep. Each is an optional list; empty means "use the base value".</summary>
public sealed class MatrixAxes
{
    public List<string> Locking { get; set; } = [];

    public List<int> Nodes { get; set; } = [];

    public List<bool> Sharding { get; set; } = [];

    public List<int> Parallelism { get; set; } = [];

    /// <summary>
    /// Load-generator worker counts — the concurrency sweep. Each value becomes one cell that runs the
    /// full measured pipeline at that many in-flight workers.
    ///
    /// <para>Distinct from <see cref="Parallelism"/>, which sets the cluster's
    /// <c>max_query_parallelism</c>: that governs how one query fans out inside the engine, this
    /// governs how much work the client offers at once. Only the second one answers "is the baseline
    /// concurrency-starved, and where is the knee".</para>
    /// </summary>
    public List<int> Workers { get; set; } = [];

    /// <summary>Named fault presets; each becomes one column of the sweep. A preset with no
    /// <c>events</c>/<c>random</c> is a fault-free baseline cell.</summary>
    public List<NemesisPreset> Nemesis { get; set; } = [];

    public void Validate()
    {
        foreach (string l in Locking)
            if (l is not ("optimistic" or "pessimistic"))
                throw new ScenarioException($"'axes.locking' values must be 'optimistic' or 'pessimistic', got '{l}'");

        foreach (int n in Nodes)
            if (n < 1)
                throw new ScenarioException($"'axes.nodes' values must be >= 1, got {n}");

        foreach (int p in Parallelism)
            if (p < 1)
                throw new ScenarioException($"'axes.parallelism' values must be >= 1, got {p}");

        foreach (int w in Workers)
            if (w < 1)
                throw new ScenarioException($"'axes.workers' values must be >= 1, got {w}");

        foreach (NemesisPreset preset in Nemesis)
            preset.Validate();
    }
}

/// <summary>A named fault schedule for one matrix column. Same shape as a scenario's nemesis block,
/// plus a <see cref="Name"/> that labels the column; an empty schedule is the fault-free baseline.</summary>
public sealed class NemesisPreset
{
    public string Name { get; set; } = "";

    public int Seed { get; set; } = 1;

    public List<NemesisEvent> Events { get; set; } = [];

    public RandomNemesisSpec? Random { get; set; }

    private static readonly Regex NamePattern = new("^[a-z0-9][a-z0-9-]*$", RegexOptions.Compiled);

    public bool IsFaultFree => Events.Count == 0 && Random is null;

    public NemesisSpec? ToNemesisSpec() =>
        IsFaultFree ? null : new NemesisSpec { Seed = Seed, Events = Events, Random = Random };

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || !NamePattern.IsMatch(Name))
            throw new ScenarioException($"'axes.nemesis[].name' must be a non-empty lowercase [a-z0-9-] identifier, got '{Name}'");

        ToNemesisSpec()?.Validate();
    }
}
