/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Text.RegularExpressions;

namespace Caraxes.Core.Cluster;

/// <summary>
/// Declarative description of a CamusDB Docker cluster under test: how many nodes, how they are
/// placed and balanced, and the database configuration every node boots with. Read from YAML by
/// <see cref="ClusterSpecReader"/>; everything derived (IPs, ports, peer lists, file paths) lives
/// in <see cref="ClusterPlan"/> so the spec stays purely declarative and diffable.
/// </summary>
public sealed class ClusterSpec
{
    /// <summary>Cluster name; prefixes container names, the compose project, and the image tag,
    /// so several caraxes clusters can coexist on one docker daemon.</summary>
    public string Name { get; set; } = "";

    public int Nodes { get; set; } = 3;

    /// <summary>The node names this cluster will have, <c>camus1..camusN</c> — the same names
    /// <see cref="ClusterPlan"/> assigns and the nemesis targets by. Available before a plan is built
    /// so a spec that names a node can be validated at read time.</summary>
    public IEnumerable<string> NodeNames => Enumerable.Range(1, Nodes).Select(i => $"camus{i}");

    public int Partitions { get; set; } = 3;

    /// <summary>Per-partition replica-set size. 3 is the standard chaos-test posture; 0 keeps
    /// full replication (every node hosts every partition).</summary>
    public int ReplicationFactor { get; set; } = 3;

    /// <summary>Repair under-replication on node loss, trim over-replication, smooth skew on
    /// join/leave. On by default: chaos runs exist to exercise exactly these moves.</summary>
    public bool PlacementRebalancer { get; set; } = true;

    /// <summary>Spread partition leadership by load reports. On by default per the test posture.</summary>
    public bool LeaderBalancer { get; set; } = true;

    /// <summary>Optional failure-domain label per node, parallel to the node list (entry i is
    /// camus{i+1}'s zone). Empty = zone-unaware placement.</summary>
    public List<string> Zones { get; set; } = [];

    /// <summary>First three octets of the cluster's /24 bridge network. The default deliberately
    /// avoids 172.31.0 so a caraxes cluster can run alongside the repo's docker/local.yml one.</summary>
    public string Subnet { get; set; } = "10.101.0";

    /// <summary>Last octet of the first node's address; node i gets .(first_ip + i - 1).</summary>
    public int FirstIp { get; set; } = 2;

    /// <summary>Host port of node 1's REST API; node i publishes container port 5095 on
    /// (base_rest_port + i - 1). Defaults high (15095) so a caraxes cluster coexists with a local
    /// standalone CamusDB dev server on the conventional 5095/5096.</summary>
    public int BaseRestPort { get; set; } = 15095;

    /// <summary>Host port of node 1's gRPC API; node i publishes container port 5096 on
    /// (base_grpc_port + i - 1). Defaults high (16095) for the same coexistence reason.</summary>
    public int BaseGrpcPort { get; set; } = 16095;

    /// <summary>Raft port of node 1; node i advertises (base_raft_port + 2*(i-1)), matching the
    /// repo's docker/local.yml convention. Not published to the host — raft traffic stays on the
    /// bridge network.</summary>
    public int BaseRaftPort { get; set; } = 7070;

    /// <summary>Maps to CamusDB <c>default_transaction_locking</c>: optimistic | pessimistic.</summary>
    public string Locking { get; set; } = "optimistic";

    /// <summary>Maps to CamusDB <c>default_isolation_level</c>: read_committed | serializable.</summary>
    public string Isolation { get; set; } = "read_committed";

    /// <summary>Maps to CamusDB <c>default_read_validation</c>: none | track_and_validate. Empty
    /// (the default) omits the key so the engine default applies.
    ///
    /// Scope note, verified in Kahuna's TransactionCoordinator (RequiresReadSetValidation): an
    /// optimistic transaction always validates its read set at commit regardless of this setting,
    /// so the knob changes behaviour only for pessimistic transactions, where
    /// <c>track_and_validate</c> adds a commit-time read-set check on top of the locks. It is
    /// plumbed here so soak scenarios can sweep the setting explicitly instead of relying on the
    /// shipped default.</summary>
    public string ReadValidation { get; set; } = "";

    public bool KeyRangeSharding { get; set; }

    public bool DistributedQueryExecution { get; set; }

    public int MaxQueryParallelism { get; set; } = 1;

    /// <summary>Enables OpenTelemetry diagnostics + the Prometheus /metrics endpoint on every
    /// node. On by default: chaos verdicts correlate against kahuna.placement.* counters.</summary>
    public bool Diagnostics { get; set; } = true;

    /// <summary>Path of the CamusDB repository checkout (Dockerfile, cert script, build context).</summary>
    public string CamusdbRepo { get; set; } = "~/camusdb";

    /// <summary>Docker image tag to build and run. Empty derives <c>caraxes/camusdb:{name}</c>.</summary>
    public string Image { get; set; } = "";

    /// <summary>Extra certificate SAN entries generated beyond <see cref="Nodes"/>, so nodes added
    /// later (join-existing) are covered without regenerating certs and rebuilding the image.</summary>
    public int SpareCerts { get; set; } = 5;

    /// <summary>When &gt; 0, each node's <c>/data</c> is a size-capped tmpfs of this many MiB instead of
    /// a named volume. This is what makes a <c>disk-full</c> fault meaningful — the cap lets the harness
    /// exhaust free space on demand. It is RAM-backed and lost on restart, so use it for disk-pressure
    /// scenarios, not for kill/recovery durability tests. 0 (the default) keeps the named volume.</summary>
    public int DataTmpfsMb { get; set; }

    /// <summary>Preloads the clock skew shim (<c>tools/clockskew</c>) into every node, which the
    /// <c>clock-skew</c> fault needs: the nodes run the <c>-clockskew</c> image layer with
    /// <c>LD_PRELOAD</c> set, and each node's wall clock follows the offset in its own
    /// <c>/tmp/caraxes-skew</c> file (zero until a fault writes one). Off by default, so a scenario
    /// without clock faults runs the node image and process exactly as built. A scenario that
    /// schedules a <c>clock-skew</c> fault without this is rejected at read time.</summary>
    public bool ClockSkew { get; set; }

    /// <summary>When &gt; 0, each node container gets a hard memory limit of this many MiB
    /// (compose <c>mem_limit</c>). This is the fix for the observed unbounded RSS growth: CamusDB
    /// runs with Server GC, and with no cgroup limit each node sizes its heap against the whole
    /// Docker VM — three nodes each grew past 2 GiB of mostly-empty committed heap (live managed
    /// objects were ~190 MiB) until the VM OOM-killed one. The limit alone is not enough: the
    /// runtime's default heap self-cap is 75% of the cgroup limit, and ~350-400 MiB of native
    /// memory (RocksDB) sits outside the managed heap, so at 1536 MiB the two together meet the
    /// limit and a loaded node still dies (soak run G, OOM at t+88m). The generator therefore
    /// also sets <c>DOTNET_GCHeapHardLimitPercent</c> to 60% whenever a limit is set — see
    /// <see cref="ComposeGenerator"/>. Measured guidance: limits below ~1 GiB starve the GC;
    /// 1536-2048 is a good posture for the bank soaks on a 6-8 GiB Docker VM.
    /// 0 (the default) keeps the old behavior: no limit.</summary>
    public int MemoryLimitMb { get; set; }

    /// <summary>When &gt; 0, pins the node's managed-heap hard limit (<c>DOTNET_GCHeapHardLimit</c>) to
    /// this many MiB instead of the 60%-of-<see cref="MemoryLimitMb"/> the generator derives. The
    /// runtime reports this value as <c>TotalAvailableMemoryBytes</c>, and CamusDB sizes its
    /// memory-proportional defaults (RocksDB block cache, memtables, actor caches) from that number.
    /// So this is the knob that keeps a node's memory posture identical when the container limit has
    /// to move for a reason unrelated to the node — the case it exists for is <see cref="DataTmpfsMb"/>:
    /// tmpfs pages are charged to the container's memory cgroup, so a tmpfs-backed run needs a larger
    /// <c>memory_limit_mb</c> to make room for the data, and without this pin that larger limit would
    /// also inflate the heap budget and every proportional cache. A 4096 MiB limit derives
    /// 0x99999999 bytes, i.e. 2457.6 MiB; 2458 rounds to the same integer-MiB cache sizes.
    /// 0 (the default) keeps the derived 60%.</summary>
    public int GcHeapHardLimitMb { get; set; }

    /// <summary>Raw passthrough into the generated config's <c>kahuna:</c> section, for knobs the
    /// spec does not model (election timing, pacing, WAL settings). Keys are written verbatim, so
    /// they must be valid CamusDB <c>kahuna.*</c> option names.</summary>
    public Dictionary<string, object> Kahuna { get; set; } = [];

    /// <summary>Raw passthrough into the generated config's <b>root</b>, for CamusDB options the spec
    /// does not model (transaction budgets, reaper timing, storage thresholds). Keys are written
    /// verbatim after the modeled fields, so an explicit key wins over them; <c>kahuna</c> and
    /// <c>data_dir</c> are refused because they would replace whole modeled sections.</summary>
    public Dictionary<string, object> Camusdb { get; set; } = [];

    /// <summary>Per-category log levels for the node containers, e.g.
    /// <c>{ Kommander: Information }</c>. The entries are joined into CamusDB's
    /// <c>CAMUS_LOG_FILTERS</c> environment variable (<c>Category=Level,...</c>), which its
    /// Program.cs applies last so it outranks the per-category defaults.
    ///
    /// Note this deliberately does NOT use <c>Logging__LogLevel__*</c>: CamusDB calls
    /// <c>ClearProviders()</c> and installs explicit <c>AddFilter</c> rules for Kahuna, Kommander
    /// and Grpc, so the standard configuration path never reaches those categories. That was
    /// verified the hard way — the variable arrived in the container and Kommander still logged
    /// only at Warning.
    ///
    /// This exists because CamusDB ships <c>"Kommander": "Warning"</c>, which hides every
    /// Kommander <c>Information</c> line — including election outcomes, leader-balancer transfers,
    /// and (before they were raised to Warning) snapshot rescue. Two investigations were misled by
    /// that filter: "snapshot mentions: 0" could not distinguish "never attempted" from "attempted
    /// silently", and the run-H stall left a silent tail because election wins log at Information.
    ///
    /// Leave empty (the default) for normal runs — Information on Kommander is verbose. Set it for
    /// a diagnostic run where leadership behaviour is the thing under test.</summary>
    public Dictionary<string, string> LogLevels { get; set; } = [];

    /// <summary>Extra environment variables set on every node container, applied <b>after</b> the ones
    /// Caraxes derives itself, so a deliberate experiment can override them.
    ///
    /// <para>This exists because a node's resident set is mostly native memory that no application
    /// setting reaches. CamusDB's Phase 5 fault soaks lost a node to container OOM three times
    /// (fs1/fs2/fs3) with ~3 GB of native memory per node; the managed heap, RocksDB's block cache,
    /// its memtables, its table readers and Kahuna's durable-2PC stores together accounted for under
    /// a third of it. Reading <c>/proc/1/smaps</c> in a live node found 116 anonymous mappings of
    /// 60-70 MB holding 1,227 MB resident — glibc's secondary malloc arenas, whose count is capped at
    /// <c>8 x ncores</c> and which retain freed memory per thread rather than returning it. The lever
    /// for that is <c>MALLOC_ARENA_MAX</c>, an allocator environment variable with no representation
    /// anywhere in CamusDB, Kahuna or Kommander configuration.</para>
    ///
    /// <para>Kept general rather than a dedicated <c>malloc_arena_max</c> key: the same need recurs for
    /// any runtime or allocator knob the spec does not model, and a scenario that sets one records it
    /// in the generated compose file either way.</para></summary>
    public Dictionary<string, string> Env { get; set; } = [];

    private static readonly Regex NamePattern = new("^[a-z0-9][a-z0-9-]*$", RegexOptions.Compiled);

    private static readonly Regex SubnetPattern = new(@"^\d{1,3}\.\d{1,3}\.\d{1,3}$", RegexOptions.Compiled);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || !NamePattern.IsMatch(Name))
            throw new ClusterSpecException(
                $"'name' must be a non-empty lowercase [a-z0-9-] identifier, got '{Name}'; " +
                "it names containers, volumes, and the compose project");

        foreach (string reserved in new[] { "kahuna", "data_dir" })
            if (Camusdb.Keys.Any(k => string.Equals(k, reserved, StringComparison.OrdinalIgnoreCase)))
                throw new ClusterSpecException(
                    $"'camusdb.{reserved}' is not allowed: it would replace a section the spec models " +
                    "(use the 'kahuna:' block for engine knobs)");

        if (Nodes < 1)
            throw new ClusterSpecException($"'nodes' must be >= 1, got {Nodes}");

        if (Partitions < 1)
            throw new ClusterSpecException($"'partitions' must be >= 1, got {Partitions}");

        if (ReplicationFactor < 0)
            throw new ClusterSpecException(
                $"'replication_factor' must be >= 0 (0 = full replication), got {ReplicationFactor}");

        if (Zones.Count > 0 && Zones.Count != Nodes)
            throw new ClusterSpecException(
                $"'zones' has {Zones.Count} entries but 'nodes' is {Nodes}; they must be parallel (one zone per node)");

        if (!SubnetPattern.IsMatch(Subnet))
            throw new ClusterSpecException(
                $"'subnet' must be the first three octets of a /24 (e.g. '10.101.0'), got '{Subnet}'");

        if (FirstIp < 2 || FirstIp + Nodes + SpareCerts - 1 > 254)
            throw new ClusterSpecException(
                $"'first_ip' ({FirstIp}) must be >= 2 (gateway owns .1) and leave room for " +
                $"{Nodes} nodes + {SpareCerts} spares within the /24");

        if (Locking is not ("optimistic" or "pessimistic"))
            throw new ClusterSpecException($"'locking' must be 'optimistic' or 'pessimistic', got '{Locking}'");

        if (Isolation is not ("read_committed" or "serializable"))
            throw new ClusterSpecException($"'isolation' must be 'read_committed' or 'serializable', got '{Isolation}'");

        if (ReadValidation is not ("" or "none" or "track_and_validate"))
            throw new ClusterSpecException(
                $"'read_validation' must be empty (engine default), 'none' or 'track_and_validate', got '{ReadValidation}'");

        if (MaxQueryParallelism < 1)
            throw new ClusterSpecException($"'max_query_parallelism' must be >= 1, got {MaxQueryParallelism}");

        if (SpareCerts < 0)
            throw new ClusterSpecException($"'spare_certs' must be >= 0, got {SpareCerts}");

        if (DataTmpfsMb < 0)
            throw new ClusterSpecException($"'data_tmpfs_mb' must be >= 0 (0 = named volume), got {DataTmpfsMb}");

        // A tiny limit does not fail cleanly: the container boots, the runtime and RocksDB alone
        // exceed it, and the node is OOM-killed mid-scenario in a way that reads as a database
        // crash. Refuse limits below what the stack demonstrably needs to stand up.
        if (MemoryLimitMb is not 0 and < 512)
            throw new ClusterSpecException(
                $"'memory_limit_mb' must be 0 (no limit) or >= 512, got {MemoryLimitMb}; " +
                "native memory (RocksDB) alone uses ~350-400 MiB per node");

        if (GcHeapHardLimitMb < 0)
            throw new ClusterSpecException($"'gc_heap_hard_limit_mb' must be >= 0 (0 = 60% of memory_limit_mb), got {GcHeapHardLimitMb}");

        // The pin only means something under a container limit, and a heap allowed to grow to the
        // whole container leaves nothing for native memory: the node would be OOM-killed under load
        // exactly the way the 75% self-cap was (see memory_limit_mb).
        if (GcHeapHardLimitMb > 0 && MemoryLimitMb == 0)
            throw new ClusterSpecException("'gc_heap_hard_limit_mb' requires 'memory_limit_mb'");
        if (GcHeapHardLimitMb > 0 && GcHeapHardLimitMb > MemoryLimitMb * 3 / 4)
            throw new ClusterSpecException(
                $"'gc_heap_hard_limit_mb' ({GcHeapHardLimitMb}) must be <= 75% of 'memory_limit_mb' ({MemoryLimitMb}) " +
                "to leave room for native memory and any tmpfs data charged to the container");

        foreach (int port in (int[])[BaseRestPort, BaseGrpcPort, BaseRaftPort])
            if (port is < 1 or > 65535)
                throw new ClusterSpecException($"port bases must be in 1..65535, got {port}");
    }

    /// <summary>The image tag actually used: explicit, or derived from the cluster name.</summary>
    public string EffectiveImage => string.IsNullOrEmpty(Image) ? $"caraxes/camusdb:{Name}" : Image;

    /// <summary>The image the node containers run: <see cref="EffectiveImage"/>, or with
    /// <see cref="ClockSkew"/> the <c>-clockskew</c> layer built on top of it.</summary>
    public string NodeImage => ClockSkew ? EffectiveImage + ClockSkewShim.ImageSuffix : EffectiveImage;

    /// <summary>CamusDB repo path with a leading <c>~</c> expanded.</summary>
    public string EffectiveCamusdbRepo => CamusdbRepo.StartsWith("~")
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), CamusdbRepo.TrimStart('~', '/', '\\'))
        : CamusdbRepo;
}

/// <summary>An invalid or unreadable cluster spec; the message names the offending key.</summary>
public sealed class ClusterSpecException : Exception
{
    public ClusterSpecException(string message) : base(message)
    {
    }
}
