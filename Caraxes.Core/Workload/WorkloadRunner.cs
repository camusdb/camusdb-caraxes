/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using Caraxes.Core.Cluster;

namespace Caraxes.Core.Workload;

/// <summary>Exit code of one workload invocation, with the workload's exit-code contract decoded.</summary>
public sealed record WorkloadInvocation(int ExitCode)
{
    /// <summary>0 = summary valid AND reconciliation passed.</summary>
    public bool Ok => ExitCode == 0;

    /// <summary>1 = the run was invalid, reconciliation failed, or the tool threw.</summary>
    public bool InvalidRun => ExitCode == 1;

    /// <summary>2 = usage/precondition error (bad flags, output dir exists, dataset missing).</summary>
    public bool UsageError => ExitCode == 2;
}

/// <summary>
/// Drives <c>CamusDB.Workload</c> against a running cluster from inside the cluster's Docker
/// network. The workload runs in a one-shot container based on the node image — which already
/// trusts the baked dev CA — with a framework-dependent publish of the workload bind-mounted in,
/// so it reaches nodes over TLS by their <c>camusN</c> DNS names with no host-side trust changes.
/// Artifacts land in a bind-mounted directory on the host.
/// </summary>
public sealed class WorkloadRunner
{
    private readonly ClusterPlan plan;

    private readonly string publishDir;

    /// <summary>Artifacts mount point inside the workload container.</summary>
    private const string ContainerArtifactsDir = "/artifacts";

    /// <summary>Published workload mount point inside the workload container.</summary>
    private const string ContainerWorkloadDir = "/workload";

    public WorkloadRunner(ClusterPlan plan, string publishDir)
    {
        this.plan = plan;
        this.publishDir = publishDir;
    }

    /// <summary>
    /// Publishes the workload once (framework-dependent, so the same output runs under the node
    /// image's runtime) unless it is already present. Returns the publish directory.
    /// </summary>
    public async Task EnsurePublishedAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        string dll = Path.Combine(publishDir, "CamusDB.Workload.dll");
        if (!force && File.Exists(dll))
            return;

        string project = Path.Combine(plan.Spec.EffectiveCamusdbRepo, "CamusDB.Workload", "CamusDB.Workload.csproj");
        if (!File.Exists(project))
            throw new InvalidOperationException(
                $"workload project not found at {project}; is 'camusdb_repo' ({plan.Spec.EffectiveCamusdbRepo}) a CamusDB checkout?");

        Directory.CreateDirectory(publishDir);
        Console.WriteLine($"==> publishing CamusDB.Workload to {publishDir}");
        await ProcessRunner.RunCheckedAsync(
            "dotnet",
            ["publish", project, "-c", "Release", "-o", publishDir, "--nologo", "-v", "quiet"],
            streamOutput: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Seeds the dataset (creates the database if absent). Setup is never measured.</summary>
    public async Task<WorkloadInvocation> InitAsync(
        Caraxes.Core.Scenario.ScenarioSpec scenario, string hostArtifactsDir, CancellationToken cancellationToken = default)
    {
        List<string> workloadArgs =
        [
            "init",
            "--endpoint", plan.InternalWorkloadEndpointPool,
            "--database", scenario.Workload.Database,
            "--seed", scenario.Workload.Seed.ToString(),
            "--rows", scenario.Workload.Rows.ToString(),
            "--payload-bytes", scenario.Workload.PayloadBytes.ToString(),
            "--batch", scenario.Workload.Batch.ToString(),
        ];
        AppendCommonFlags(workloadArgs, scenario.Workload);

        Console.WriteLine($"==> seeding {scenario.Workload.Rows} rows into '{scenario.Workload.Database}'");
        return await RunContainerAsync(workloadArgs, hostArtifactsDir, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the measured workload, writing artifacts to <c>{containerOutputSubdir}</c> under the
    /// mounted artifacts directory. That subdirectory must not already exist (the workload
    /// refuses a pre-existing output dir), so the caller passes a fresh name.
    /// </summary>
    public async Task<WorkloadInvocation> RunAsync(
        Caraxes.Core.Scenario.ScenarioSpec scenario,
        string hostArtifactsDir,
        string containerOutputSubdir,
        CancellationToken cancellationToken = default)
    {
        // The workload refuses a pre-existing --output directory (so a real run never clobbers
        // another's artifacts). On a re-run of the same scenario the harness intends replacement, so
        // clear the prior output first — the run directory is disposable, gitignored output.
        string hostOutput = Path.Combine(hostArtifactsDir, containerOutputSubdir);
        if (Directory.Exists(hostOutput))
            Directory.Delete(hostOutput, recursive: true);

        List<string> workloadArgs =
        [
            "run",
            "--endpoint", plan.InternalWorkloadEndpointPool,
            "--database", scenario.Workload.Database,
            "--output", $"{ContainerArtifactsDir}/{containerOutputSubdir}",
            "--seed", scenario.Workload.Seed.ToString(),
            "--rows", scenario.Workload.Rows.ToString(),
            "--payload-bytes", scenario.Workload.PayloadBytes.ToString(),
            "--mode", scenario.Workload.Mode,
            "--target-ops", scenario.Workload.TargetOps.ToString(),
            "--workers", scenario.Workload.Workers.ToString(),
            "--read-percent", scenario.Workload.ReadPercent.ToString(),
            "--write-percent", scenario.Workload.WritePercent.ToString(),
            "--writes-per-transaction", scenario.Workload.WritesPerTransaction.ToString(),
            "--duration", scenario.Workload.Duration,
            "--warmup", scenario.Workload.Warmup,
            "--drain", scenario.Workload.Drain,
            "--connections", scenario.Workload.Connections.ToString(),
            "--max-in-flight", scenario.Workload.MaxInFlight.ToString(),
            "--locking", scenario.EffectiveLocking,
            "--isolation", scenario.EffectiveIsolation,
            "--workload", scenario.Workload.Kind,
        ];
        if (scenario.Workload.ExpectFaults)
            workloadArgs.Add("--expect-faults");
        AppendCommonFlags(workloadArgs, scenario.Workload);

        Console.WriteLine(
            $"==> running workload ({scenario.Workload.Mode}-loop, {scenario.Workload.Duration}, " +
            $"{scenario.EffectiveLocking}/{scenario.EffectiveIsolation})");
        return await RunContainerAsync(workloadArgs, hostArtifactsDir, cancellationToken).ConfigureAwait(false);
    }

    private static void AppendCommonFlags(List<string> args, Caraxes.Core.Scenario.WorkloadSpec workload)
    {
        if (workload.NoAutoPrepare)
            args.Add("--no-auto-prepare");
        if (workload.RequestTimeout > 0)
        {
            args.Add("--request-timeout");
            args.Add(workload.RequestTimeout.ToString());
        }
    }

    private async Task<WorkloadInvocation> RunContainerAsync(
        IReadOnlyList<string> workloadArgs, string hostArtifactsDir, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(hostArtifactsDir);

        List<string> dockerArgs =
        [
            "run", "--rm",
            "--network", plan.NetworkName,
            "-v", $"{Path.GetFullPath(publishDir)}:{ContainerWorkloadDir}:ro",
            "-v", $"{Path.GetFullPath(hostArtifactsDir)}:{ContainerArtifactsDir}",
            "--entrypoint", "dotnet",
            plan.Spec.EffectiveImage,
            $"{ContainerWorkloadDir}/CamusDB.Workload.dll",
        ];
        dockerArgs.AddRange(workloadArgs);

        // The workload's own non-zero exit (invalid run / usage) is data the caller branches on,
        // not a harness failure, so this uses RunAsync (never throws on exit code) and streams the
        // workload's console output through for live progress.
        ProcessResult result = await ProcessRunner.RunAsync(
            "docker", dockerArgs, streamOutput: true, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new WorkloadInvocation(result.ExitCode);
    }
}
