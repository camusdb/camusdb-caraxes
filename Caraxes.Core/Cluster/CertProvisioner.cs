/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

namespace Caraxes.Core.Cluster;

/// <summary>
/// Ensures the CamusDB repo's development certificate covers this cluster's SANs (DNS camus1..N+spare
/// and the IPs on the cluster subnet) by running <c>docker/certs/generate.sh</c> in parameterized
/// mode. The inter-node Raft TLS validates the presented cert against the CA anchor baked into the
/// image at build time, so certs and image must change together: <c>up</c> regenerates only when
/// the SAN parameters changed (tracked in a marker file next to the certs) and always rebuilds the
/// image afterwards, which re-bakes both the .pfx and the CA anchor.
/// </summary>
public static class CertProvisioner
{
    private const string MarkerFileName = ".caraxes-sans";

    /// <summary>The SAN parameter fingerprint for a spec; certs regenerate when it changes.</summary>
    public static string SanFingerprint(ClusterSpec spec)
        => $"nodes={spec.Nodes + spec.SpareCerts};subnet={spec.Subnet};first_ip={spec.FirstIp}";

    public static bool NeedsRegeneration(ClusterSpec spec)
    {
        string marker = Path.Combine(CertsDir(spec), MarkerFileName);
        return !File.Exists(marker) || File.ReadAllText(marker).Trim() != SanFingerprint(spec);
    }

    public static async Task EnsureAsync(ClusterSpec spec, CancellationToken cancellationToken = default)
    {
        if (!NeedsRegeneration(spec))
            return;

        string certsDir = CertsDir(spec);
        if (!File.Exists(Path.Combine(certsDir, "generate.sh")))
            throw new InvalidOperationException(
                $"cert script not found at {certsDir}/generate.sh; is 'camusdb_repo' ({spec.EffectiveCamusdbRepo}) a CamusDB checkout?");

        Console.WriteLine($"==> regenerating dev certs ({SanFingerprint(spec)})");

        await ProcessRunner.RunCheckedAsync(
            "sh",
            ["generate.sh"],
            workingDirectory: certsDir,
            environment: new Dictionary<string, string>
            {
                ["NODES"] = spec.Nodes.ToString(),
                ["SPARE"] = spec.SpareCerts.ToString(),
                ["SUBNET"] = spec.Subnet,
                ["FIRST_IP"] = spec.FirstIp.ToString(),
                // Default OUT_DIR (the certs dir itself) on purpose: the image build COPYs the
                // .pfx and .crt from docker/certs, which is what keeps cert and CA anchor paired.
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        File.WriteAllText(Path.Combine(certsDir, MarkerFileName), SanFingerprint(spec) + "\n");
    }

    public static string CertsDir(ClusterSpec spec)
        => Path.Combine(spec.EffectiveCamusdbRepo, "docker", "certs");
}
