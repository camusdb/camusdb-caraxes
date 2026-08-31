/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Collections;
using Caraxes.Core.Cluster;
using Caraxes.Core.Scenario;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Caraxes.Core.Matrix;

/// <summary>Reads and validates a <see cref="MatrixSpec"/> from YAML, rejecting unknown keys at the
/// root, <c>cluster:</c>, <c>workload:</c>, <c>checks:</c>, and <c>axes:</c> levels for the same
/// reason a scenario does: a silently-dropped typo is a sweep that did not test what it claimed.</summary>
public static class MatrixReader
{
    private static readonly HashSet<string> AllowedRootKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "cluster", "workload", "checks", "axes", "teardown", "settle_seconds",
        "capture_node_logs", "node_log_tail",
    };

    private static readonly HashSet<string> AllowedAxesKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "locking", "nodes", "sharding", "parallelism", "workers", "nemesis",
    };

    public static MatrixSpec ReadFile(string path)
    {
        if (!File.Exists(path))
            throw new ScenarioException($"matrix not found: {path}");
        return Read(File.ReadAllText(path));
    }

    public static MatrixSpec Read(string yml)
    {
        RejectUnknownKeys(yml);

        IDeserializer deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        MatrixSpec spec;
        try
        {
            spec = deserializer.Deserialize<MatrixSpec>(yml) ?? new MatrixSpec();
        }
        catch (Exception e)
        {
            throw new ScenarioException($"matrix is not valid YAML: {e.Message}");
        }

        spec.Validate();
        return spec;
    }

    private static void RejectUnknownKeys(string yml)
    {
        if (string.IsNullOrWhiteSpace(yml))
            throw new ScenarioException("matrix is empty");

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
            throw new ScenarioException($"matrix is not valid YAML: {e.Message}");
        }

        if (root is null)
            throw new ScenarioException("matrix is empty");

        foreach (string key in root.Keys)
            if (!AllowedRootKeys.Contains(key))
                throw new ScenarioException(
                    $"unknown matrix option '{key}'; allowed keys: {string.Join(", ", AllowedRootKeys.OrderBy(k => k))}");

        RejectNested(root, "cluster", ClusterSpecReader.AllowedRootKeys);
        RejectNested(root, "axes", AllowedAxesKeys);
    }

    private static void RejectNested(Dictionary<string, object> root, string section, HashSet<string> allowed)
    {
        if (!root.TryGetValue(section, out object? raw) || raw is not IDictionary dict)
            return;

        foreach (object key in dict.Keys)
        {
            string name = key.ToString() ?? "";
            if (!allowed.Contains(name))
                throw new ScenarioException(
                    $"unknown {section} option '{section}.{name}'; allowed keys: {string.Join(", ", allowed.OrderBy(k => k))}");
        }
    }
}
