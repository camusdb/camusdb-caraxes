/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Caraxes.Core.Nemesis;

/// <summary>
/// The few Docker Engine API calls the nemesis needs that the <c>docker</c> CLI does not expose.
/// Talks to the daemon over its Unix socket (<c>DOCKER_HOST=unix://…</c> or the default
/// <c>/var/run/docker.sock</c>); docker-group membership is the only privilege required.
/// </summary>
public static class DockerEngineApi
{
    private const string DefaultSocket = "/var/run/docker.sock";

    /// <summary>
    /// <c>POST /containers/{name}/update</c> with a resources document. Docker forwards it to the
    /// running container's runtime, which rewrites the container's cgroup files in place — no
    /// container is created or removed, which is what makes it safe to call while the target's
    /// block I/O is throttled (see <see cref="SlowDiskFault"/>). Returns the daemon's warnings.
    /// </summary>
    public static async Task<IReadOnlyList<string>> UpdateContainerAsync(
        string containerName, string resourcesJson, CancellationToken cancellationToken)
    {
        using HttpClient client = CreateClient();
        using StringContent body = new(resourcesJson, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client
            .PostAsync($"containers/{Uri.EscapeDataString(containerName)}/update", body, cancellationToken)
            .ConfigureAwait(false);

        string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new NemesisException($"docker update of {containerName} failed ({(int)response.StatusCode}): {text.Trim()}");

        return ParseWarnings(text);
    }

    /// <summary>The <c>Warnings</c> array of an update response (null and empty both mean none).</summary>
    public static IReadOnlyList<string> ParseWarnings(string responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson))
            return [];

        using JsonDocument doc = JsonDocument.Parse(responseJson);
        if (!doc.RootElement.TryGetProperty("Warnings", out JsonElement warnings) || warnings.ValueKind != JsonValueKind.Array)
            return [];

        return warnings.EnumerateArray().Select(w => w.GetString() ?? "").Where(w => w.Length > 0).ToList();
    }

    /// <summary>The socket path from <c>DOCKER_HOST</c> when it is a <c>unix://</c> URL, else the default.</summary>
    public static string SocketPath(string? dockerHost)
    {
        if (!string.IsNullOrWhiteSpace(dockerHost) && dockerHost.StartsWith("unix://", StringComparison.Ordinal))
            return dockerHost["unix://".Length..];
        return DefaultSocket;
    }

    private static HttpClient CreateClient()
    {
        string socketPath = SocketPath(Environment.GetEnvironmentVariable("DOCKER_HOST"));
        SocketsHttpHandler handler = new()
        {
            ConnectCallback = async (_, ct) =>
            {
                Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };

        return new HttpClient(handler)
        {
            BaseAddress = new Uri("http://docker/"),
            Timeout = TimeSpan.FromSeconds(60),
        };
    }
}
