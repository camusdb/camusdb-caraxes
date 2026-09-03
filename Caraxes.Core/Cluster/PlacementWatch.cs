/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Text;

namespace Caraxes.Core.Cluster;

/// <summary>
/// One observation of the whole cluster's placement: who led each partition, at which placement
/// generation, at one instant.
///
/// <para>A partition with no entry in <see cref="LeaderByPartition"/> had no node claiming it — an
/// election in progress, or a partition whose hosts were all unreachable. That is kept as an absence
/// rather than a placeholder name, because "nobody led it" and "somebody led it and we could not tell
/// who" are different findings and only the first is normal during churn.</para>
/// </summary>
public sealed record PlacementSample(
    DateTime Utc,
    IReadOnlyDictionary<int, string> LeaderByPartition,
    IReadOnlyDictionary<int, long> GenerationByPartition)
{
    /// <summary>The partitions any node reported, whether or not one claimed leadership.</summary>
    public IReadOnlyCollection<int> Partitions => GenerationByPartition.Keys.ToList();

    public string Format() =>
        GenerationByPartition.Keys.Order().Select(FormatPartition).DefaultIfEmpty("no partitions").Aggregate((a, b) => a + "  " + b);

    private string FormatPartition(int partition)
        => $"p{partition}={(LeaderByPartition.TryGetValue(partition, out string? leader) ? leader : "?")}" +
           $"@g{GenerationByPartition[partition]}";
}

/// <summary>
/// Grades a series of placement observations taken across a measured window.
///
/// <para>A capacity run assumes the topology it started with is the topology it measured. That
/// assumption is usually true and occasionally very false, and when it is false the run is not
/// merely noisy — it is a weighted average of two different clusters. A leadership move mid-window
/// shifts which node's disk carries a partition's writes; a split changes how many partitions exist
/// at all. Either one can move a throughput number by tens of percent, which is the same size as the
/// effects these runs exist to detect.</para>
///
/// <para>So the window is watched rather than assumed, and instability is reported as a fact about
/// the run rather than smoothed away. The grading is deliberately separate from the polling: what
/// counts as a stable window is a judgement worth testing directly, and it should not require a
/// cluster to exercise.</para>
/// </summary>
public static class PlacementWatch
{
    /// <summary>
    /// What the samples say about the window: a human-readable line for the run's notes, plus whether
    /// the topology held still.
    ///
    /// <para>Fewer than two samples cannot show a change, so they are reported as unwatched rather
    /// than as stable. Claiming stability from a single observation is exactly the assumption this
    /// class exists to stop making.</para>
    /// </summary>
    public static PlacementStability Grade(IReadOnlyList<PlacementSample> samples)
    {
        if (samples.Count == 0)
            return new PlacementStability(Watched: false, Stable: false, "placement was not watched during the measured window");

        if (samples.Count == 1)
            return new PlacementStability(
                Watched: false, Stable: false,
                $"placement was observed once during the measured window ({samples[0].Format()}); " +
                "one observation cannot show whether it held");

        List<string> changes = [];

        PlacementSample first = samples[0];
        PlacementSample last = samples[^1];

        // A partition appearing or disappearing is a split or a merge, and it invalidates the run's
        // per-partition denominators as well as its throughput.
        IReadOnlySet<int> firstPartitions = first.Partitions.ToHashSet();
        IReadOnlySet<int> lastPartitions = last.Partitions.ToHashSet();

        List<int> appeared = lastPartitions.Except(firstPartitions).Order().ToList();
        List<int> vanished = firstPartitions.Except(lastPartitions).Order().ToList();
        if (appeared.Count > 0)
            changes.Add($"partition(s) {string.Join(", ", appeared)} appeared (a split)");
        if (vanished.Count > 0)
            changes.Add($"partition(s) {string.Join(", ", vanished)} disappeared (a merge or removal)");

        foreach (int partition in firstPartitions.Intersect(lastPartitions).Order())
        {
            long fromGeneration = first.GenerationByPartition[partition];
            long toGeneration = last.GenerationByPartition[partition];
            if (fromGeneration != toGeneration)
                changes.Add($"p{partition} placement generation {fromGeneration} -> {toGeneration} (a move)");
        }

        // Leadership is compared across every consecutive pair, not just the ends: a partition that
        // moved away and came back would look untouched from the endpoints alone, and the window still
        // contains the election it paid for.
        for (int i = 1; i < samples.Count; i++)
        {
            foreach ((int partition, string leader) in samples[i - 1].LeaderByPartition.OrderBy(kv => kv.Key))
            {
                if (samples[i].LeaderByPartition.TryGetValue(partition, out string? next) &&
                    !string.Equals(leader, next, StringComparison.Ordinal))
                {
                    changes.Add($"p{partition} leadership {leader} -> {next}");
                }
            }
        }

        // Distinct, because a leader that flapped back and forth would otherwise fill the note with
        // the same sentence; the count still says how often the window was disturbed.
        List<string> distinct = changes.Distinct(StringComparer.Ordinal).ToList();

        if (distinct.Count == 0)
        {
            return new PlacementStability(
                Watched: true, Stable: true,
                $"placement held for the whole measured window across {samples.Count} observation(s): {last.Format()}");
        }

        StringBuilder detail = new();
        detail.Append($"placement CHANGED during the measured window ({samples.Count} observation(s)): ");
        detail.Append(string.Join("; ", distinct.Take(6)));
        if (distinct.Count > 6)
            detail.Append($"; and {distinct.Count - 6} more");
        detail.Append(". A run whose topology moved is an average of two clusters, not a measurement of one");

        return new PlacementStability(Watched: true, Stable: false, detail.ToString());
    }
}

/// <summary>
/// Polls every node's placement on an interval, so the topology a run measured is recorded rather
/// than assumed.
///
/// <para>Sampling costs three small HTTP GETs per interval against an endpoint the harness already
/// polls for readiness, which is nothing next to the load the workload is offering; the default
/// interval is coarse anyway, because a leadership move is not a sub-second event.</para>
///
/// <para>It never throws. A node that stops answering contributes nothing to that sample, exactly as
/// it does for leader observation: a run that is killing nodes on purpose must not lose its placement
/// record to the kill.</para>
/// </summary>
public sealed class PlacementPoller
{
    private readonly ClusterPlan plan;
    private readonly TimeSpan interval;
    private readonly List<PlacementSample> samples = [];

    public PlacementPoller(ClusterPlan plan, TimeSpan? interval = null)
    {
        this.plan = plan;
        this.interval = interval ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>Every sample taken so far, oldest first.</summary>
    public IReadOnlyList<PlacementSample> Samples => samples;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using HttpProbes probes = new();

        while (!cancellationToken.IsCancellationRequested)
        {
            samples.Add(await SampleAsync(probes, cancellationToken).ConfigureAwait(false));

            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<PlacementSample> SampleAsync(HttpProbes probes, CancellationToken cancellationToken)
    {
        Dictionary<int, string> leaders = [];
        Dictionary<int, long> generations = [];

        foreach (NodePlan node in plan.Nodes)
        {
            ClusterPlacement? placement = await probes
                .GetPlacementAsync($"http://localhost:{node.HostRestPort}", cancellationToken).ConfigureAwait(false);

            if (placement is null)
                continue;

            foreach (PartitionPlacement partition in placement.Partitions)
            {
                generations[partition.PartitionId] = partition.Generation;

                if (!partition.LeaderLocal)
                    continue;

                // Two nodes claiming one partition is an election caught mid-flight. Recording both
                // keeps the next sample's comparison honest: whichever way it settles registers as a
                // change, which is what it was.
                leaders[partition.PartitionId] = leaders.TryGetValue(partition.PartitionId, out string? already)
                    ? $"{already}+{node.Name}"
                    : node.Name;
            }
        }

        return new PlacementSample(DateTime.UtcNow, leaders, generations);
    }
}

/// <summary>
/// The verdict on one measured window's topology. <see cref="Watched"/> and <see cref="Stable"/> are
/// separate because "it did not move" and "nobody looked" must not read the same in a run's notes.
/// </summary>
public sealed record PlacementStability(bool Watched, bool Stable, string Note);
