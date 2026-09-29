/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Globalization;
using System.Text.Json;

namespace Caraxes.Core.Verdict;

/// <summary>A fault's active interval on the wall clock: from its injection to its heal (or to the
/// end of observation, for a fault that was never healed — a crash or a permanent partition).</summary>
public sealed class FaultWindow
{
    public string Kind { get; init; } = "";

    public string? Target { get; init; }

    /// <summary>When the nemesis issued the fault, not when its command returned: a <c>docker kill</c>
    /// on a saturated device has taken seven seconds to return after the node was already dead.</summary>
    public DateTime StartUtc { get; init; }

    /// <summary>Heal time, or null if the fault was never healed (crash / permanent change).</summary>
    public DateTime? EndUtc { get; init; }

    public bool Healed => EndUtc is not null;

    public string Label => $"{Kind}{(Target is null ? "" : $"/{Target}")}";
}

/// <summary>
/// Parses <c>timeline.jsonl</c> into fault windows by pairing each <c>inject</c> with the next
/// <c>heal</c> of the same kind+target. An inject with no matching heal (the fault outlived the run,
/// e.g. crash-then-repair or remove-node) yields an open-ended window the correlator closes at the
/// end of the observed series.
///
/// <para>A window opens at the inject's <c>issuedTs</c>. Timelines written before injects carried
/// it fall back to the <c>note</c> that resolved a <c>leader</c> target to this node, which the
/// nemesis writes immediately before issuing the fault, and otherwise to the inject's own
/// <c>ts</c> (when its command returned).</para>
/// </summary>
public static class FaultTimeline
{
    public static IReadOnlyList<FaultWindow> Parse(string timelinePath)
    {
        if (!File.Exists(timelinePath))
            return [];

        var pendingByLabel = new Dictionary<string, (string Kind, string? Target, DateTime Start)>();
        var resolvedAtByLabel = new Dictionary<string, DateTime>();
        List<FaultWindow> windows = [];

        foreach (string line in File.ReadLines(timelinePath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            TimelineRecord? rec = TryParse(line);
            if (rec is null)
                continue;

            string label = $"{rec.Kind}/{rec.Target}";

            switch (rec.Phase)
            {
                case "note":
                    if (rec.Target is not null)
                        resolvedAtByLabel[label] = rec.Ts;
                    break;

                case "error":
                    resolvedAtByLabel.Remove(label);
                    break;

                case "inject":
                    DateTime start = rec.Ts;
                    if (rec.IssuedTs is DateTime issuedAt)
                        start = issuedAt;
                    else if (resolvedAtByLabel.TryGetValue(label, out DateTime resolvedAt) && resolvedAt <= rec.Ts)
                        start = resolvedAt;
                    resolvedAtByLabel.Remove(label);

                    // A second inject of the same label before a heal (should not happen in a
                    // well-formed run) closes the previous window at this instant to stay well-defined.
                    if (pendingByLabel.TryGetValue(label, out var prev))
                        windows.Add(new FaultWindow { Kind = prev.Kind, Target = prev.Target, StartUtc = prev.Start, EndUtc = start });
                    pendingByLabel[label] = (rec.Kind, rec.Target, start);
                    break;

                case "heal":
                    if (pendingByLabel.Remove(label, out var open))
                        windows.Add(new FaultWindow { Kind = open.Kind, Target = open.Target, StartUtc = open.Start, EndUtc = rec.Ts });
                    break;
            }
        }

        // Injects never healed (crash, remove-node): open-ended windows.
        foreach ((string Kind, string? Target, DateTime Start) open in pendingByLabel.Values)
            windows.Add(new FaultWindow { Kind = open.Kind, Target = open.Target, StartUtc = open.Start, EndUtc = null });

        return windows.OrderBy(w => w.StartUtc).ToList();
    }

    private static TimelineRecord? TryParse(string line)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;

            string phase = root.GetProperty("phase").GetString() ?? "";
            if (phase is not ("inject" or "heal" or "note" or "error"))
                return null;

            string kind = root.TryGetProperty("kind", out JsonElement k) ? k.GetString() ?? "" : "";
            string? target = root.TryGetProperty("target", out JsonElement t) && t.ValueKind != JsonValueKind.Null
                ? t.GetString()
                : null;

            if (!root.TryGetProperty("ts", out JsonElement tsEl) ||
                !DateTime.TryParse(tsEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime ts))
                return null;

            DateTime? issuedTs = null;
            if (root.TryGetProperty("issuedTs", out JsonElement issuedEl) &&
                DateTime.TryParse(issuedEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime issued))
                issuedTs = issued.ToUniversalTime();

            return new TimelineRecord(phase, kind, target, ts.ToUniversalTime(), issuedTs);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed record TimelineRecord(string Phase, string Kind, string? Target, DateTime Ts, DateTime? IssuedTs);
}
