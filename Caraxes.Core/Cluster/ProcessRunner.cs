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

    /// <summary>
    /// Runs a command and streams its standard output straight to <paramref name="destinationPath"/>
    /// instead of buffering it, returning the exit code and the bytes written.
    ///
    /// <para>Container logs from a ten-minute fault run reach tens of megabytes each. <see
    /// cref="RunAsync"/> accumulates every line into a <c>StringBuilder</c> and then materializes one
    /// string from it, so capturing a whole fleet that way would cost several times the log's size in
    /// peak memory for output nothing ever reads in process. Here the child's stdout is copied to the
    /// file as it arrives and never lands on the managed heap in one piece.</para>
    ///
    /// <para>Standard error is still buffered, because it carries the diagnostic when the command
    /// fails and is small in the success case. It is appended to the file under a marker line rather
    /// than interleaved: writing both streams into one handle concurrently would need locking, and
    /// the interleaving would not be faithful to real time anyway.</para>
    /// </summary>
    public static async Task<(int ExitCode, long BytesWritten)> RunToFileAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string destinationPath,
        string? workingDirectory = null,
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

        using Process process = new() { StartInfo = psi };

        if (!process.Start())
            throw new InvalidOperationException($"failed to start '{fileName}'");

        long written;
        string stderr;

        await using (FileStream destination = new(destinationPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            // Drain both pipes concurrently. Copying one to completion before reading the other
            // deadlocks as soon as the unread pipe fills, which a busy node's log does immediately.
            Task copy = process.StandardOutput.BaseStream.CopyToAsync(destination, cancellationToken);
            Task<string> readErrors = process.StandardError.ReadToEndAsync(cancellationToken);

            await Task.WhenAll(copy, readErrors).ConfigureAwait(false);

            stderr = await readErrors.ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            written = destination.Length;
        }

        if (!string.IsNullOrWhiteSpace(stderr))
            await File.AppendAllTextAsync(
                destinationPath,
                $"{Environment.NewLine}--- stderr of '{fileName} {string.Join(' ', arguments)}' ---{Environment.NewLine}{stderr}",
                cancellationToken).ConfigureAwait(false);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return (process.ExitCode, written);
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
