/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Diagnostics;
using System.Text;

namespace Caraxes.Core.Cluster;

/// <summary>Result of one external command: exit code plus captured output streams.</summary>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

/// <summary>
/// Thin wrapper over external commands (docker, docker compose, sh). Deliberately shell-free:
/// arguments are passed as a list, never concatenated into a shell line, so node names and
/// paths can never be re-tokenized. <c>streamOutput</c> tees the child's output to the console
/// for long operations (image builds) where silence reads as a hang.
/// </summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null,
        bool streamOutput = false,
        CancellationToken cancellationToken = default)
    {
        ProcessStartInfo psi = new()
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (string arg in arguments)
            psi.ArgumentList.Add(arg);

        if (environment is not null)
            foreach ((string key, string value) in environment)
                psi.Environment[key] = value;

        using Process process = new() { StartInfo = psi };

        StringBuilder stdout = new();
        StringBuilder stderr = new();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            stdout.AppendLine(e.Data);
            if (streamOutput)
                Console.WriteLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            stderr.AppendLine(e.Data);
            if (streamOutput)
                Console.Error.WriteLine(e.Data);
        };

        if (!process.Start())
            throw new InvalidOperationException($"failed to start '{fileName}'");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Runs and throws with the captured stderr when the command fails — for steps where
    /// a failure must abort the flow rather than be branched on.</summary>
    public static async Task<ProcessResult> RunCheckedAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null,
        bool streamOutput = false,
        CancellationToken cancellationToken = default)
    {
        ProcessResult result = await RunAsync(fileName, arguments, workingDirectory, environment, streamOutput, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
            throw new InvalidOperationException(
                $"'{fileName} {string.Join(' ', arguments)}' failed with exit code {result.ExitCode}:\n" +
                (string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr));

        return result;
    }
}
