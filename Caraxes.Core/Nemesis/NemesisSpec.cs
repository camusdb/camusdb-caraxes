/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

namespace Caraxes.Core.Nemesis;

/// <summary>
/// The fault schedule for a scenario. Either an explicit <see cref="Events"/> timeline (each event
/// timed from the start of the workload run) or a seeded <see cref="Random"/> soak that keeps
/// injecting until the workload finishes — not both.
/// </summary>
public sealed class NemesisSpec
{
    /// <summary>Seed for target selection and the random schedule, so a nemesis run is reproducible.</summary>
    public int Seed { get; set; } = 1;

    public List<NemesisEvent> Events { get; set; } = [];

    public RandomNemesisSpec? Random { get; set; }

    public void Validate()
    {
        if (Events.Count > 0 && Random is not null)
            throw new NemesisException("nemesis has both 'events' and 'random'; use one or the other");

        if (Events.Count == 0 && Random is null)
            throw new NemesisException("nemesis has neither 'events' nor 'random'; give it something to do");

        foreach (NemesisEvent e in Events)
            e.Validate();

        Random?.Validate();
    }
}

/// <summary>
/// One scheduled fault: what, to whom, when, and for how long. <see cref="At"/> and
/// <see cref="Duration"/> are compact duration strings (<c>20s</c>, <c>1m</c>). A non-healable fault
/// (crash, remove-node) ignores duration.
/// </summary>
public sealed class NemesisEvent
{
    /// <summary>Fault kind: kill | stop | pause | partition | slow | loss | remove-node.</summary>
    public string Fault { get; set; } = "";

    /// <summary>Target: a node name (camusN) or <c>random</c>.</summary>
    public string Target { get; set; } = "random";

    /// <summary>Offset from the start of the workload run at which the fault is injected.</summary>
    public string At { get; set; } = "0s";

    /// <summary>How long the fault is held before healing. Ignored for non-healable faults.</summary>
    public string Duration { get; set; } = "10s";

    /// <summary>Override healing: <c>false</c> turns a kill into crash-then-repair (never restarted)
    /// or holds a partition open for the rest of the run. Null keeps the fault's own default.</summary>
    public bool? Heal { get; set; }

    /// <summary>Added latency in ms for the <c>slow</c> fault.</summary>
    public int DelayMs { get; set; } = 100;

    /// <summary>Packet loss percentage for the <c>loss</c> fault.</summary>
    public double LossPercent { get; set; } = 10;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Fault))
            throw new NemesisException("a nemesis event is missing 'fault'");

        FaultFactory.EnsureKnownKind(Fault);

        try
        {
            DurationParser.Parse(At);
            DurationParser.Parse(Duration);
        }
        catch (FormatException e)
        {
            throw new NemesisException($"nemesis event '{Fault}': {e.Message}");
        }
    }
}

/// <summary>A seeded random soak: pick a fault from <see cref="Faults"/> for a random node every
/// <see cref="MinInterval"/>–<see cref="MaxInterval"/>, hold it <see cref="Duration"/>, repeat up to
/// <see cref="Count"/> times (0 = until the workload ends).</summary>
public sealed class RandomNemesisSpec
{
    public List<string> Faults { get; set; } = [];

    public string MinInterval { get; set; } = "10s";

    public string MaxInterval { get; set; } = "20s";

    public string Duration { get; set; } = "10s";

    /// <summary>Maximum number of faults to inject; 0 = keep going until the workload finishes.</summary>
    public int Count { get; set; }

    public void Validate()
    {
        if (Faults.Count == 0)
            throw new NemesisException("nemesis.random.faults is empty");

        foreach (string fault in Faults)
            FaultFactory.EnsureKnownKind(fault);

        try
        {
            TimeSpan min = DurationParser.Parse(MinInterval);
            TimeSpan max = DurationParser.Parse(MaxInterval);
            DurationParser.Parse(Duration);
            if (min > max)
                throw new NemesisException(
                    $"nemesis.random.min_interval ({MinInterval}) must be <= max_interval ({MaxInterval})");
        }
        catch (FormatException e)
        {
            throw new NemesisException($"nemesis.random: {e.Message}");
        }
    }
}

/// <summary>An invalid nemesis specification; the message names the problem.</summary>
public sealed class NemesisException : Exception
{
    public NemesisException(string message) : base(message)
    {
    }
}
