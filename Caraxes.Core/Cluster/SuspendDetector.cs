/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Diagnostics;

namespace Caraxes.Core.Cluster;

/// <summary>
/// Detects that the host was suspended while a run was in progress.
///
/// <para>It exists because a suspended host produces an artifact that looks completely normal. A
/// twelve-table seed once appeared to stall for 6h18m: three node logs fell silent in the same
/// second and resumed together six hours later, the run then completed its measured window and would
/// have been reported <c>PASS</c>. The cluster had done nothing wrong — the laptop had slept. Nothing
/// in the run's own evidence could say so, and the shape is a convincing imitation of a real
/// engine-wide stall, so the first reading of it was a defect report against the database.</para>
///
/// <para><b>Why a run that spans a suspend must be rejected rather than annotated.</b> Every number
/// the harness produces is a rate or a latency over a window, and the window's two clocks disagree
/// across a suspend: throughput is computed from an interval the host did not experience, the
/// workload's own timers stop while the wall clock does not, and per-second series acquire a gap no
/// consumer expects. A recovery-time threshold measured across one is meaningless. There is no
/// salvage — the run has to be repeated.</para>
///
/// <para><b>How it is detected.</b> Two clocks that agree on an awake host and diverge on a
/// suspended one: <see cref="Stopwatch"/> reads <c>CLOCK_MONOTONIC</c>, which does not advance while
/// the system is suspended, and <see cref="DateTime.UtcNow"/> reads the wall clock, which does. Their
/// difference over the same interval is the time the host spent asleep. This needs no privileges, no
/// journal access, and no platform-specific API — it is the same signal as the
/// <c>CLOCK_BOOTTIME − CLOCK_MONOTONIC</c> divergence, measured over a window rather than since
/// boot.</para>
///
/// <para>A large forward step of the wall clock (an NTP correction on a machine whose clock was badly
/// wrong) would also trip this. That is acceptable and arguably correct: a measurement window whose
/// wall clock jumped is not one to publish a rate from either.</para>
/// </summary>
public sealed class SuspendDetector
{
    /// <summary>
    /// Divergence below this is ordinary scheduling and clock-adjustment noise, not a suspend. A
    /// suspend is seconds to hours; NTP slewing is milliseconds. Two seconds sits far above the
    /// noise and far below anything worth reporting.
    /// </summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(2);

    private readonly Stopwatch monotonic;

    private readonly DateTime startedUtc;

    private SuspendDetector(Stopwatch monotonic, DateTime startedUtc)
    {
        this.monotonic = monotonic;
        this.startedUtc = startedUtc;
    }

    /// <summary>Starts watching. Call at the very beginning of a run, before anything is built or started.</summary>
    public static SuspendDetector Start() => new(Stopwatch.StartNew(), DateTime.UtcNow);

    /// <summary>Wall-clock time elapsed since <see cref="Start"/>.</summary>
    public TimeSpan Wall => DateTime.UtcNow - startedUtc;

    /// <summary>Awake time elapsed since <see cref="Start"/>.</summary>
    public TimeSpan Awake => monotonic.Elapsed;

    /// <summary>
    /// How long the host was suspended since <see cref="Start"/>. Never negative: a backwards wall-clock
    /// step (an NTP correction the other way) reads as zero rather than as negative sleep.
    /// </summary>
    public TimeSpan Suspended
    {
        get
        {
            TimeSpan difference = Wall - Awake;
            return difference > TimeSpan.Zero ? difference : TimeSpan.Zero;
        }
    }

    /// <summary>Whether the host slept long enough to invalidate the run.</summary>
    public bool HostSlept => Suspended > Tolerance;

    /// <summary>
    /// The failure note for a run that spanned a suspend, or null when the host stayed awake.
    /// Separate from the sampling so the message is unit-testable without suspending anything.
    /// </summary>
    public static string? Describe(TimeSpan suspended, TimeSpan wall)
        => suspended > Tolerance
            ? $"THE HOST SLEPT FOR {Format(suspended)} DURING THIS RUN (wall clock {Format(wall)}, " +
              $"awake {Format(wall - suspended)}). Every rate and latency here was computed over a window " +
              "the machine did not experience, and the per-second series carry a gap. The run is not a " +
              "measurement; repeat it on a machine that stays awake."
            : null;

    private static string Format(TimeSpan span)
        => span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h{span.Minutes:00}m{span.Seconds:00}s"
            : span.TotalMinutes >= 1
                ? $"{(int)span.TotalMinutes}m{span.Seconds:00}s"
                : $"{span.TotalSeconds:N1}s";
}
