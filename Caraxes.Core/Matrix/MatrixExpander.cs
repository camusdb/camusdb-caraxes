/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using Caraxes.Core.Cluster;
using Caraxes.Core.Scenario;

namespace Caraxes.Core.Matrix;

/// <summary>One expanded cell: a runnable scenario plus the axis coordinates that produced it.</summary>
public sealed record MatrixCell(string Name, ScenarioSpec Scenario, IReadOnlyDictionary<string, string> Coordinates);

/// <summary>
/// Expands a <see cref="MatrixSpec"/> into the cartesian product of its axes. Pure — no filesystem, no
/// docker — so the whole sweep (names, per-cell config, cluster identities) is unit-testable before a
/// single container starts. Every cell shares one image tag so the matrix builds the image once
/// instead of once per cell (cells differ only in runtime config, never in the image).
/// </summary>
public static class MatrixExpander
{
    public static IReadOnlyList<MatrixCell> Expand(MatrixSpec matrix)
    {
        matrix.Validate();

        // Each axis contributes a list of options; an unset axis contributes a single no-op option so
        // it drops out of the product without special-casing. The coordinate key names the report
        // column; the name prefix (possibly empty, when the value is self-descriptive) shapes the cell
        // name — so `locking: optimistic` reads as "optimistic" while `nodes: 3` reads as "n3".
        List<AxisOption> locking = Axis("locking", "", matrix.Axes.Locking, v => v, (s, v) => s.Cluster.Locking = v);
        List<AxisOption> nodes = Axis("nodes", "n", matrix.Axes.Nodes, v => v.ToString(), (s, v) => s.Cluster.Nodes = v);
        List<AxisOption> sharding = Axis("sharding", "shard", matrix.Axes.Sharding, v => v ? "on" : "off", (s, v) => s.Cluster.KeyRangeSharding = v);
        List<AxisOption> parallelism = Axis("parallelism", "par", matrix.Axes.Parallelism, v => v.ToString(), (s, v) => s.Cluster.MaxQueryParallelism = v);
        List<AxisOption> workers = Axis("workers", "w", matrix.Axes.Workers, v => v.ToString(), (s, v) => s.Workload.Workers = v);

        List<AxisOption> nemesis = matrix.Axes.Nemesis.Count == 0
            ? [new AxisOption(null, null, null, _ => { })]
            : matrix.Axes.Nemesis.Select(p => new AxisOption("nemesis", "", p.Name, s => s.Nemesis = p.ToNemesisSpec())).ToList();

        // Shared image tag so the whole matrix uses one build.
        string sharedImage = string.IsNullOrEmpty(matrix.Cluster.Image)
            ? $"caraxes/camusdb:matrix-{matrix.Name}"
            : matrix.Cluster.Image;

        List<MatrixCell> cells = [];
        foreach (AxisOption l in locking)
            foreach (AxisOption n in nodes)
                foreach (AxisOption sh in sharding)
                    foreach (AxisOption par in parallelism)
                        foreach (AxisOption w in workers)
                            foreach (AxisOption nem in nemesis)
                                cells.Add(BuildCell(matrix, sharedImage, [l, n, sh, par, w, nem]));

        return cells;
    }

    private static MatrixCell BuildCell(MatrixSpec matrix, string sharedImage, IReadOnlyList<AxisOption> options)
    {
        // Deep-ish copy of the base so cells never share mutable state.
        ScenarioSpec scenario = new()
        {
            Cluster = CloneCluster(matrix.Cluster),
            Workload = matrix.Workload.Clone(),
            Checks = matrix.Checks.Clone(),
            Teardown = matrix.Teardown,
            SettleSeconds = matrix.SettleSeconds,
        };
        scenario.Cluster.Image = sharedImage;

        Dictionary<string, string> coords = new();
        List<string> suffixParts = [];
        foreach (AxisOption opt in options)
        {
            opt.Apply(scenario);
            if (opt.CoordinateKey is not null && opt.Label is not null)
            {
                coords[opt.CoordinateKey] = opt.Label;
                suffixParts.Add($"{opt.NamePrefix}{opt.Label}");
            }
        }

        string cellName = suffixParts.Count == 0 ? matrix.Name : $"{matrix.Name}-{string.Join("-", suffixParts)}";
        scenario.Name = cellName;
        // Cluster name is bounded and unique per cell so containers/volumes never collide.
        scenario.Cluster.Name = Sanitize($"{matrix.Cluster.Name}-{string.Join("-", suffixParts)}");

        return new MatrixCell(cellName, scenario, coords);
    }

    private static List<AxisOption> Axis<T>(string coordinateKey, string namePrefix, List<T> values, Func<T, string> label, Action<ScenarioSpec, T> apply) =>
        values.Count == 0
            ? [new AxisOption(null, null, null, _ => { })]
            : values.Select(v => new AxisOption(coordinateKey, namePrefix, label(v), s => apply(s, v))).ToList();

    private static string Sanitize(string s)
    {
        string cleaned = new(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-').ToArray());
        return cleaned.Trim('-');
    }

    private static ClusterSpec CloneCluster(ClusterSpec c) => new()
    {
        Name = c.Name, Nodes = c.Nodes, Partitions = c.Partitions, ReplicationFactor = c.ReplicationFactor,
        PlacementRebalancer = c.PlacementRebalancer, LeaderBalancer = c.LeaderBalancer, Zones = [.. c.Zones],
        Subnet = c.Subnet, FirstIp = c.FirstIp, BaseRestPort = c.BaseRestPort, BaseGrpcPort = c.BaseGrpcPort,
        BaseRaftPort = c.BaseRaftPort, Locking = c.Locking, Isolation = c.Isolation, KeyRangeSharding = c.KeyRangeSharding,
        DistributedQueryExecution = c.DistributedQueryExecution, MaxQueryParallelism = c.MaxQueryParallelism,
        Diagnostics = c.Diagnostics, CamusdbRepo = c.CamusdbRepo, Image = c.Image, SpareCerts = c.SpareCerts,
        Kahuna = new Dictionary<string, object>(c.Kahuna),
    };

    private sealed record AxisOption(string? CoordinateKey, string? NamePrefix, string? Label, Action<ScenarioSpec> Apply);
}
