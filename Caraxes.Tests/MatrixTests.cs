/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using NUnit.Framework;
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
