/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Collections.Concurrent;
using System.Diagnostics;
using Caraxes.Core.Cluster;

namespace Caraxes.Core.Nemesis;

/// <summary>
/// Executes a nemesis schedule against a running cluster, concurrently with the workload, writing
/// every injection and heal to a JSONL timeline. Two guarantees make it safe to run alongside a
/// measured workload:
/// <list type="bullet">
/// <item>Faults are best-effort — an inject/heal that errors is logged and swallowed, never
/// propagated, so a docker hiccup cannot fail the run the nemesis is decorating.</item>
/// <item>Every healable fault still in effect when the schedule stops (the workload finished, or the
/// user interrupted) is healed on the way out with a fresh, non-cancelled token — the cluster is
/// never left partitioned or throttled after the run.</item>
/// </list>
/// </summary>
public sealed class NemesisRunner
{
    private readonly ClusterPlan plan;

    private readonly HttpProbes probes;

    // Faults currently in effect, keyed by a unique id. Heal claims an entry with TryRemove, so a
    // fault is healed by exactly one of {its own hold-then-heal, the stop-time sweep} — never twice.
    private readonly ConcurrentDictionary<long, ActiveFault> active = new();

    private long nextId;

    public NemesisRunner(ClusterPlan plan, HttpProbes probes)
    {
        this.plan = plan;
        this.probes = probes;
    }

    public async Task RunAsync(NemesisSpec spec, string timelinePath, CancellationToken stopToken)
    {
        spec.Validate();

        DateTime startUtc = DateTime.UtcNow;
        Stopwatch clock = Stopwatch.StartNew();
        Random rng = new(spec.Seed);
        TargetSelector selector = new(plan, rng);

        using TimelineWriter timeline = new(timelinePath, startUtc);
        timeline.Write("note", "start", null,
            spec.Random is not null ? "random schedule" : $"{spec.Events.Count} scheduled event(s)", DateTime.UtcNow);

        try
        {
            if (spec.Random is not null)
                await RunRandomAsync(spec.Random, selector, rng, timeline, clock, stopToken).ConfigureAwait(false);
            else
                await RunEventsAsync(spec.Events, selector, timeline, clock, stopToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The workload finished (or the user interrupted); fall through to the heal sweep.
        }
        finally
        {
            await HealRemainingAsync(timeline).ConfigureAwait(false);
            timeline.Write("note", "stop", null, "nemesis complete", DateTime.UtcNow);
        }
    }

    private async Task RunEventsAsync(
        IReadOnlyList<NemesisEvent> events, TargetSelector selector, TimelineWriter timeline,
        Stopwatch clock, CancellationToken stopToken)
    {
        // Resolve targets up front in list order so the seeded 'random' picks are deterministic and
        // not raced by the concurrent event tasks. A group target (e.g. zone:za) resolves to several
        // nodes the fault is applied to together.
        var scheduled = events.Select(e => (Event: e, Targets: selector.ResolveGroup(e.Target))).ToList();

        Task[] tasks = scheduled
            .Select(s => RunOneEventAsync(s.Event, s.Targets, timeline, clock, stopToken))
            .ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task RunOneEventAsync(
        NemesisEvent e, IReadOnlyList<NodePlan> targets, TimelineWriter timeline, Stopwatch clock, CancellationToken stopToken)
    {
        // One stateless fault instance serves every node in the group; the fault operates on the node
        // passed to inject/heal, so the (fault, node) pair stays unique per node in the active set.
        IFault fault = FaultFactory.Create(e, probes);
        bool healable = fault.Healable && (e.Heal ?? true);

        try
        {
            await DelayUntilAsync(DurationParser.Parse(e.At), clock, stopToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // stopped before this fault was ever injected
        }

        List<NodePlan> injected = [];
        foreach (NodePlan target in targets)
        {
            if (await InjectAsync(fault, target, healable, timeline, stopToken).ConfigureAwait(false))
                injected.Add(target);
        }

        if (!healable || injected.Count == 0)
            return;

        try
        {
            await Task.Delay(DurationParser.Parse(e.Duration), stopToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // leave the group for the stop-time heal sweep
        }

        foreach (NodePlan target in injected)
            await HealTrackedAsync(fault, target, timeline).ConfigureAwait(false);
    }

    private async Task RunRandomAsync(
        RandomNemesisSpec spec, TargetSelector selector, Random rng, TimelineWriter timeline,
        Stopwatch clock, CancellationToken stopToken)
    {
        TimeSpan min = DurationParser.Parse(spec.MinInterval);
        TimeSpan max = DurationParser.Parse(spec.MaxInterval);
        TimeSpan hold = DurationParser.Parse(spec.Duration);
        int injected = 0;

        while (!stopToken.IsCancellationRequested && (spec.Count == 0 || injected < spec.Count))
        {
            TimeSpan wait = min + (max - min) * rng.NextDouble();
            try
            {
                await Task.Delay(wait, stopToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            string kind = spec.Faults[rng.Next(spec.Faults.Count)];
            NodePlan target = selector.Resolve("random");
            NemesisEvent e = new() { Fault = kind, Duration = spec.Duration };
            IFault fault = FaultFactory.Create(e, probes);
            bool healable = fault.Healable;

            injected++;

            if (!await InjectAsync(fault, target, healable, timeline, stopToken).ConfigureAwait(false))
                continue;

            if (!healable)
                continue;

            try
            {
                await Task.Delay(hold, stopToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break; // leave it for the stop-time heal sweep
            }

            await HealTrackedAsync(fault, target, timeline).ConfigureAwait(false);
        }
    }

    private async Task<bool> InjectAsync(
        IFault fault, NodePlan target, bool healable, TimelineWriter timeline, CancellationToken stopToken)
    {
        long id = Interlocked.Increment(ref nextId);
        if (healable)
            active[id] = new ActiveFault(id, fault, target);

        try
        {
            await fault.InjectAsync(target, plan, stopToken).ConfigureAwait(false);
            timeline.Write("inject", fault.Kind, target.Name, fault.Describe(target), DateTime.UtcNow);
            return true;
        }
        catch (OperationCanceledException)
        {
            active.TryRemove(id, out _);
            throw;
        }
        catch (Exception ex)
        {
            active.TryRemove(id, out _);
            timeline.Write("error", fault.Kind, target.Name, $"inject failed: {ex.Message}", DateTime.UtcNow);
            return false;
        }
    }

    private async Task HealTrackedAsync(IFault fault, NodePlan target, TimelineWriter timeline)
    {
        // Claim this fault's active entry so the stop-time sweep does not also heal it.
        long? id = null;
        foreach (KeyValuePair<long, ActiveFault> kv in active)
        {
            if (kv.Value.Fault == fault && kv.Value.Target.Index == target.Index && active.TryRemove(kv.Key, out _))
            {
                id = kv.Key;
                break;
            }
        }

        if (id is null)
            return; // already healed by the sweep

        await HealAsync(fault, target, timeline).ConfigureAwait(false);
    }

    private async Task HealRemainingAsync(TimelineWriter timeline)
    {
        foreach (long key in active.Keys.ToList())
        {
            if (active.TryRemove(key, out ActiveFault? af))
                await HealAsync(af.Fault, af.Target, timeline).ConfigureAwait(false);
        }
    }

    private async Task HealAsync(IFault fault, NodePlan target, TimelineWriter timeline)
    {
        // Heal must complete even though the run's token is cancelled — leaving a node partitioned or
        // throttled after the run would poison the next scenario and the operator's machine.
        try
        {
            await fault.HealAsync(target, plan, CancellationToken.None).ConfigureAwait(false);
            timeline.Write("heal", fault.Kind, target.Name, $"healed {fault.Kind} on {target.Name}", DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            timeline.Write("error", fault.Kind, target.Name, $"heal failed: {ex.Message}", DateTime.UtcNow);
        }
    }

    private static async Task DelayUntilAsync(TimeSpan at, Stopwatch clock, CancellationToken token)
    {
        TimeSpan remaining = at - clock.Elapsed;
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining, token).ConfigureAwait(false);
    }

    private sealed record ActiveFault(long Id, IFault Fault, NodePlan Target);
}
