/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Caraxes.Core.Cluster;

/// <summary>
/// Reads and validates a <see cref="ClusterSpec"/> from YAML. Unknown root keys are rejected —
/// YamlDotNet would otherwise silently drop them, so a misspelled <c>replication_facto</c> would
/// quietly run the cluster with the default instead of the intended value, which in a test harness
/// invalidates the run it was supposed to shape.
/// </summary>
public static class ClusterSpecReader
{
    internal static readonly HashSet<string> AllowedRootKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "name",
        "nodes",
        "partitions",
        "replication_factor",
        "placement_rebalancer",
        "leader_balancer",
        "zones",
        "subnet",
        "first_ip",
        "base_rest_port",
        "base_grpc_port",
        "base_raft_port",
        "locking",
        "isolation",
        "key_range_sharding",
        "distributed_query_execution",
        "max_query_parallelism",
        "diagnostics",
        "camusdb_repo",
        "image",
        "spare_certs",
        "data_tmpfs_mb",
        "memory_limit_mb",
        "kahuna",
    };

    public static ClusterSpec ReadFile(string path)
    {
        if (!File.Exists(path))
            throw new ClusterSpecException($"cluster spec not found: {path}");

        return Read(File.ReadAllText(path));
    }

    public static ClusterSpec Read(string yml)
    {
        RejectUnknownKeys(yml);

        IDeserializer deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        ClusterSpec spec;
        try
        {
            spec = deserializer.Deserialize<ClusterSpec>(yml) ?? new ClusterSpec();
        }
        catch (Exception e)
        {
            throw new ClusterSpecException($"cluster spec is not valid YAML: {e.Message}");
        }

        spec.Validate();
        return spec;
    }

    private static void RejectUnknownKeys(string yml)
    {
        if (string.IsNullOrWhiteSpace(yml))
            throw new ClusterSpecException("cluster spec is empty");

        IDeserializer raw = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        Dictionary<string, object>? root;
        try
        {
            root = raw.Deserialize<Dictionary<string, object>>(yml);
        }
        catch (Exception e)
        {
            throw new ClusterSpecException($"cluster spec is not valid YAML: {e.Message}");
        }

        if (root is null)
            throw new ClusterSpecException("cluster spec is empty");

        foreach (string key in root.Keys)
            if (!AllowedRootKeys.Contains(key))
                throw new ClusterSpecException(
                    $"unknown cluster spec option '{key}'; allowed keys: " +
                    string.Join(", ", AllowedRootKeys.OrderBy(k => k)));
    }
}
