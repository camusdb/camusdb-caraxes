/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using Caraxes.Core.Cluster;

namespace Caraxes.Core.Nemesis;

/// <summary>
/// Sends an HTTP request to a node's REST port from inside that node's network namespace.
///
/// <para>CamusDB's topology mutations (<c>/v1/cluster/leave</c>, <c>/v1/cluster/transfer-leadership</c>)
/// are superuser-only with authentication on and loopback-only with it off, and the harness cluster
/// runs without authentication. A request from the host arrives through Docker's port proxy with
/// the bridge gateway as its source, not loopback, and is refused. So the request runs in a throwaway
/// <c>curlimages/curl</c> container that shares the target container's network namespace, where
/// 127.0.0.1:5095 is the node's REST port and the source address is loopback.</para>
/// </summary>
public static class InContainerHttp
{
    public const string CurlImage = "curlimages/curl:latest";

    /// <summary>In-container REST port of every harness node.</summary>
    public const int RestPort = 5095;

    /// <summary>
    /// POSTs <paramref name="path"/> on <paramref name="target"/> and returns the response body.
    /// A non-2xx status is not an error: the cluster endpoints report a refused outcome with the
    /// same body shape and a non-2xx code, so the caller reads the body. The request is bounded by
    /// <paramref name="maxTime"/>; the node aborts the operation when the request is aborted, so
    /// the bound must cover the operation, not only the round trip.
    /// </summary>
    public static async Task<string> PostAsync(
        NodePlan target, string path, string? jsonBody, TimeSpan maxTime, CancellationToken cancellationToken)
    {
        List<string> args =
        [
            "run", "--rm", "--network", $"container:{target.ContainerName}", CurlImage,
            "-sS", "--max-time", ((int)Math.Ceiling(maxTime.TotalSeconds)).ToString(), "-X", "POST",
        ];

        if (jsonBody is not null)
            args.AddRange(["-H", "Content-Type: application/json", "-d", jsonBody]);

        args.Add($"http://127.0.0.1:{RestPort}{path}");

        ProcessResult run = await ProcessRunner.RunCheckedAsync("docker", args, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return run.StdOut;
    }
}
