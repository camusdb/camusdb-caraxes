/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Globalization;

namespace Caraxes.Core.Cluster;

/// <summary>
/// Background sampler that runs for the whole measured window and writes two artifacts into the
/// run directory: <c>node-health.csv</c> (one <c>/ping</c> probe per node per sample) and
/// <c>memory-samples.csv</c> (per-container RSS/CPU from <c>docker stats</c>, in the same schema
/// earlier soak runs produced by hand). It exists because a node process can die while its
/// container stays <c>Up</c> — docker state alone said "healthy" for a node that served nothing
/// for five minutes — so liveness must be observed at the REST port, on a wall-clock series the
/// verdict can correlate with the fault timeline (<see cref="Verdict.NodeHealthAnalysis"/>).
/// Sampling failures are recorded or skipped, never thrown: the monitor must not be able to kill
/// or distort the run it observes.
/// </summary>
public sealed class NodeMonitor
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);

    private readonly ClusterPlan plan;

    private readonly HttpProbes probes;

    private readonly string healthPath;

    private readonly string memoryPath;

    private readonly TimeSpan interval;

    public NodeMonitor(ClusterPlan plan, HttpProbes probes, string runDir, TimeSpan? interval = null)
    {
        this.plan = plan;
        this.probes = probes;
        this.interval = interval ?? DefaultInterval;
        healthPath = Path.Combine(runDir, "node-health.csv");
        memoryPath = Path.Combine(runDir, "memory-samples.csv");
    }

    /// <summary>
    /// Samples until <paramref name="stopToken"/> is cancelled. The first sample is immediate, so
    /// even a run that dies early leaves at least one row. Cancellation is the normal exit; it is
    /// never surfaced as an error.
    /// </summary>
    public async Task RunAsync(CancellationToken stopToken)
    {
        File.WriteAllText(healthPath, "ts,node,reachable\n");
        File.WriteAllText(memoryPath, "ts,container,mem_mib,limit_mib,mem_pct,cpu_pct,state\n");

        while (!stopToken.IsCancellationRequested)
        {
            await SampleOnceAsync(stopToken).ConfigureAwait(false);

            try
            {
                await Task.Delay(interval, stopToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SampleOnceAsync(CancellationToken stopToken)
    {
        string ts = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        List<string> healthRows = new(plan.Nodes.Count);
        foreach (NodePlan node in plan.Nodes)
        {
            bool reachable = await probes
                .PingAsync($"http://localhost:{node.HostRestPort}", stopToken).ConfigureAwait(false);
            healthRows.Add($"{ts},{node.Name},{(reachable ? "true" : "false")}");
        }

        TryAppend(healthPath, healthRows);
        TryAppend(memoryPath, await SampleMemoryAsync(ts, stopToken).ConfigureAwait(false));
    }

    /// <summary>
    /// One <c>docker stats --no-stream</c> pass plus one <c>docker inspect</c> for container state.
    /// A docker failure (daemon busy, container gone mid-teardown) yields zero rows for this
    /// sample; the health series is the liveness signal, memory is best-effort telemetry.
    /// </summary>
    private async Task<List<string>> SampleMemoryAsync(string ts, CancellationToken stopToken)
    {
        List<string> rows = [];
        List<string> containers = plan.Nodes.Select(n => n.ContainerName).ToList();

        try
        {
            ProcessResult stats = await ProcessRunner.RunAsync(
                "docker",
                ["stats", "--no-stream", "--format", "{{.Name}};{{.MemUsage}};{{.MemPerc}};{{.CPUPerc}}", .. containers],
                cancellationToken: stopToken).ConfigureAwait(false);

            ProcessResult inspect = await ProcessRunner.RunAsync(
                "docker",
                ["inspect", "-f", "{{.Name}};{{.State.Status}}", .. containers],
                cancellationToken: stopToken).ConfigureAwait(false);

            Dictionary<string, string> stateByContainer = [];
            foreach (string line in inspect.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] parts = line.Split(';');
                if (parts.Length == 2)
                    stateByContainer[parts[0].TrimStart('/')] = parts[1];
            }

            foreach (string line in stats.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] parts = line.Split(';');
                if (parts.Length != 4)
                    continue;

                (double memMib, double limitMib) = ParseMemUsage(parts[1]);
                string state = stateByContainer.GetValueOrDefault(parts[0], "unknown");
                rows.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{ts},{parts[0]},{memMib:0.0},{limitMib:0.0},{parts[2].TrimEnd('%')},{parts[3].TrimEnd('%')},{state}"));
            }
        }
        catch (Exception) when (!stopToken.IsCancellationRequested)
        {
            // Best-effort telemetry; a failed docker call must not disturb the run.
        }

        return rows;
    }

    /// <summary>
    /// Parses a docker <c>MemUsage</c> cell (for example <c>"570.9MiB / 1.5GiB"</c>) into
    /// (used, limit) in MiB. Unparseable input yields zeros rather than an exception, because a
    /// docker format change must degrade the telemetry, not the run.
    /// </summary>
    public static (double MemMib, double LimitMib) ParseMemUsage(string memUsage)
    {
        string[] parts = memUsage.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            return (0, 0);

        return (ParseSizeToMib(parts[0]), ParseSizeToMib(parts[1]));
    }

    private static double ParseSizeToMib(string size)
    {
        int unitStart = 0;
        while (unitStart < size.Length && (char.IsAsciiDigit(size[unitStart]) || size[unitStart] == '.'))
            unitStart++;

        if (unitStart == 0 ||
            !double.TryParse(size[..unitStart], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            return 0;

        return size[unitStart..].Trim() switch
        {
            "B" => value / (1024.0 * 1024.0),
            "KiB" or "kB" => value / 1024.0,
            "MiB" or "MB" => value,
            "GiB" or "GB" => value * 1024.0,
            "TiB" or "TB" => value * 1024.0 * 1024.0,
            _ => 0,
        };
    }

    private static void TryAppend(string path, IReadOnlyList<string> rows)
    {
        if (rows.Count == 0)
            return;

        try
        {
            File.AppendAllLines(path, rows);
        }
        catch (IOException)
        {
            // A transient write failure loses one sample, never the run.
        }
    }
}
