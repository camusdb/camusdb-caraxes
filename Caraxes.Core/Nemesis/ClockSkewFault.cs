/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Globalization;
using Caraxes.Core.Cluster;

namespace Caraxes.Core.Nemesis;

/// <summary>
/// Moves one node's wall clock by <c>offset_ms</c> (forward when positive, back when negative) and
/// heals by moving it back to real time. Only <c>CLOCK_REALTIME</c> moves; monotonic time, sleeps and
/// timers keep real durations. See <see cref="ClockSkewShim"/> for how, and why a container clock
/// cannot simply be set.
///
/// <para>Inject and heal are both clock jumps, not a gradual drift: the node reads the new offset
/// within 100 ms. CamusDB's HLC takes the maximum of its own clock and every timestamp it receives, so
/// a forward skew on one node pulls the HLC of every node it talks to forward with it, and the HLC
/// floor each partition persists keeps that lead after the heal and across restarts. A backward skew
/// does not move the HLC back; it stalls the node's physical component until real time catches up.
/// The paths a skew can reach are the snapshot clock fence (a read timestamp more than 5 s ahead of
/// the serving node's HLC skips it), intent, lock and session expiries evaluated on another node, and
/// the wall-clock gossip and schema-ack leases.</para>
///
/// <para>Needs <c>clock_skew: true</c> on the cluster, so the nodes start with the shim loaded. Each
/// inject measures the node's skew back (a preloaded <c>date</c> against an unpreloaded one, in the
/// same container) and fails when it does not match, so a node that is not running the shim can never
/// log a fault that did not happen.</para>
/// </summary>
public sealed class ClockSkewFault : IFault
{
    /// <summary>How far the measured skew may differ from the requested one. The two reads run back to
    /// back in one shell, so the difference is process start-up time, a few milliseconds.</summary>
    public const long ToleranceMs = 250;

    private readonly long offsetMs;

    public ClockSkewFault(long offsetMs) => this.offsetMs = offsetMs;

    public string Kind => "clock-skew";

    public bool Healable => true;

    public string Describe(NodePlan target)
        => $"skew {target.ContainerName} wall clock by {FormatOffset(offsetMs)} (CLOCK_REALTIME only)";

    public Task InjectAsync(NodePlan target, ClusterPlan plan, CancellationToken cancellationToken)
        => SetOffsetAsync(target, plan, offsetMs, cancellationToken);

    public Task HealAsync(NodePlan target, ClusterPlan plan, CancellationToken cancellationToken)
        => SetOffsetAsync(target, plan, 0, cancellationToken);

    private static async Task SetOffsetAsync(NodePlan target, ClusterPlan plan, long offsetMs, CancellationToken cancellationToken)
    {
        if (!plan.Spec.ClockSkew)
            throw new InvalidOperationException(
                $"cluster '{plan.Spec.Name}' was started without clock_skew: true, so its nodes do not load the clock skew shim");

        ProcessResult result = await ProcessRunner.RunCheckedAsync(
            "docker", ["exec", target.ContainerName, "sh", "-c", Script(offsetMs)], cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        long measured = ParseMeasuredOffset(result.StdOut);
        if (Math.Abs(measured - offsetMs) > ToleranceMs)
            throw new InvalidOperationException(
                $"{target.ContainerName} wall clock is off by {FormatOffset(measured)} after setting {FormatOffset(offsetMs)}; " +
                $"is the node running {ClockSkewShim.LibraryPath} (LD_PRELOAD)?");
    }

    /// <summary>
    /// The shell run in the node: replace the offset file with a rename (the shim never reads a
    /// partial write), then print the real and the skewed time in milliseconds. <c>date</c> inherits
    /// the container's <c>LD_PRELOAD</c>, and a new process reads the offset file on its first clock
    /// read, so the second value reflects the offset just written.
    /// </summary>
    public static string Script(long offsetMs)
    {
        string file = ClockSkewShim.OffsetFile;
        return $"printf '%s\\n' {offsetMs.ToString(CultureInfo.InvariantCulture)} > {file}.tmp && mv -f {file}.tmp {file} && " +
               "echo \"$(env -u LD_PRELOAD date +%s%3N) $(date +%s%3N)\"";
    }

    /// <summary>Skewed minus real time from the script's output line, in milliseconds.</summary>
    public static long ParseMeasuredOffset(string output)
    {
        string[] parts = output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2
            || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long real)
            || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long skewed))
            throw new InvalidOperationException($"could not read the node's clock skew from '{output.Trim()}'");

        return skewed - real;
    }

    public static string FormatOffset(long offsetMs)
        => (offsetMs >= 0 ? "+" : "") + offsetMs.ToString(CultureInfo.InvariantCulture) + " ms";
}
