/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Text.Json;
using Caraxes.Core.Cluster;
using Caraxes.Core.Scenario;
using Caraxes.Core.Workload;

namespace Caraxes.Core.Verdict;

/// <summary>
/// What Elle said about a history. <see cref="Valid"/> is elle-cli's <c>valid?</c>: <c>true</c>,
/// <c>false</c> or <c>unknown</c>, or null when no verdict could be read at all, in which case
/// <see cref="Error"/> says why.
/// </summary>
public sealed record ElleResult(
    string? Valid,
    IReadOnlyList<string> AnomalyTypes,
    IReadOnlyList<string> NotModels,
    string? Error)
{
    /// <summary>Only an explicit <c>true</c> passes. <c>unknown</c> is a search that gave up, and that
    /// proves nothing about the history.</summary>
    public bool Passed => Valid == "true";

    public static ElleResult Failed(string error) => new(null, [], [], error);
}

/// <summary>
/// Runs elle-cli's list-append checker on an append run's <c>history.edn</c>.
///
/// <para>The check runs in a JDK container, like the workload, so the host needs Docker and nothing
/// else. The run directory is mounted at <c>/work</c>: Elle reads the history there and writes its
/// explanations of any anomaly under <c>elle/</c> beside it.</para>
///
/// <para>The verdict comes from the JSON on stdout, never from the exit code alone. elle-cli exits 1
/// only when a history is invalid; an <c>unknown</c> verdict exits 0, and reading that as a pass would
/// certify a history nobody finished checking.</para>
/// </summary>
public static class ElleCheck
{
    private const string ContainerWorkDir = "/work";

    private const string ContainerJar = "/elle/elle-cli.jar";

    /// <summary>The <c>docker</c> argument list. Separate from the run so it is testable without Docker.</summary>
    public static IReadOnlyList<string> BuildDockerArgs(
        ElleSpec spec, string isolation, string hostRunDir, string hostJar, string containerName)
    {
        List<string> args = ["run", "--rm", "--name", containerName];
        args.AddRange(WorkloadRunner.BuildUserArgs());
        args.AddRange(
        [
            "-v", $"{Path.GetFullPath(hostRunDir)}:{ContainerWorkDir}",
            "-v", $"{Path.GetFullPath(hostJar)}:{ContainerJar}:ro",
            "--entrypoint", "java",
            spec.Image,
        ]);

        if (spec.HeapMb > 0)
            args.Add($"-Xmx{spec.HeapMb}m");

        args.AddRange(
        [
            "-jar", ContainerJar,
            "--model", "list-append",
            "--output", "json",
            "--consistency-models", spec.EffectiveConsistencyModels(isolation),
            "--directory", $"{ContainerWorkDir}/elle",
        ]);

        // 0 bytes is below every graph, so Elle skips each diagram instead of calling a dot it lacks.
        if (!spec.Plots)
        {
            args.Add("--max-plot-bytes");
            args.Add("0");
        }

        if (!string.IsNullOrWhiteSpace(spec.Anomalies))
        {
            args.Add("--anomalies");
            args.Add(spec.Anomalies.Replace(" ", ""));
        }

        if (spec.CycleSearchTimeoutMs > 0)
        {
            args.Add("--cycle-search-timeout");
            args.Add(spec.CycleSearchTimeoutMs.ToString());
        }

        args.Add($"{ContainerWorkDir}/history.edn");
        return args;
    }

    /// <summary>
    /// Checks <c>history.edn</c> in <paramref name="runDir"/> (the workload's output directory) and
    /// writes <c>elle-result.json</c> — elle-cli's own analysis — beside it. Never throws for a check
    /// that could not run; that becomes a result with an <see cref="ElleResult.Error"/>.
    /// </summary>
    public static async Task<ElleResult> RunAsync(
        ElleSpec spec, string isolation, string runDir, string containerName, CancellationToken cancellationToken)
    {
        string history = Path.Combine(runDir, "history.edn");
        if (!File.Exists(history))
            return ElleResult.Failed($"no history.edn in {runDir}; the workload wrote no history to check");

        string jar = Path.GetFullPath(spec.Jar);
        if (!File.Exists(jar))
            return ElleResult.Failed($"elle-cli jar not found at {jar}; run tools/elle/fetch.sh or set 'elle.jar'");

        // A container left by an interrupted check holds the name; it is dead work, as with the workload.
        await ProcessRunner.RunAsync("docker", ["rm", "--force", containerName], cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        ProcessResult result;
        try
        {
            result = await ProcessRunner.RunAsync(
                "docker", BuildDockerArgs(spec, isolation, runDir, jar, containerName),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return ElleResult.Failed($"could not start the Elle container: {e.Message}");
        }

        await File.WriteAllTextAsync(Path.Combine(runDir, "elle-result.json"), result.StdOut, cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(result.StdErr))
            await File.WriteAllTextAsync(Path.Combine(runDir, "elle-stderr.txt"), result.StdErr, cancellationToken)
                .ConfigureAwait(false);

        return Parse(result.StdOut, result.ExitCode);
    }

    /// <summary>
    /// Reads elle-cli's <c>--output json</c> analysis. Anything before the first line that opens the
    /// JSON object (a log line, a JVM warning) is skipped. Exit 255 is elle-cli's own crash code; the
    /// history may be fine, but it was not checked.
    /// </summary>
    public static ElleResult Parse(string stdout, int exitCode)
    {
        if (exitCode == 255)
            return ElleResult.Failed($"elle-cli crashed (exit 255): {FirstLine(stdout)}");

        int start = stdout.IndexOf("\n{", StringComparison.Ordinal);
        start = stdout.StartsWith('{') ? 0 : start < 0 ? -1 : start + 1;
        if (start < 0)
            return ElleResult.Failed($"elle-cli printed no JSON verdict (exit {exitCode}): {FirstLine(stdout)}");

        try
        {
            using JsonDocument doc = JsonDocument.Parse(stdout[start..]);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("valid?", out JsonElement valid))
                return ElleResult.Failed($"elle-cli JSON has no 'valid?' field (exit {exitCode})");

            string verdict = valid.ValueKind switch
            {
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.String => valid.GetString() ?? "unknown",
                _ => "unknown",
            };

            // Belt and braces: the exit code and the JSON must agree on an invalid history.
            if (exitCode == 1 && verdict == "true")
                return ElleResult.Failed("elle-cli exited 1 (invalid) but its JSON says valid; not trusting either");

            return new ElleResult(verdict, Strings(root, "anomaly-types"), Strings(root, "not"), null);
        }
        catch (JsonException e)
        {
            return ElleResult.Failed($"elle-cli JSON did not parse (exit {exitCode}): {e.Message}");
        }
    }

    private static IReadOnlyList<string> Strings(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
            return [];
        return array.EnumerateArray().Select(e => e.ToString()).ToList();
    }

    private static string FirstLine(string text)
    {
        string trimmed = text.Trim();
        int newline = trimmed.IndexOf('\n');
        string line = newline < 0 ? trimmed : trimmed[..newline];
        return line.Length <= 200 ? line : line[..200] + "…";
    }
}
