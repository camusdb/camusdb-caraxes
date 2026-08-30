/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Collections;
using Caraxes.Core.Cluster;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Caraxes.Core.Scenario;

/// <summary>
/// Reads and validates a <see cref="ScenarioSpec"/> from YAML, rejecting unknown keys at every
/// level (root, <c>cluster:</c>, <c>workload:</c>). A misspelled key in a test scenario silently
/// falls back to a default, which invalidates the very run it was meant to shape — so it is an
/// error, not a warning.
/// </summary>
public static class ScenarioSpecReader
{
    private static readonly HashSet<string> AllowedRootKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "cluster", "workload", "nemesis", "checks", "teardown", "settle_seconds",
    };

    private static readonly HashSet<string> AllowedChecksKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "max_recovery_seconds", "require_recovery", "require_progress_under_fault", "require_node_health",
        "require_client_headroom", "require_cluster_facts", "require_quiet_host",
    };

    private static readonly HashSet<string> AllowedNemesisKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "seed", "events", "random",
    };

    private static readonly HashSet<string> AllowedWorkloadKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "kind", "database", "seed", "rows", "tables", "payload_bytes", "batch", "mode", "target_ops", "workers",
        "read_percent", "write_percent", "writes_per_transaction", "duration", "warmup", "drain",
        "connections", "max_in_flight", "locking", "isolation", "no_auto_prepare", "request_timeout",
        "expect_faults", "reconcile_timeout", "node_metrics", "metrics_interval", "cluster_facts",
    };

    public static ScenarioSpec ReadFile(string path)
    {
        if (!File.Exists(path))
            throw new ScenarioException($"scenario not found: {path}");

        return Read(File.ReadAllText(path));
    }

    public static ScenarioSpec Read(string yml)
    {
        RejectUnknownKeys(yml);

        IDeserializer deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        ScenarioSpec spec;
        try
        {
            spec = deserializer.Deserialize<ScenarioSpec>(yml) ?? new ScenarioSpec();
        }
        catch (Exception e)
        {
            throw new ScenarioException($"scenario is not valid YAML: {e.Message}");
        }

        spec.Validate();
        return spec;
    }

    private static void RejectUnknownKeys(string yml)
    {
        if (string.IsNullOrWhiteSpace(yml))
            throw new ScenarioException("scenario is empty");

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
            throw new ScenarioException($"scenario is not valid YAML: {e.Message}");
        }

        if (root is null)
            throw new ScenarioException("scenario is empty");

        RejectSection(root, null, AllowedRootKeys, "scenario");
        RejectNested(root, "cluster", ClusterSpecReader.AllowedRootKeys);
        RejectNested(root, "workload", AllowedWorkloadKeys);
        RejectNested(root, "nemesis", AllowedNemesisKeys);
        RejectNested(root, "checks", AllowedChecksKeys);
    }

    private static void RejectNested(Dictionary<string, object> root, string section, HashSet<string> allowed)
    {
        if (!root.TryGetValue(section, out object? raw) || raw is null)
            return;

        if (raw is not IDictionary dict)
            throw new ScenarioException($"'{section}' must be a mapping of option names to values");

        RejectSection(dict, section, allowed, section);
    }

    private static void RejectSection(IDictionary dict, string? section, HashSet<string> allowed, string label)
    {
        foreach (object key in dict.Keys)
        {
            string name = key.ToString() ?? "";
            if (!allowed.Contains(name))
            {
                string prefix = section is null ? "" : $"{section}.";
                throw new ScenarioException(
                    $"unknown {label} option '{prefix}{name}'; allowed keys: " +
                    string.Join(", ", allowed.OrderBy(k => k)));
            }
        }
    }
}
