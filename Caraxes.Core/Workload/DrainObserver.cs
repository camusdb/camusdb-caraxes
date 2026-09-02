/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using Caraxes.Core.Cluster;

namespace Caraxes.Core.Workload;

/// <summary>
/// Scrapes every node's metrics endpoint for a while <b>after the workload has stopped</b>, writing
/// each scrape to its own file so a later reader can watch deferred work drain.
///
/// <para>It exists because the harness could not answer one of the questions the throughput plan asks
/// out loud: deferred settlement must "reach a plateau under sustained load, and drain to an idle
/// bound after the load stops". Every metric series ends at the instant the load ends, because the
/// scraping is done by the workload container and the container exits — so the moment the drain
/// question begins is the moment the evidence stops. Completion receipts were measured growing at
/// roughly one per committed write transaction, per node, with no plateau inside a 12-minute window
/// and a reclaim counter reading 7 for a whole run; whether that is a bounded backlog or an unbounded
/// obligation is exactly what the post-load window shows.</para>
///
/// <para><b>Raw text, not parsed.</b> Each scrape is saved verbatim as
/// <c>post-run-metrics-{node}-{seconds}s.txt</c>. Parsing here would mean maintaining a Prometheus
/// reader in the harness and deciding in advance which series matter; the raw dumps cost a few
/// hundred kilobytes and let a later question be asked of an earlier run. The filename carries the
/// offset from the workload's end, so the series is reconstructable without reading file timestamps
/// that a copy or an archive would not preserve.</para>
///
/// <para>Host-side, over each node's mapped REST port, because the workload container that did the
/// in-run scraping is gone by now — this is the one collection the harness must do itself. It runs
/// before teardown and never fails a run: an unreachable node during the drain window is recorded as
/// a note, since the run's real verdict was settled by the measured window that already finished.</para>
/// </summary>
public static class DrainObserver
{
    /// <summary>
    /// Scrapes each node every <paramref name="interval"/> for <paramref name="duration"/>, starting
    /// immediately. Returns notes describing what was collected or what failed.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ObserveAsync(
        ClusterPlan plan,
        string outputDirectory,
        TimeSpan duration,
        TimeSpan interval,
        CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero)
            return [];

        List<string> notes = [];
        Directory.CreateDirectory(outputDirectory);

        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(10) };

        DateTime start = DateTime.UtcNow;
        DateTime deadline = start + duration;
        int scrapes = 0;
        int failures = 0;

        Console.WriteLine(
            $"==> observing drain for {duration.TotalSeconds:0}s (every {interval.TotalSeconds:0}s) " +
            "after the load stopped");

        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            int offsetSeconds = (int)Math.Round((DateTime.UtcNow - start).TotalSeconds);

            foreach (NodePlan node in plan.Nodes)
            {
                string url = $"http://localhost:{node.HostRestPort}/metrics";
                string destination = Path.Combine(outputDirectory, $"post-run-metrics-{node.Name}-{offsetSeconds:D5}s.txt");

                try
                {
                    string body = await client.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
                    await File.WriteAllTextAsync(destination, body, cancellationToken).ConfigureAwait(false);
                    scrapes++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception e)
                {
                    // Never fatal: the measured window is already over and its verdict already decided.
                    failures++;
                    if (failures <= 3)
                        notes.Add($"drain scrape of {node.Name} at +{offsetSeconds}s failed: {e.Message}");
                }
            }

            TimeSpan remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                break;

            await Task.Delay(interval < remaining ? interval : remaining, cancellationToken).ConfigureAwait(false);
        }

        notes.Add(
            failures == 0
                ? $"drain observed for {duration.TotalSeconds:0}s after the load stopped: {scrapes} scrape(s) " +
                  $"across {plan.Nodes.Count} node(s) in post-run-metrics-*.txt"
                : $"drain observed for {duration.TotalSeconds:0}s after the load stopped: {scrapes} scrape(s), " +
                  $"{failures} failed");

        return notes;
    }
}
