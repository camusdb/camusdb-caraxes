/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Reflection;
using NUnit.Framework;
using Caraxes.Core.Cluster;
using Caraxes.Core.Matrix;
using Caraxes.Core.Scenario;

namespace Caraxes.Tests;

[TestFixture]
public sealed class MatrixExpanderTests
{
    private const string TwoByTwo = """
        name: mx
        cluster:
          name: c
          nodes: 3
        workload:
          rows: 1000
        axes:
          locking: [optimistic, pessimistic]
          nemesis:
            - name: none
            - name: kill
              seed: 7
              events:
                - { at: 20s, fault: kill, target: random, duration: 15s }
        """;

    [Test]
    public void ExpandsCartesianProduct()
    {
        var cells = MatrixExpander.Expand(MatrixReader.Read(TwoByTwo));

        Assert.That(cells, Has.Count.EqualTo(4), "2 locking × 2 nemesis");
        Assert.That(cells.Select(c => c.Name), Is.EquivalentTo(new[]
        {
            "mx-optimistic-none", "mx-optimistic-kill", "mx-pessimistic-none", "mx-pessimistic-kill",
        }));
    }

    [Test]
    public void CellsCarryTheirAxisValues()
    {
        var cells = MatrixExpander.Expand(MatrixReader.Read(TwoByTwo));

        MatrixCell pessKill = cells.Single(c => c.Name == "mx-pessimistic-kill");
        Assert.That(pessKill.Scenario.Cluster.Locking, Is.EqualTo("pessimistic"));
        Assert.That(pessKill.Scenario.Nemesis, Is.Not.Null);
        Assert.That(pessKill.Coordinates["locking"], Is.EqualTo("pessimistic"));
        Assert.That(pessKill.Coordinates["nemesis"], Is.EqualTo("kill"));

        MatrixCell optNone = cells.Single(c => c.Name == "mx-optimistic-none");
        Assert.That(optNone.Scenario.Nemesis, Is.Null, "the 'none' preset is a fault-free cell");
    }

    [Test]
    public void AllCellsShareOneImageButHaveDistinctClusterNames()
    {
        var cells = MatrixExpander.Expand(MatrixReader.Read(TwoByTwo));

        Assert.That(cells.Select(c => c.Scenario.Cluster.EffectiveImage).Distinct().Count(), Is.EqualTo(1),
            "one shared image → the matrix builds once");
        Assert.That(cells.Select(c => c.Scenario.Cluster.Name).Distinct().Count(), Is.EqualTo(cells.Count),
            "distinct cluster names → containers never collide");
    }

    [Test]
    public void UnsetAxesContributeOnlyTheBaseValue()
    {
        var cells = MatrixExpander.Expand(MatrixReader.Read("""
            name: solo
            cluster: { name: c }
            axes:
              nodes: [5]
            """));

        Assert.That(cells, Has.Count.EqualTo(1));
        Assert.That(cells[0].Scenario.Cluster.Nodes, Is.EqualTo(5));
    }

    [Test]
    public void CellsDoNotShareMutableState()
    {
        var cells = MatrixExpander.Expand(MatrixReader.Read(TwoByTwo));
        // Mutating one cell's cluster must not bleed into another.
        cells[0].Scenario.Cluster.Nodes = 99;
        Assert.That(cells[1].Scenario.Cluster.Nodes, Is.EqualTo(3));
    }

    [Test]
    public void CellClusterCarriesEveryClusterSpecField()
    {
        // The cell cluster is a hand-written field-by-field copy, so a ClusterSpec field that is not
        // copied silently reverts to its default in every cell: memory_limit_mb was dropped this way,
        // and sweep cells ran with no memory cap while the baseline they inform ran at 1536 MB. This
        // walks the full property list with a non-default value per field, so the next added field
        // fails loudly here until both the copy and this map are extended.
        Dictionary<string, object> nonDefaults = new()
        {
            [nameof(ClusterSpec.Name)] = "clone-src",
            [nameof(ClusterSpec.Nodes)] = 5,
            [nameof(ClusterSpec.Partitions)] = 7,
            [nameof(ClusterSpec.ReplicationFactor)] = 2,
            [nameof(ClusterSpec.PlacementRebalancer)] = false,
            [nameof(ClusterSpec.LeaderBalancer)] = false,
            [nameof(ClusterSpec.Zones)] = new List<string> { "z1", "z2", "z3", "z4", "z5" },
            [nameof(ClusterSpec.Subnet)] = "10.222.0",
            [nameof(ClusterSpec.FirstIp)] = 4,
            [nameof(ClusterSpec.BaseRestPort)] = 25095,
            [nameof(ClusterSpec.BaseGrpcPort)] = 26095,
            [nameof(ClusterSpec.BaseRaftPort)] = 8070,
            [nameof(ClusterSpec.Locking)] = "pessimistic",
            [nameof(ClusterSpec.Isolation)] = "serializable",
            [nameof(ClusterSpec.ReadValidation)] = "track_and_validate",
            [nameof(ClusterSpec.KeyRangeSharding)] = true,
            [nameof(ClusterSpec.DistributedQueryExecution)] = true,
            [nameof(ClusterSpec.MaxQueryParallelism)] = 4,
            [nameof(ClusterSpec.Diagnostics)] = false,
            [nameof(ClusterSpec.CamusdbRepo)] = "/tmp/camusdb-src",
            [nameof(ClusterSpec.Image)] = "caraxes/test:pinned",
            [nameof(ClusterSpec.SpareCerts)] = 2,
            [nameof(ClusterSpec.DataTmpfsMb)] = 256,
            [nameof(ClusterSpec.MemoryLimitMb)] = 1536,
            [nameof(ClusterSpec.Kahuna)] = new Dictionary<string, object> { ["wal_sync_writes"] = "true" },
            [nameof(ClusterSpec.LogLevels)] = new Dictionary<string, string> { ["Camus"] = "Debug" },
        };

        PropertyInfo[] properties = typeof(ClusterSpec)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .ToArray();

        ClusterSpec source = new();
        foreach (PropertyInfo prop in properties)
        {
            Assert.That(nonDefaults, Does.ContainKey(prop.Name),
                $"ClusterSpec.{prop.Name} has no value in this test's map — add one here and copy the " +
                "field in MatrixExpander's cell-cluster clone, or every cell silently uses its default");
            prop.SetValue(source, nonDefaults[prop.Name]);
        }

        var cells = MatrixExpander.Expand(new MatrixSpec { Name = "clone-check", Cluster = source });
        Assert.That(cells, Has.Count.EqualTo(1), "no axes → exactly the base cell");
        ClusterSpec cloned = cells[0].Scenario.Cluster;

        foreach (PropertyInfo prop in properties)
        {
            Assert.That(prop.GetValue(cloned), Is.EqualTo(prop.GetValue(source)),
                $"ClusterSpec.{prop.Name} did not survive matrix expansion — the cell would run with " +
                "the default instead of the value the matrix asked for");
        }
    }

    [Test]
    public void UnknownAxis_IsRejected()
    {
        ScenarioException ex = Assert.Throws<ScenarioException>(() => MatrixReader.Read("""
            name: mx
            cluster: { name: c }
            axes:
              lockng: [optimistic]
            """))!;
        Assert.That(ex.Message, Does.Contain("axes.lockng"));
    }
}
