/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Runtime.InteropServices;

namespace Caraxes.Core.Cluster;

/// <summary>A load-average reading from the host, with the core count it should be judged against.</summary>
/// <param name="One">1-minute load average.</param>
/// <param name="Five">5-minute load average.</param>
/// <param name="Fifteen">15-minute load average.</param>
/// <param name="ProcessorCount">Host cores, so a reading can be normalised.</param>
public sealed record HostLoadSample(double One, double Five, double Fifteen, int ProcessorCount)
{
    /// <summary>Runnable work per core. Around 1.0 means fully committed; well above means oversubscribed.</summary>
    public double PerCore => ProcessorCount > 0 ? One / ProcessorCount : 0;
}

/// <summary>
/// Reads the <b>host's</b> load average, to detect work competing with a measurement.
///
/// <para>This exists because of a measurement that was quietly wrong. A six-run A/B produced
/// coefficients of variation of 15% and 22% against a 4% baseline, and its control arm came in 13%
/// below two earlier measurements of the identical configuration. The cause was an unrelated process
/// taking 2.3 cores for part of the session. Nothing in the harness noticed: the run's own
/// <c>client-resources.json</c> correctly reported the load generator as healthy, because the
/// generator <em>was</em> healthy — it was the machine underneath that was contended.</para>
///
/// <para>The host reading is the one that matters, and it cannot be taken from inside the cluster.
/// Docker Desktop runs the containers in a Linux VM with its own kernel, so a load average sampled in
/// there describes the VM and stays low while macOS processes outside it compete for the same
/// physical cores.</para>
///
/// <para>Sampled before the harness starts anything of its own, so it measures <em>ambient</em> load.
/// A sample taken mid-run would be dominated by the cluster and the generator, which are the very
/// work the run is supposed to be doing.</para>
/// </summary>
public static class HostLoad
{
    [DllImport("libc", SetLastError = true)]
    private static extern int getloadavg([Out] double[] loadavg, int nelem);

    /// <summary>
    /// The current load average, or null where the platform does not provide one (Windows has no
    /// <c>getloadavg</c>). A null reading is reported as "not measured" and never as "quiet" — an
    /// unknown machine state must not read as a clean one.
    /// </summary>
    public static HostLoadSample? Read()
    {
        try
        {
            double[] values = new double[3];
            if (getloadavg(values, 3) != 3)
                return null;

            return new HostLoadSample(values[0], values[1], values[2], Environment.ProcessorCount);
        }
        catch (Exception)
        {
            // DllNotFoundException / EntryPointNotFoundException on a platform without it. Absence of
            // the reading is not a failure of the run.
            return null;
        }
    }

    /// <summary>
    /// Whether ambient load is high enough to distort a measurement. Half the cores is the bar: a
    /// quiet desktop sits near 2-4 on a 10-core machine, while the session that prompted this saw 12
    /// to 30.
    /// </summary>
    public static bool IsContended(HostLoadSample sample) => sample.PerCore > 0.5;

    /// <summary>
    /// The quiet-host verdict for a scenario that asked for one, given the <b>ambient</b> sample —
    /// the reading taken before the harness started anything of its own.
    ///
    /// <para>Pure, and separate from the sampling, because which sample is graded is the whole
    /// question: a reading taken after the workload finished is dominated by the cluster and
    /// generator the run itself started, so grading that one fails a scenario for the crime of
    /// loading the machine, and does it more readily the higher the concurrency. A load average
    /// cannot attribute its own contributors, so only the pre-run sample can carry a verdict.</para>
    ///
    /// <para>An unmeasured ambient fails the check rather than passing it: a scenario that asked for
    /// a quiet machine and cannot be told whether it got one has not met its condition.</para>
    /// </summary>
    /// <returns>The failure reason, or null when the check passes.</returns>
    public static string? GradeAmbient(HostLoadSample? ambient)
    {
        if (ambient is null)
            return "checks.require_quiet_host is on and ambient host load could not be measured";

        return IsContended(ambient)
            ? $"checks.require_quiet_host is on and ambient host load before the run was " +
              $"{ambient.One:N2} over {ambient.ProcessorCount} core(s)"
            : null;
    }
}
