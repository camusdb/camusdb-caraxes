/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

namespace Caraxes.Core.Cluster;

/// <summary>
/// The clock skew shim (<c>tools/clockskew/skew.c</c>): where it lives in the node image, how a node
/// process loads it, and the image layer that carries it. A cluster with
/// <see cref="ClusterSpec.ClockSkew"/> runs every node with the shim preloaded, and the
/// <c>clock-skew</c> fault then moves one node's wall clock by rewriting <see cref="OffsetFile"/>.
///
/// <para>Docker containers share the host kernel's <c>CLOCK_REALTIME</c>, so a node's clock cannot be
/// set on its own (Linux time namespaces offset only the monotonic and boot clocks). The shim shifts
/// the wall clock inside the one process instead, and leaves monotonic time alone: CamusDB, Kahuna and
/// Kommander time elections, leases and timeouts on monotonic ticks, while the HLC's physical
/// component, intent and lock expiries and session expiries read the wall clock.</para>
/// </summary>
public static class ClockSkewShim
{
    /// <summary>Appended to the node image tag to name the layer that carries the shim.</summary>
    public const string ImageSuffix = "-clockskew";

    /// <summary>Path of the shared library inside the node image.</summary>
    public const string LibraryPath = "/usr/local/lib/libcaraxes-skew.so";

    /// <summary>The file the shim reads the offset from: one signed integer, in milliseconds.</summary>
    public const string OffsetFile = "/tmp/caraxes-skew";

    /// <summary>Build context of the layer, relative to the Caraxes checkout (the working directory,
    /// as for <c>tools/elle</c>).</summary>
    public const string BuildContext = "tools/clockskew";

    /// <summary>Environment that makes a node process load the shim. <c>docker exec</c> inherits it,
    /// so <c>docker exec camusN date</c> prints the node's skewed time.</summary>
    public static IReadOnlyDictionary<string, string> Environment { get; } = new Dictionary<string, string>
    {
        ["LD_PRELOAD"] = LibraryPath,
        ["CARAXES_SKEW_FILE"] = OffsetFile,
    };

    /// <summary>
    /// Builds <see cref="ClusterSpec.NodeImage"/> from <see cref="ClusterSpec.EffectiveImage"/>. Runs
    /// on every <c>up</c> of a clock-skew cluster, including one that skipped the CamusDB build: the
    /// layer must sit on the base image as it is now, and with both stages cached the build takes
    /// about a second.
    /// </summary>
    public static async Task BuildImageAsync(ClusterSpec spec, CancellationToken cancellationToken)
    {
        string context = Path.GetFullPath(BuildContext);
        if (!File.Exists(Path.Combine(context, "Dockerfile")))
            throw new InvalidOperationException(
                $"clock skew shim not found at {context}; run Caraxes from the root of its checkout");

        Console.WriteLine($"==> building image {spec.NodeImage} (clock skew shim on {spec.EffectiveImage})");
        await ProcessRunner.RunCheckedAsync(
            "docker",
            ["build", "--build-arg", $"BASE_IMAGE={spec.EffectiveImage}", "-t", spec.NodeImage, "."],
            workingDirectory: context,
            streamOutput: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
