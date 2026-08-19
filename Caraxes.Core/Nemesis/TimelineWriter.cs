/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Text.Json;

namespace Caraxes.Core.Nemesis;

/// <summary>
/// Append-only JSONL log of nemesis activity, one event per line, each stamped with a UTC timestamp
/// and an offset (seconds) from the nemesis start. This is the record a verdict engine (Phase 4)
/// correlates against the workload's per-second <c>intervals.csv</c> to attribute error and latency
/// spikes to specific faults, so it is written even for a run whose verdict later passes.
/// Thread-safe: faults inject and heal concurrently.
/// </summary>
public sealed class TimelineWriter : IDisposable
{
    private readonly StreamWriter writer;

    private readonly DateTime startUtc;

    private readonly Lock gate = new();

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public TimelineWriter(string path, DateTime startUtc)
    {
        this.startUtc = startUtc;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        writer = new StreamWriter(path, append: false) { AutoFlush = true };
    }

    /// <param name="phase">inject | heal | error | note.</param>
    public void Write(string phase, string kind, string? target, string detail, DateTime nowUtc)
    {
        var record = new
        {
            ts = nowUtc.ToString("o"),
            offsetSeconds = Math.Round((nowUtc - startUtc).TotalSeconds, 3),
            phase,
            kind,
            target,
            detail,
        };

        string line = JsonSerializer.Serialize(record, JsonOptions);
        lock (gate)
        {
            writer.WriteLine(line);
        }

        Console.WriteLine($"    [nemesis +{record.offsetSeconds,7:0.0}s] {phase} {kind}{(target is null ? "" : $" {target}")}: {detail}");
    }

    public void Dispose() => writer.Dispose();
}
