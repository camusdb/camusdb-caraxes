/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using NUnit.Framework;
using Caraxes.Core.Cluster;
using Caraxes.Core.LeaderBalance;

namespace Caraxes.Tests;

/// <summary>
/// A capacity run assumes it measured one cluster. A leadership move or a split inside the measured
/// window breaks that assumption without breaking anything the run checks, and it moves a throughput
/// number by about as much as the effects these runs exist to detect — a Phase 1 A/B was read
/// backwards for exactly this reason before repetition caught it. These pin what the harness will and
/// will not call a stable window.
/// </summary>
[TestFixture]
public sealed class PlacementWatchTests
{
    private static readonly DateTime Base = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

    private static PlacementSample Sample(int second, params (int Partition, string? Leader, long Generation)[] partitions)
        => new(
            Base.AddSeconds(second),
            partitions.Where(p => p.Leader is not null).ToDictionary(p => p.Partition, p => p.Leader!),
            partitions.ToDictionary(p => p.Partition, p => p.Generation));

    [Test]
    public void AnUnchangedWindowIsStable()
    {
        PlacementStability stability = PlacementWatch.Grade(
        [
            Sample(0, (1, "camus1", 1), (2, "camus2", 1), (3, "camus3", 1)),
            Sample(30, (1, "camus1", 1), (2, "camus2", 1), (3, "camus3", 1)),
            Sample(60, (1, "camus1", 1), (2, "camus2", 1), (3, "camus3", 1)),
        ]);

        Assert.That(stability.Watched, Is.True);
        Assert.That(stability.Stable, Is.True);
        Assert.That(stability.Note, Does.Contain("held for the whole measured window"));
    }

    [Test]
    public void ALeadershipMoveIsReportedWithTheNodesItMovedBetween()
    {
        PlacementStability stability = PlacementWatch.Grade(
        [
            Sample(0, (1, "camus1", 1)),
            Sample(30, (1, "camus2", 1)),
        ]);

        Assert.That(stability.Stable, Is.False);
        Assert.That(stability.Note, Does.Contain("p1 leadership camus1 -> camus2"));
    }

    /// <summary>
    /// Comparing only the first and last samples would call this window untouched, and it still paid
    /// for two elections inside it.
    /// </summary>
    [Test]
    public void ALeaderThatMovedAwayAndCameBackIsStillAMove()
    {
        PlacementStability stability = PlacementWatch.Grade(
        [
            Sample(0, (1, "camus1", 1)),
            Sample(30, (1, "camus2", 1)),
            Sample(60, (1, "camus1", 1)),
        ]);

        Assert.That(stability.Stable, Is.False);
    }

    [Test]
    public void ASplitIsReportedAsAppearingPartitions()
    {
        PlacementStability stability = PlacementWatch.Grade(
        [
            Sample(0, (1, "camus1", 1), (2, "camus2", 1)),
            Sample(30, (1, "camus1", 1), (2, "camus2", 1), (3, "camus3", 1)),
        ]);

        Assert.That(stability.Stable, Is.False);
        Assert.That(stability.Note, Does.Contain("partition(s) 3 appeared (a split)"));
    }

    /// <summary>A move that keeps the leader still changes the placement generation, and the run's
    /// per-partition denominators change with it.</summary>
    [Test]
    public void AGenerationBumpIsReportedEvenWhenLeadershipHeld()
    {
        PlacementStability stability = PlacementWatch.Grade(
        [
            Sample(0, (1, "camus1", 4)),
            Sample(30, (1, "camus1", 5)),
        ]);

        Assert.That(stability.Stable, Is.False);
        Assert.That(stability.Note, Does.Contain("p1 placement generation 4 -> 5"));
    }

    /// <summary>
    /// "Nobody looked" must never read as "it did not move" — the whole point is to stop assuming the
    /// topology held.
    /// </summary>
    [Test]
    public void OneObservationIsNotEvidenceOfStability()
    {
        PlacementStability stability = PlacementWatch.Grade([Sample(0, (1, "camus1", 1))]);

        Assert.That(stability.Watched, Is.False);
        Assert.That(stability.Stable, Is.False);
        Assert.That(stability.Note, Does.Contain("cannot show whether it held"));
    }

    [Test]
    public void NoObservationsAtAllIsReportedAsUnwatched()
    {
        PlacementStability stability = PlacementWatch.Grade([]);

        Assert.That(stability.Watched, Is.False);
        Assert.That(stability.Stable, Is.False);
        Assert.That(stability.Note, Does.Contain("was not watched"));
    }

    /// <summary>A partition born during the run is a split. One that was there at the first sample
    /// proves nothing: the seeding init can split before the poller starts.</summary>
    [Test]
    public void EvidenceCountsOnlyPartitionsBornDuringTheRunAsSplits()
    {
        TopologyEvidence evidence = PlacementWatch.Evidence(
        [
            Sample(0, (1, "camus1", 1), (5, "camus2", 1)),
            Sample(10, (1, "camus1", 1), (5, "camus2", 1), (9, "camus3", 1)),
            Sample(20, (1, "camus1", 1), (5, "camus2", 1), (9, "camus3", 1), (10, "camus1", 1)),
        ]);

        Assert.That(evidence.Watched, Is.True);
        Assert.That(evidence.PartitionsAppeared, Is.EqualTo(new[] { 9, 10 }));
        Assert.That(evidence.Moves, Is.Empty, "the birth of a partition is its split, not a move");
    }

    /// <summary>A merged-away child still proves its split happened.</summary>
    [Test]
    public void ASplitChildThatLaterMergesAwayStillCounts()
    {
        TopologyEvidence evidence = PlacementWatch.Evidence(
        [
            Sample(0, (1, "camus1", 1)),
            Sample(10, (1, "camus1", 1), (9, "camus2", 1)),
            Sample(20, (1, "camus1", 1)),
        ]);

        Assert.That(evidence.PartitionsAppeared, Is.EqualTo(new[] { 9 }));
    }

    [Test]
    public void AGenerationRiseIsAMove()
    {
        TopologyEvidence evidence = PlacementWatch.Evidence(
        [
            Sample(0, (1, "camus1", 3), (2, "camus2", 7)),
            Sample(10, (1, "camus1", 4), (2, "camus2", 7)),
            Sample(20, (1, "camus1", 4), (2, "camus2", 9)),
        ]);

        Assert.That(evidence.Moves, Is.EqualTo(new[] { "p1 g3 -> g4", "p2 g7 -> g9" }));
        Assert.That(evidence.PartitionsAppeared, Is.Empty);
    }

    /// <summary>A sample keeps the last node that answered, so a lagging node can report an older
    /// generation after a newer one. A fall is a stale view, and coming back to a generation already
    /// seen is not a second move.</summary>
    [Test]
    public void AStaleGenerationIsNeitherAMoveNorASecondMove()
    {
        TopologyEvidence evidence = PlacementWatch.Evidence(
        [
            Sample(0, (1, "camus1", 3)),
            Sample(10, (1, "camus1", 5)),
            Sample(20, (1, "camus1", 4)),
            Sample(30, (1, "camus1", 5)),
        ]);

        Assert.That(evidence.Moves, Is.EqualTo(new[] { "p1 g3 -> g5" }));
    }

    [Test]
    public void AnUnchangedTopologyHasNoEvidence()
    {
        TopologyEvidence evidence = PlacementWatch.Evidence(
        [
            Sample(0, (1, "camus1", 2), (2, "camus2", 2)),
            Sample(10, (1, "camus2", 2), (2, "camus2", 2)),
        ]);

        Assert.That(evidence.Watched, Is.True);
        Assert.That(evidence.PartitionsAppeared, Is.Empty);
        Assert.That(evidence.Moves, Is.Empty, "a leadership change alone moves no replica");
        Assert.That(evidence.Format(), Does.Contain("no split").And.Contain("no replica move"));
    }

    /// <summary>One sample cannot show a change, so it must not read as "watched, nothing happened".</summary>
    [Test]
    public void OneSampleIsNotWatchedEvidence()
    {
        TopologyEvidence evidence = PlacementWatch.Evidence([Sample(0, (1, "camus1", 1))]);

        Assert.That(evidence.Watched, Is.False);
        Assert.That(evidence.Format(), Does.Contain("not watched"));
    }

    /// <summary>A leader that flaps between two nodes across many samples must not fill the note with
    /// the same sentence repeated; the reader needs the shape, not the transcript.</summary>
    [Test]
    public void RepeatedIdenticalMovesAreReportedOnce()
    {
        List<PlacementSample> samples = [];
        for (int i = 0; i < 10; i++)
            samples.Add(Sample(i * 10, (1, i % 2 == 0 ? "camus1" : "camus2", 1)));

        PlacementStability stability = PlacementWatch.Grade(samples);

        Assert.That(stability.Stable, Is.False);
        Assert.That(stability.Note.Split("p1 leadership camus1 -> camus2").Length - 1, Is.EqualTo(1));
    }
}

/// <summary>
/// Leadership resolving is not leadership spreading, and a capacity run needs the second. A measured
/// run settled instantly at camus1=3 camus2=0 camus3=0, passed every readiness check, and then spent
/// its window with one node carrying 100% of the durable work behind an even three-way gateway split.
/// </summary>
[TestFixture]
public sealed class LeaderSpreadTests
{
    private static readonly IReadOnlyList<string> ThreeNodes = ["camus1", "camus2", "camus3"];

    private static LeaderSnapshot Snapshot(int total, params (string Node, int Leaders)[] leaders)
        => new(
            DateTime.UtcNow,
            leaders.ToDictionary(l => l.Node, l => l.Leaders),
            leaders.Sum(l => l.Leaders),
            total);

    [Test]
    public void EvenLeadershipIsSpread()
    {
        Assert.That(
            LeaderObservation.IsSpread(Snapshot(3, ("camus1", 1), ("camus2", 1), ("camus3", 1)), ThreeNodes),
            Is.True);
    }

    [Test]
    public void OneNodeLeadingEverythingIsNotSpreadEvenThoughEveryPartitionHasALeader()
    {
        LeaderSnapshot concentrated = Snapshot(3, ("camus1", 3), ("camus2", 0), ("camus3", 0));

        Assert.That(concentrated.ResolvedPartitions, Is.EqualTo(concentrated.TotalPartitions));
        Assert.That(LeaderObservation.IsSpread(concentrated, ThreeNodes), Is.False);
    }

    /// <summary>Four partitions on three nodes cannot do better than 2/1/1; demanding a perfectly
    /// even split would wait out the whole budget on an already-balanced cluster.</summary>
    [Test]
    public void AnUnevenlyDivisiblePartitionCountIsSpreadAtOneApart()
    {
        Assert.That(
            LeaderObservation.IsSpread(Snapshot(4, ("camus1", 2), ("camus2", 1), ("camus3", 1)), ThreeNodes),
            Is.True);
    }

    [Test]
    public void UnresolvedLeadershipIsNotSpreadHoweverEvenlyTheRestSits()
    {
        Assert.That(
            LeaderObservation.IsSpread(Snapshot(4, ("camus1", 1), ("camus2", 1), ("camus3", 1)), ThreeNodes),
            Is.False);
    }

    [Test]
    public void ASingleNodeClusterIsTriviallySpread()
    {
        Assert.That(LeaderObservation.IsSpread(Snapshot(3, ("camus1", 3)), ["camus1"]), Is.True);
    }

    [Test]
    public void AClusterWithNoPartitionsHasNotSettled()
    {
        Assert.That(LeaderObservation.IsSpread(Snapshot(0), ThreeNodes), Is.False);
    }
}
