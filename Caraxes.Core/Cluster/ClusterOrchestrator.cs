/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

namespace Caraxes.Core.Cluster;

/// <summary>
/// Drives a cluster's lifecycle: generate artifacts (per-node configs + compose file) into the run
/// directory, ensure certs, build the image, bring the fleet up, and wait until every node reports
/// ready. A certificate regeneration forces the image build even when the caller asked to skip it —
/// see <see cref="ShouldBuildImage"/>. Artifacts are regenerated on every command from the spec — the spec is the source of
/// truth and the run directory is disposable output, so <c>down</c> works even after the run
/// directory was deleted.
/// </summary>
public sealed class ClusterOrchestrator
{
    private readonly ClusterPlan plan;

    private readonly string runDir;

    public ClusterOrchestrator(ClusterSpec spec, string? runRoot = null)
    {
        plan = ClusterPlan.FromSpec(spec);
        runDir = Path.Combine(runRoot ?? Path.Combine(Environment.CurrentDirectory, "runs"), spec.Name);
    }

    public ClusterPlan Plan => plan;

    public string ComposeFilePath => Path.Combine(runDir, "compose.yml");

    /// <summary>Writes compose.yml and config/camusN.yml into the run directory.</summary>
    public void WriteArtifacts()
    {
        string configDir = Path.Combine(runDir, "config");
        Directory.CreateDirectory(configDir);

        foreach (NodePlan node in plan.Nodes)
            File.WriteAllText(Path.Combine(configDir, $"{node.Name}.yml"), NodeConfigGenerator.Generate(plan, node));

        // The config mount is relative so the run directory stays relocatable.
        File.WriteAllText(ComposeFilePath, ComposeGenerator.Generate(plan, configDirInCompose: "./config"));
    }

    /// <summary>
    /// Decides whether <c>up</c> builds the image. A caller asks to skip the build to save time when
    /// only runtime config changed, but that request loses to a certificate regeneration: the image
    /// bakes the .pfx and its CA anchor together, so a new certificate on disk against an old image
    /// breaks every inter-node handshake with <c>UntrustedRoot</c>. The matrix runner reaches this
    /// case without any flag, because it skips the build on every cell after the first and a
    /// <c>nodes</c> axis changes the SAN parameters between cells.
    /// </summary>
    public static bool ShouldBuildImage(bool skipBuild, bool certsRegenerated) => !skipBuild || certsRegenerated;

    public async Task UpAsync(bool skipBuild = false, TimeSpan? readyTimeout = null, CancellationToken cancellationToken = default)
    {
        ClusterSpec spec = plan.Spec;

        WriteArtifacts();
        Console.WriteLine($"==> artifacts written to {runDir}");

        bool certsRegenerated = await CertProvisioner.EnsureAsync(spec, cancellationToken).ConfigureAwait(false);

        if (skipBuild && certsRegenerated)
            Console.WriteLine("==> certs changed; building anyway so the image re-bakes the matching CA anchor");

        if (ShouldBuildImage(skipBuild, certsRegenerated))
        {
            Console.WriteLine($"==> building image {spec.EffectiveImage} from {spec.EffectiveCamusdbRepo}");
            await ProcessRunner.RunCheckedAsync(
                "docker",
                ["build", "-f", "docker/Dockerfile", "-t", spec.EffectiveImage, "."],
                workingDirectory: spec.EffectiveCamusdbRepo,
                streamOutput: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        Console.WriteLine($"==> starting {spec.Nodes} node(s)");
        await ProcessRunner.RunCheckedAsync(
            "docker",
            ["compose", "-f", ComposeFilePath, "up", "-d"],
            workingDirectory: runDir,
            streamOutput: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await WaitForReadyAsync(readyTimeout ?? TimeSpan.FromSeconds(180), cancellationToken).ConfigureAwait(false);

        await PrintStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DownAsync(bool keepVolumes = false, CancellationToken cancellationToken = default)
    {
        // Regenerate the compose file if the run directory is gone: down must work from the spec
        // alone, or a deleted runs/ directory strands containers and volumes.
        if (!File.Exists(ComposeFilePath))
            WriteArtifacts();

        List<string> args = ["compose", "-f", ComposeFilePath, "down", "--remove-orphans"];
        if (!keepVolumes)
            args.Add("-v");

        await ProcessRunner.RunCheckedAsync(
            "docker", args, workingDirectory: runDir, streamOutput: true, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task WaitForReadyAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using HttpProbes probes = new();
        DateTime deadline = DateTime.UtcNow + timeout;
        HashSet<string> ready = [];

        Console.WriteLine($"==> waiting up to {timeout.TotalSeconds:0}s for {plan.Nodes.Count} node(s) to become ready");

        while (DateTime.UtcNow < deadline)
        {
            foreach (NodePlan node in plan.Nodes)
            {
                if (ready.Contains(node.Name))
                    continue;

                NodeHealth? health = await probes.GetHealthAsync($"http://localhost:{node.HostRestPort}", cancellationToken)
                    .ConfigureAwait(false);

                if (health is { Ready: true })
                {
                    ready.Add(node.Name);
                    Console.WriteLine($"    {node.Name}: ready (role={health.LocalRole}, hosting {health.HostedPartitions} partition(s))");
                }
            }

            if (ready.Count == plan.Nodes.Count)
                return;

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        string missing = string.Join(", ", plan.Nodes.Select(n => n.Name).Where(n => !ready.Contains(n)));
        throw new TimeoutException(
            $"cluster '{plan.Spec.Name}' did not become ready within {timeout.TotalSeconds:0}s; " +
            $"nodes still not ready: {missing}. Inspect with: caraxes logs --spec <spec> --node <name>");
    }

    public async Task PrintStatusAsync(CancellationToken cancellationToken = default)
    {
        using HttpProbes probes = new();

        Console.WriteLine();
        Console.WriteLine($"cluster '{plan.Spec.Name}' ({plan.Nodes.Count} nodes, rf={plan.Spec.ReplicationFactor}, partitions={plan.Spec.Partitions})");
        Console.WriteLine($"{"node",-10} {"container",-24} {"ip",-14} {"rest",-6} {"grpc",-6} {"ready",-6} {"role",-10} hosted");

        ClusterPlacement? placement = null;

        foreach (NodePlan node in plan.Nodes)
        {
            string baseUrl = $"http://localhost:{node.HostRestPort}";
            NodeHealth? health = await probes.GetHealthAsync(baseUrl, cancellationToken).ConfigureAwait(false);

            placement ??= await probes.GetPlacementAsync(baseUrl, cancellationToken).ConfigureAwait(false);

            Console.WriteLine(
                $"{node.Name,-10} {node.ContainerName,-24} {node.Ip,-14} {node.HostRestPort,-6} {node.HostGrpcPort,-6} " +
                $"{(health is null ? "down" : health.Ready ? "yes" : "no"),-6} {health?.LocalRole ?? "-",-10} {(health is null ? "-" : health.HostedPartitions.ToString())}");
        }

        if (placement is not null)
        {
            Console.WriteLine();
            Console.WriteLine($"placement (rf={placement.ReplicationFactor}, rebalancer={(placement.RebalancerEnabled ? "on" : "off")}):");
            foreach (PartitionPlacement partition in placement.Partitions)
            {
                string replicas = partition.Replicas.Count == 0
                    ? "all voters (full replication)"
                    : string.Join(", ", partition.Replicas.Select(r => $"{r.Endpoint}({r.Role})"));
                Console.WriteLine($"  p{partition.PartitionId}: gen={partition.Generation} state={partition.State} rf={partition.EffectiveReplicationFactor} replicas: {replicas}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"workload endpoint pool: {plan.WorkloadEndpointPool}");
    }

    /// <summary>
    /// Writes every node's container log into <paramref name="destinationDir"/> as
    /// <c>node-log-{node}.txt</c>, and returns one note per node that could not be captured.
    ///
    /// <para>Must be called BEFORE teardown. A container's log dies with the container, so once
    /// <see cref="DownAsync"/> has run the evidence is gone — and the log is where a whole class of
    /// diagnostic witness lives, the kind emitted once at a leadership change rather than counted
    /// into a metric the sampler scrapes. A gate run that reproduced a data-loss defect was left
    /// unattributable exactly this way: the metric witnesses survived in the scraped series and the
    /// log-only ones did not.</para>
    ///
    /// <para>Best effort by construction: it never throws, because losing a log must not also lose
    /// the teardown that frees the port and subnet for the next run. Every failure comes back as a
    /// note so the run says out loud that capture did not happen — silence here would read as
    /// "captured", which is the failure this method exists to prevent.</para>
    /// </summary>
    /// <param name="tail">Last N lines per node; 0 or less captures the whole log. Bound it only
    /// when disk forces the issue: a promotion fingerprint printed early in a ten-minute run is
    /// exactly what a tail discards.</param>
    public async Task<IReadOnlyList<string>> CaptureLogsAsync(
        string destinationDir,
        int tail = 0,
        CancellationToken cancellationToken = default)
    {
        List<string> notes = [];

        try
        {
            Directory.CreateDirectory(destinationDir);
        }
        catch (Exception e)
        {
            notes.Add($"node logs were not captured: could not create '{destinationDir}': {e.Message}");
            return notes;
        }

        foreach (NodePlan node in plan.Nodes)
        {
            string destination = Path.Combine(destinationDir, $"node-log-{node.Name}.txt");

            List<string> arguments = ["logs"];
            if (tail > 0)
            {
                arguments.Add("--tail");
                arguments.Add(tail.ToString());
            }

            arguments.Add(node.ContainerName);

            try
            {
                (int exitCode, long bytes) = await ProcessRunner
                    .RunToFileAsync("docker", arguments, destination, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                if (exitCode != 0)
                    notes.Add($"node log capture for '{node.Name}' exited {exitCode}; see {Path.GetFileName(destination)}");
                else if (bytes == 0)
                    notes.Add($"node log capture for '{node.Name}' produced an empty log");
            }
            catch (Exception e)
            {
                notes.Add($"node log capture for '{node.Name}' failed: {e.Message}");
            }
        }

        return notes;
    }

    public async Task LogsAsync(string nodeName, int tail = 200, CancellationToken cancellationToken = default)
    {
        NodePlan? node = plan.Nodes.FirstOrDefault(n => n.Name == nodeName);
        if (node is null)
            throw new ClusterSpecException(
                $"unknown node '{nodeName}'; this cluster has: {string.Join(", ", plan.Nodes.Select(n => n.Name))}");

        await ProcessRunner.RunCheckedAsync(
            "docker",
            ["logs", "--tail", tail.ToString(), node.ContainerName],
            streamOutput: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
