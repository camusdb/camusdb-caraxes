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

/// <summary>The measured run's command line plus anything the operator should hear about how it was
/// composed — notably a capture that was asked for and had to be skipped.</summary>
public sealed record WorkloadRunPlan(IReadOnlyList<string> Args, IReadOnlyList<string> Notes);

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
    /// Publishes the workload (framework-dependent, so the same output runs under the node image's
    /// runtime). Returns the publish directory.
    ///
    /// <para>This publishes on every run rather than reusing whatever is already in the directory. It
    /// used to skip when the DLL existed, which silently pinned every scenario to the binary built by
    /// the first run that ever used that directory: correctness checks added to the workload afterwards
    /// never executed, and their absence from the verdict was indistinguishable from them passing. A
    /// repeat publish is incremental and costs a few seconds; running an unknown, older correctness
    /// check costs the meaning of every run in between.</para>
    /// </summary>
    public async Task EnsurePublishedAsync(CancellationToken cancellationToken = default)
    {
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

        Console.WriteLine(
            $"==> seeding {scenario.Workload.Rows} rows over {scenario.Workload.Tables} table(s) " +
            $"into '{scenario.Workload.Database}'");
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

        WorkloadRunPlan runPlan = BuildRunArgs(plan, scenario, containerOutputSubdir);

        Console.WriteLine(
            $"==> running workload ({scenario.Workload.Mode}-loop, {scenario.Workload.Duration}, " +
            $"{scenario.EffectiveLocking}/{scenario.EffectiveIsolation})");
        foreach (string note in runPlan.Notes)
            Console.WriteLine($"    {note}");

        return await RunContainerAsync(runPlan.Args, hostArtifactsDir, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the measured run's command line. Separated from the invocation so the argument
    /// construction — which decides what evidence a run collects — is testable without docker.
    /// </summary>
    public static WorkloadRunPlan BuildRunArgs(
        ClusterPlan plan, Caraxes.Core.Scenario.ScenarioSpec scenario, string containerOutputSubdir)
    {
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

        // 0 means "leave the workload's own default" rather than passing an explicit 0, which the
        // workload would clamp to a 1-second budget and reintroduce the false-unavailable verdict.
        if (scenario.Workload.ReconcileTimeout > 0)
        {
            workloadArgs.Add("--reconcile-timeout");
            workloadArgs.Add(scenario.Workload.ReconcileTimeout.ToString());
        }

        List<string> notes = [];

        // Per-node metric collection needs something to scrape. Asking for it on a cluster with
        // diagnostics off would produce a series of nothing but failed scrapes, which reads like a
        // dead fleet — so it is skipped, and the skip is said out loud rather than inferred later
        // from an artifact that is missing.
        if (scenario.Workload.NodeMetrics && plan.Spec.Diagnostics)
        {
            workloadArgs.Add("--metrics-endpoint");
            workloadArgs.Add(plan.InternalMetricsEndpoints);
            workloadArgs.Add("--metrics-interval");
            workloadArgs.Add(scenario.Workload.MetricsInterval);
            notes.Add($"collecting per-node metrics every {scenario.Workload.MetricsInterval} from {plan.Nodes.Count} node(s)");
        }
        else if (scenario.Workload.NodeMetrics)
        {
            notes.Add("per-node metrics NOT collected: the cluster spec has 'diagnostics: false', so no node serves /metrics");
        }

        // Cluster facts do not need diagnostics: /v1/version, /v1/cluster/health and SHOW VARIABLES
        // are always served. A node image built before /v1/version existed simply records that probe
        // as failed and still contributes the rest.
        if (scenario.Workload.ClusterFacts)
        {
            workloadArgs.Add("--node-endpoint");
            workloadArgs.Add(plan.InternalNodeEndpoints);
        }

        AppendCommonFlags(workloadArgs, scenario.Workload);
        return new WorkloadRunPlan(workloadArgs, notes);
    }

    /// <summary>
    /// Flags that must be identical on <c>init</c> and <c>run</c>. The table count belongs here: it
    /// shapes the schema the seeder writes, and a <c>run</c> that disagrees with the <c>init</c> looks
    /// for tables that were never created.
    /// </summary>
    private static void AppendCommonFlags(List<string> args, Caraxes.Core.Scenario.WorkloadSpec workload)
    {
        args.Add("--tables");
        args.Add(workload.Tables.ToString());
        if (workload.NoAutoPrepare)
            args.Add("--no-auto-prepare");
        if (workload.RequestTimeout > 0)
        {
            args.Add("--request-timeout");
            args.Add(workload.RequestTimeout.ToString());
        }
    }

    /// <summary>
    /// The <c>--user</c> (and matching <c>HOME</c>) arguments the workload container needs so its
    /// artifacts are owned by whoever invoked the harness, or an empty list where the platform
    /// already does that.
    ///
    /// <para>Under rootful Docker on Linux the container runs as root, so everything it writes into
    /// the bind-mounted artifacts directory lands <c>root:root</c> — the run directory included.
    /// The harness then cannot write <c>node-log-{node}.txt</c> beside those artifacts and every
    /// capture fails with "Access to the path … is denied", which is how a whole gate campaign can
    /// finish with no node logs at all. Running the container as the invoking user fixes the
    /// ownership at the source, so both the container and the harness can write the directory.</para>
    ///
    /// <para>Docker Desktop on macOS already maps container UIDs onto the host user, so files come
    /// out host-owned there and this must stay empty: pinning a macOS UID the image has no passwd
    /// entry for would break runs that currently work. <c>HOME</c> is redirected because the
    /// mapped UID likewise has no home directory inside the image, and a runtime that decides to
    /// write under <c>$HOME</c> must not fail on a directory it cannot create.</para>
    /// </summary>
    public static IReadOnlyList<string> BuildUserArgs() =>
        OperatingSystem.IsLinux()
            ? ["--user", $"{Libc.getuid()}:{Libc.getgid()}", "-e", "HOME=/tmp"]
            : [];

    private async Task<WorkloadInvocation> RunContainerAsync(
        IReadOnlyList<string> workloadArgs, string hostArtifactsDir, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(hostArtifactsDir);

        List<string> dockerArgs =
        [
            "run", "--rm",
            "--network", plan.NetworkName,
        ];
        dockerArgs.AddRange(BuildUserArgs());
        dockerArgs.AddRange(
        [
            "-v", $"{Path.GetFullPath(publishDir)}:{ContainerWorkloadDir}:ro",
            "-v", $"{Path.GetFullPath(hostArtifactsDir)}:{ContainerArtifactsDir}",
            "--entrypoint", "dotnet",
            plan.Spec.EffectiveImage,
            $"{ContainerWorkloadDir}/CamusDB.Workload.dll",
        ]);
        dockerArgs.AddRange(workloadArgs);

        // The workload's own non-zero exit (invalid run / usage) is data the caller branches on,
        // not a harness failure, so this uses RunAsync (never throws on exit code) and streams the
        // workload's console output through for live progress.
        ProcessResult result = await ProcessRunner.RunAsync(
            "docker", dockerArgs, streamOutput: true, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new WorkloadInvocation(result.ExitCode);
    }
}

/// <summary>Real user and group of the harness process, for mapping the workload container onto
/// them. .NET exposes no managed <c>getuid</c>, and these are the whole of what is needed.</summary>
internal static class Libc
{
    // DllImport rather than the source-generated LibraryImport: the latter requires
    // AllowUnsafeBlocks across the whole project, which is a large change to buy two syscalls
    // that marshal nothing.
    [System.Runtime.InteropServices.DllImport("libc")]
    internal static extern uint getuid();

    [System.Runtime.InteropServices.DllImport("libc")]
    internal static extern uint getgid();
}
