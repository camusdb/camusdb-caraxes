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

    /// <param name="backupsRoot">Where the nodes' <c>/backups</c> bind mounts live when the spec enables
    /// backups: one sub-directory per node under it. Null puts them under the cluster's run directory;
    /// a scenario passes its own run directory so a tagged run keeps its backups beside its other
    /// artifacts instead of sharing one catalog across runs.</param>
    public ClusterOrchestrator(ClusterSpec spec, string? runRoot = null, string? backupsRoot = null)
    {
        plan = ClusterPlan.FromSpec(spec);
        runDir = Path.Combine(runRoot ?? Path.Combine(Environment.CurrentDirectory, "runs"), spec.Name);
        BackupsRoot = Path.GetFullPath(backupsRoot ?? Path.Combine(runDir, "backups"));
    }

    public ClusterPlan Plan => plan;

    public string ComposeFilePath => Path.Combine(runDir, "compose.yml");

    /// <summary>The per-node config directory the fleet mounts read-only; a joiner's config is written here too.</summary>
    public string ConfigDir => Path.Combine(runDir, "config");

    /// <summary>The crash-dump bind mounts, one sub-directory per node.</summary>
    public string DumpsDir => Path.Combine(runDir, "dumps");

    /// <summary>The compose file of the disaster-recovery rehearsal fleet (<see cref="UpRestoredAsync"/>):
    /// the same project, nodes and addresses, with every <c>/data</c> bind-mounted from a restored root.</summary>
    public string RestoredComposeFilePath => Path.Combine(runDir, "compose-restored.yml");

    /// <summary>Host directory holding each node's <c>/backups</c> mount (<c>{BackupsRoot}/{node}</c>).
    /// Meaningful only when the spec enables backups.</summary>
    public string BackupsRoot { get; }

    /// <summary>Host path of a node's backup mount: its catalog sits under <c>catalog/</c> and its
    /// restores under <c>restore/</c>, matching <see cref="NodeConfigGenerator.BackupCatalogDir"/> and
    /// <see cref="NodeConfigGenerator.BackupRestoreRoot"/> inside the container.</summary>
    public string BackupsDirOf(NodePlan node) => Path.Combine(BackupsRoot, node.Name);

    /// <summary>Writes compose.yml and config/camusN.yml into the run directory.</summary>
    public void WriteArtifacts()
    {
        string configDir = Path.Combine(runDir, "config");
        Directory.CreateDirectory(configDir);

        foreach (NodePlan node in plan.Nodes)
        {
            File.WriteAllText(Path.Combine(configDir, $"{node.Name}.yml"), NodeConfigGenerator.Generate(plan, node));
            // Created here, before `up`, so the crash-dump bind mount is owned by the harness user
            // rather than by the docker daemon creating a missing source directory as root.
            Directory.CreateDirectory(Path.Combine(runDir, "dumps", node.Name));

            // Same for the backup mount, plus the mode Kahuna insists on: a backup or restore root that
            // is group- or world-writable fails with BackupInsecureRoot, and the node would only find
            // out at the first backup, minutes into the run.
            if (plan.Spec.Backups)
            {
                foreach (string sub in new[] { "catalog", "restore" })
                    CreatePrivateDirectory(Path.Combine(BackupsDirOf(node), sub));
            }
        }

        // The config mount is relative so the run directory stays relocatable; the backups root is
        // absolute because a scenario places it outside the cluster directory.
        File.WriteAllText(ComposeFilePath, ComposeGenerator.Generate(
            plan, configDirInCompose: "./config", backupsDirInCompose: plan.Spec.Backups ? BackupsRoot : null));
    }

    /// <summary>Creates a directory the harness user owns with owner-only (0700) permissions.</summary>
    private static void CreatePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
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

        if (spec.ClockSkew)
            await ClockSkewShim.BuildImageAsync(spec, cancellationToken).ConfigureAwait(false);

        // A previous run of this cluster left its catalog behind (the mount outlives teardown by
        // design). Left in place, the next run's first incremental would chain on a backup whose WAL
        // range this fresh cluster never had, and the fallback-to-full would read as a finding. The
        // node wrote those files as root, so the harness user cannot remove them directly.
        if (spec.Backups)
            await ClearBackupsAsync(cancellationToken).ConfigureAwait(false);

        await ReclaimSubnetAsync(cancellationToken).ConfigureAwait(false);

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

    /// <summary>Empties every node's backup mount, through a container because the nodes write there as
    /// root, then recreates the two roots with the harness user's ownership and mode.</summary>
    private async Task ClearBackupsAsync(CancellationToken cancellationToken)
    {
        bool anything = plan.Nodes.Any(n => Directory.Exists(BackupsDirOf(n)) && Directory.EnumerateFileSystemEntries(BackupsDirOf(n)).Any());
        if (!anything)
            return;

        Console.WriteLine($"==> clearing backups left under {BackupsRoot} by a previous run");
        await RemoveContentsAsRootAsync(BackupsRoot, cancellationToken).ConfigureAwait(false);
        foreach (NodePlan node in plan.Nodes)
            foreach (string sub in new[] { "catalog", "restore" })
                CreatePrivateDirectory(Path.Combine(BackupsDirOf(node), sub));
    }

    /// <summary>Removes everything under <paramref name="hostDir"/> (not the directory itself) from a
    /// throwaway container on the node image, for trees the nodes wrote as root.</summary>
    public Task RemoveContentsAsRootAsync(string hostDir, CancellationToken cancellationToken = default)
        => ProcessRunner.RunCheckedAsync(
            "docker",
            ["run", "--rm", "--entrypoint", "sh", "-v", $"{Path.GetFullPath(hostDir)}:/caraxes-target", plan.Spec.NodeImage,
             "-c", "find /caraxes-target -mindepth 1 -delete"],
            cancellationToken: cancellationToken);

    /// <summary>Copies the tree under <paramref name="sourceHostDir"/> into <paramref name="targetHostDir"/>
    /// (which must exist), preserving ownership and modes, from a throwaway container on the node image —
    /// the source is a restore the node wrote as root, unreadable to the harness user.</summary>
    public Task CopyTreeAsRootAsync(string sourceHostDir, string targetHostDir, CancellationToken cancellationToken = default)
        => ProcessRunner.RunCheckedAsync(
            "docker",
            ["run", "--rm", "--entrypoint", "sh",
             "-v", $"{Path.GetFullPath(sourceHostDir)}:/caraxes-source:ro",
             "-v", $"{Path.GetFullPath(targetHostDir)}:/caraxes-target",
             plan.Spec.NodeImage, "-c", "cp -a /caraxes-source/. /caraxes-target/"],
            cancellationToken: cancellationToken);

    /// <summary>
    /// Disaster-recovery rehearsal: brings the same fleet up again with every node's <c>/data</c> taken
    /// from <c>{restoredRoot}/{node}</c>, a copy of a restored data root. Same project, names, addresses
    /// and ports as the live cluster, so the placement the restored meta partition carries names the
    /// endpoints that come up. Must run after <see cref="DownAsync"/>: the two fleets share a subnet.
    /// Backups are off in this fleet's config — it exists to be read, not to extend the catalog.
    /// </summary>
    public async Task UpRestoredAsync(string restoredRoot, TimeSpan? readyTimeout = null, CancellationToken cancellationToken = default)
    {
        ClusterSpec spec = plan.Spec.Clone();
        spec.Backups = false;
        ClusterPlan restoredPlan = ClusterPlan.FromSpec(spec);

        string configDir = Path.Combine(runDir, "config-restored");
        Directory.CreateDirectory(configDir);
        foreach (NodePlan node in restoredPlan.Nodes)
        {
            File.WriteAllText(Path.Combine(configDir, $"{node.Name}.yml"), NodeConfigGenerator.Generate(restoredPlan, node));
            Directory.CreateDirectory(Path.Combine(runDir, "dumps", node.Name));
            if (!Directory.Exists(Path.Combine(restoredRoot, node.Name)))
                throw new InvalidOperationException($"no restored data root for {node.Name} under {restoredRoot}");
        }

        File.WriteAllText(RestoredComposeFilePath, ComposeGenerator.Generate(
            restoredPlan, configDirInCompose: "./config-restored", restoredDataDirInCompose: Path.GetFullPath(restoredRoot)));

        await ReclaimSubnetAsync(cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"==> starting {spec.Nodes} node(s) from the restored data roots under {restoredRoot}");
        await ProcessRunner.RunCheckedAsync(
            "docker",
            ["compose", "-f", RestoredComposeFilePath, "up", "-d"],
            workingDirectory: runDir,
            streamOutput: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await WaitForReadyAsync(readyTimeout ?? TimeSpan.FromSeconds(180), cancellationToken).ConfigureAwait(false);
        await PrintStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts one more node of this cluster at runtime, joining the fleet: the same image, config,
    /// environment and mounts the compose file gives a node (one <c>docker run</c> built from the same
    /// generators), on the compose network at the slot's address, with <c>--join-existing true</c>
    /// appended to the command line and <c>CAMUS_INITIAL_CLUSTER</c> naming the seeds the caller put
    /// on <paramref name="joiner"/>. Labelled as a service of the compose project so <see cref="DownAsync"/>
    /// removes it with the fleet. <paramref name="dataHostDir"/> is bound at <c>/data</c>: a restored
    /// data root, or an empty directory for a node the leader seeds.
    /// </summary>
    public async Task LaunchJoinerAsync(NodePlan joiner, string dataHostDir, CancellationToken cancellationToken = default)
    {
        ClusterSpec spec = plan.Spec;

        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(Path.Combine(ConfigDir, $"{joiner.Name}.yml"), NodeConfigGenerator.Generate(plan, joiner));
        Directory.CreateDirectory(Path.Combine(DumpsDir, joiner.Name));
        if (spec.Backups)
            foreach (string sub in new[] { "catalog", "restore" })
                CreatePrivateDirectory(Path.Combine(BackupsDirOf(joiner), sub));

        List<string> args =
        [
            "run", "-d",
            "--name", joiner.ContainerName,
            "--network", plan.NetworkName,
            "--ip", joiner.Ip,
            "--network-alias", joiner.Name,
            "--cap-add", "NET_ADMIN",
            "--restart", "no",
            "--label", $"com.docker.compose.project={plan.ProjectName}",
            "--label", $"com.docker.compose.service={joiner.Name}",
            "--label", "caraxes.joiner=true",
            "-p", $"{joiner.HostRestPort}:5095",
            "-p", $"{joiner.HostGrpcPort}:5096",
            "-v", $"{Path.GetFullPath(ConfigDir)}:/app/caraxes-config:ro",
            "-v", $"{Path.GetFullPath(Path.Combine(DumpsDir, joiner.Name))}:/dumps",
            "-v", $"{Path.GetFullPath(dataHostDir)}:/data",
        ];

        if (spec.Backups)
            args.AddRange(["-v", $"{Path.GetFullPath(BackupsDirOf(joiner))}:{NodeConfigGenerator.BackupMount}"]);
        if (spec.MemoryLimitMb > 0)
            args.AddRange(["--memory", $"{spec.MemoryLimitMb}m"]);

        foreach ((string key, object value) in ComposeGenerator.NodeEnvironment(spec, joiner))
            args.AddRange(["-e", $"{key}={value}"]);
        args.AddRange(["-e", "CAMUS_EXTRA_ARGS=--join-existing true"]);

        args.Add(spec.NodeImage);

        Console.WriteLine($"==> starting {joiner.Name} ({joiner.Ip}) to join the cluster through {joiner.InitialCluster}");
        await ProcessRunner.RunCheckedAsync("docker", args, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Containers of this compose project that the compose file does not name: the nodes
    /// <see cref="LaunchJoinerAsync"/> started. Their names, from docker.</summary>
    public async Task<IReadOnlyList<string>> ListJoinerContainersAsync(CancellationToken cancellationToken = default)
    {
        ProcessResult result = await ProcessRunner.RunAsync(
            "docker", ["ps", "-a", "--filter", $"label=com.docker.compose.project={plan.ProjectName}", "--filter", "label=caraxes.joiner=true", "--format", "{{.Names}}"],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.Success
            ? result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
    }

    /// <summary>Tears the rehearsal fleet down. Its compose file names the same project as the live
    /// cluster's, so this is the ordinary teardown pointed at the restored compose file.</summary>
    public async Task DownRestoredAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(RestoredComposeFilePath))
            return;

        await ProcessRunner.RunCheckedAsync(
            "docker", ["compose", "-f", RestoredComposeFilePath, "down", "--remove-orphans", "-v"],
            workingDirectory: runDir, streamOutput: true, cancellationToken: cancellationToken).ConfigureAwait(false);

        foreach (string note in await RemoveOwnNetworkAsync(cancellationToken).ConfigureAwait(false))
            Console.WriteLine($"==> {note}");
    }

    public async Task DownAsync(bool keepVolumes = false, CancellationToken cancellationToken = default)
    {
        // Regenerate the compose file if the run directory is gone: down must work from the spec
        // alone, or a deleted runs/ directory strands containers and volumes.
        if (!File.Exists(ComposeFilePath))
            WriteArtifacts();

        // A node that joined at runtime is a container of this project the compose file does not
        // name. --remove-orphans is meant to remove it; done explicitly first so a joiner that is
        // still catching up (or wedged) can never survive the fleet and hold the subnet.
        foreach (string joiner in await ListJoinerContainersAsync(cancellationToken).ConfigureAwait(false))
        {
            Console.WriteLine($"==> removing runtime-joined node container {joiner}");
            await ProcessRunner.RunAsync("docker", ["rm", "-f", joiner], cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        List<string> args = ["compose", "-f", ComposeFilePath, "down", "--remove-orphans"];
        if (!keepVolumes)
            args.Add("-v");

        await ProcessRunner.RunCheckedAsync(
            "docker", args, workingDirectory: runDir, streamOutput: true, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // compose leaves the network behind when anything outside the project still holds an endpoint
        // on it — a workload container that outlived a killed harness is the case that happens. The
        // fixed /24 turns that leftover into a hard block on every later run, so finish the job here.
        foreach (string note in await RemoveOwnNetworkAsync(cancellationToken).ConfigureAwait(false))
            Console.WriteLine($"==> {note}");
    }

    /// <summary>
    /// Clears whatever stands on this cluster's subnet before <c>compose up</c> claims it. A leaked
    /// Caraxes network from an interrupted run is removed with anything still attached; a network
    /// this harness did not create is refused with the recovery instructions, because it may be a
    /// live service that merely shares the address space.
    ///
    /// <para>Without this the failure surfaces as docker's <c>invalid pool request: Pool overlaps
    /// with other one on this address space</c>, which names neither the network nor the container
    /// pinning it.</para>
    /// </summary>
    private async Task ReclaimSubnetAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<DockerNetwork> networks = await DockerNetworks.ListAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<SubnetHolder> blockers = DockerNetworks.FindBlockers(networks, plan.NetworkName, plan.NetworkSubnetCidr);

        foreach (SubnetHolder blocker in blockers)
        {
            if (blocker.Kind == SubnetHolderKind.Foreign)
                throw new InvalidOperationException(
                    DockerNetworks.ForeignBlockerMessage(blocker.Network, plan.NetworkSubnetCidr));

            Console.WriteLine(
                $"==> network '{blocker.Network.Name}' is a leftover Caraxes cluster holding {plan.NetworkSubnetCidr}; reclaiming it");

            foreach (string note in await DockerNetworks.RemoveAsync(blocker.Network, cancellationToken).ConfigureAwait(false))
                Console.WriteLine($"==> {note}");
        }
    }

    /// <summary>
    /// Removes this cluster's own network if <c>compose down</c> could not, force-removing the
    /// containers still attached to it. Returns what it did, or nothing at all when the network is
    /// already gone — the normal case, which must stay silent.
    /// </summary>
    private async Task<IReadOnlyList<string>> RemoveOwnNetworkAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<DockerNetwork> networks = await DockerNetworks.ListAsync(cancellationToken).ConfigureAwait(false);
        DockerNetwork? own = networks.FirstOrDefault(n => string.Equals(n.Name, plan.NetworkName, StringComparison.Ordinal));

        return own is null
            ? []
            : await DockerNetworks.RemoveAsync(own, cancellationToken).ConfigureAwait(false);
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
    /// <param name="additionalNodes">Nodes outside the plan whose logs are wanted too: the ones a
    /// runtime join added.</param>
    public async Task<IReadOnlyList<string>> CaptureLogsAsync(
        string destinationDir,
        int tail = 0,
        CancellationToken cancellationToken = default,
        IEnumerable<NodePlan>? additionalNodes = null)
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

        foreach (NodePlan node in plan.Nodes.Concat(additionalNodes ?? []))
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
