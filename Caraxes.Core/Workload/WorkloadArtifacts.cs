/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

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

/// <summary>Loads the workload's JSON artifacts from a run directory. A missing or malformed file
/// surfaces as null so the caller distinguishes "the workload produced no summary" (it crashed
/// before writing one) from a summary that merely reports an invalid run.</summary>
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
