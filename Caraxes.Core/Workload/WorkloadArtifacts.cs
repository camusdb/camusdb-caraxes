/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Caraxes.Core.Workload;

/// <summary>
/// The subset of <c>CamusDB.Workload</c>'s <c>summary.json</c> the harness reasons about. The file
/// carries far more (per-phase latency distributions), but a scenario verdict turns on the
/// operation accounting and the validity flag; the rest stays in the artifact for a human or a
/// later report stage. Deserialized case-insensitively because the workload emits PascalCase keys.
/// </summary>
public sealed class WorkloadSummary
{
    public double MeasuredSeconds { get; set; }

    public string Mode { get; set; } = "";

    public long Offered { get; set; }

    public long Started { get; set; }

    public long Completed { get; set; }

    public long Failed { get; set; }

    public long Conflicts { get; set; }

    public long Transient { get; set; }

    public long Indeterminate { get; set; }

    public long DomainErrors { get; set; }

    public long InternalErrors { get; set; }

    public long ScheduleDrops { get; set; }

    public double AchievedOpsPerSec { get; set; }

    public double ReadOpsPerSec { get; set; }

    public double WriteTxnsPerSec { get; set; }

    public bool Valid { get; set; }

    public List<string> ValidityWarnings { get; set; } = [];
}

/// <summary>The subset of <c>reconciliation.json</c> the verdict turns on.</summary>
public sealed class ReconciliationSummary
{
    public long ExpectedMin { get; set; }

    public long ExpectedMax { get; set; }

    public long Observed { get; set; }

    public long IndeterminateTxns { get; set; }

    public bool VersionsMatch { get; set; }

    public long RowCount { get; set; }

    public bool RowCountMatches { get; set; }

    public bool AccountingBalances { get; set; }

    public bool NoConflicts { get; set; }

    public bool ConflictsWaived { get; set; }

    public bool Passed { get; set; }

    public List<string> Failures { get; set; } = [];
}

/// <summary>One assembly a node reported loading.</summary>
public sealed class ClusterFactsComponent
{
    public string Name { get; set; } = "";

    public string Version { get; set; } = "";
}

/// <summary>What one node reported about itself when the run captured its facts.</summary>
public sealed class ClusterFactsNode
{
    public string Node { get; set; } = "";

    public string? Server { get; set; }

    public List<ClusterFactsComponent> Components { get; set; } = [];

    /// <summary>Null when the node did not answer the readiness probe at all.</summary>
    public bool? Ready { get; set; }

    /// <summary>Probes this node could not answer; each one is a fact the manifest is missing.</summary>
    public List<string> Errors { get; set; } = [];
}

/// <summary>
/// The subset of <c>cluster-facts.json</c> a scenario verdict reasons about: which build answered,
/// whether every node was ready, and the fingerprint that decides whether two runs may be compared.
/// The file also carries each node's full configuration and the workload tables' range placement,
/// which stay in the artifact for a human or a later report stage.
/// </summary>
public sealed class ClusterFactsSummary
{
    public string CapturedAtUtc { get; set; } = "";

    public List<ClusterFactsNode> Nodes { get; set; } = [];

    public List<string> Errors { get; set; } = [];

    public string DurabilityFingerprint { get; set; } = "";
}

/// <summary>
/// The subset of <c>client-resources.json</c> the verdict reasons about.
///
/// <para>It answers one question: was the load generator itself the thing that ran out? A generator
/// that was CPU-bound, pausing for GC, or held at its in-flight cap produces a flat throughput curve
/// that reads exactly like a saturated cluster, and a scenario that reports that number as the
/// cluster's capacity is reporting a measurement of itself.</para>
/// </summary>
public sealed class ClientResourcesSummary
{
    public double CpuUtilization { get; set; }

    public int ProcessorCount { get; set; }

    public double AllocatedMbPerSecond { get; set; }

    public double GcPauseFraction { get; set; }

    public long PeakThreadPoolQueue { get; set; }

    public double RequiredInFlight { get; set; }

    public List<string> Warnings { get; set; } = [];

    /// <summary>True when nothing suggested the generator limited the result.</summary>
    public bool HeadroomAvailable => Warnings.Count == 0;
}

/// <summary>Loads the workload's JSON artifacts from a run directory. A missing or malformed file
/// surfaces as null so the caller distinguishes "the workload produced no summary" (it crashed
/// before writing one) from a summary that merely reports an invalid run.</summary>
/// <summary>The half-open UTC instant range a run measured.</summary>
public sealed record MeasuredWindow(DateTime StartUtc, DateTime EndUtc)
{
    public bool Contains(DateTime utc) => utc >= StartUtc && utc <= EndUtc;
}

/// <summary>The run's own timing anchor, as written to <c>run-meta.json</c>.</summary>
public sealed class RunMeta
{
    public string MeasureStartUtc { get; set; } = "";

    public double MeasureSeconds { get; set; }
}

public static class WorkloadArtifacts
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static WorkloadSummary? ReadSummary(string outputDir) =>
        ReadJson<WorkloadSummary>(Path.Combine(outputDir, "summary.json"));

    public static ReconciliationSummary? ReadReconciliation(string outputDir) =>
        ReadJson<ReconciliationSummary>(Path.Combine(outputDir, "reconciliation.json"));

    /// <summary>Null when the run did not capture cluster facts (the scenario turned them off, or the
    /// workload predates them) — which is itself worth reporting, since the run then cannot say what
    /// build it measured.</summary>
    public static ClusterFactsSummary? ReadClusterFacts(string outputDir) =>
        ReadJson<ClusterFactsSummary>(Path.Combine(outputDir, "cluster-facts.json"));

    /// <summary>Null when the measured window caught too few samples to compute a delta.</summary>
    public static ClientResourcesSummary? ReadClientResources(string outputDir) =>
        ReadJson<ClientResourcesSummary>(Path.Combine(outputDir, "client-resources.json"));

    /// <summary>
    /// The window the run actually measured, or null when it did not record one.
    ///
    /// <para>The workload writes this anchor rather than the harness computing it from the scenario's
    /// warm-up and duration: the workload is what decides when measurement began, and a seeding pass
    /// that ran long would put a computed window in the wrong place. Any evidence the harness cuts to
    /// the measured window has to use the same anchor as the artifacts it will be read beside.</para>
    /// </summary>
    public static MeasuredWindow? ReadMeasuredWindow(string outputDir)
    {
        RunMeta? meta = ReadJson<RunMeta>(Path.Combine(outputDir, "run-meta.json"));
        if (meta is null || meta.MeasureSeconds <= 0)
            return null;

        if (!DateTime.TryParse(meta.MeasureStartUtc, null, DateTimeStyles.RoundtripKind, out DateTime start))
            return null;

        DateTime startUtc = start.ToUniversalTime();
        return new MeasuredWindow(startUtc, startUtc.AddSeconds(meta.MeasureSeconds));
    }

    private static T? ReadJson<T>(string path) where T : class
    {
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
