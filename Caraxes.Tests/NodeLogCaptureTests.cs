/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using NUnit.Framework;
using Caraxes.Core.Cluster;
using Caraxes.Core.Scenario;

namespace Caraxes.Tests;

/// <summary>
/// Node log capture exists because a container's log is destroyed with its container, and some
/// diagnostic witnesses are log lines rather than scraped counters. These tests pin the two things
/// that would silently undo it: the default flipping to off, and a tail sneaking in as the default.
/// </summary>
[TestFixture]
public sealed class NodeLogCaptureTests
{
    private const string Minimal = """
        name: smoke
        cluster:
          name: smoke-c
        workload:
          rows: 5000
        """;

    [Test]
    public void CaptureIsOnByDefaultAndKeepsTheWholeLog()
    {
        ScenarioSpec spec = ScenarioSpecReader.Read(Minimal);

        Assert.That(spec.CaptureNodeLogs, Is.True,
            "capture must default on: the run that most needs its logs is the one that already failed");
        Assert.That(spec.NodeLogTail, Is.Zero,
            "0 means the whole log; a default tail would discard the early lines that explain a late failure");
    }

    [Test]
    public void CaptureCanBeDisabledAndTailBounded()
    {
        ScenarioSpec spec = ScenarioSpecReader.Read(Minimal + """

            capture_node_logs: false
            node_log_tail: 500
            """);

        Assert.That(spec.CaptureNodeLogs, Is.False);
        Assert.That(spec.NodeLogTail, Is.EqualTo(500));
    }

    [Test]
    public void NegativeTailIsRejected()
    {
        // Read validates, so a bad value is rejected at parse time rather than reaching a run.
        ScenarioException e = Assert.Throws<ScenarioException>(() => ScenarioSpecReader.Read(Minimal + """

            node_log_tail: -1
            """))!;

        Assert.That(e.Message, Does.Contain("node_log_tail"));
    }

    [Test]
    public async Task RunToFileWritesStdOutToTheDestination()
    {
        string path = Path.Combine(Path.GetTempPath(), $"caraxes-log-{Guid.NewGuid():N}.txt");

        try
        {
            (int exitCode, long bytes) = await ProcessRunner.RunToFileAsync(
                "sh", ["-c", "printf 'alpha\\nbeta\\n'"], path);

            Assert.That(exitCode, Is.Zero);
            Assert.That(bytes, Is.GreaterThan(0));
            Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo("alpha\nbeta\n"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task RunToFileReportsFailureAndKeepsStdErr()
    {
        string path = Path.Combine(Path.GetTempPath(), $"caraxes-log-{Guid.NewGuid():N}.txt");

        try
        {
            (int exitCode, _) = await ProcessRunner.RunToFileAsync(
                "sh", ["-c", "printf 'partial\\n'; printf 'boom\\n' >&2; exit 3"], path);

            Assert.That(exitCode, Is.EqualTo(3), "a failing capture must surface its exit code, not pass silently");

            string captured = await File.ReadAllTextAsync(path);
            Assert.That(captured, Does.Contain("partial"), "whatever the command did emit must be kept");
            Assert.That(captured, Does.Contain("boom"), "stderr carries the reason the capture failed");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task RunToFileHandlesOutputLargerThanAPipeBuffer()
    {
        // Reading one stream to completion before draining the other deadlocks once the unread pipe
        // fills. A busy node's log passes that threshold immediately, so the large case is the real
        // one — a small fixture would pass against the broken implementation.
        string path = Path.Combine(Path.GetTempPath(), $"caraxes-log-{Guid.NewGuid():N}.txt");

        try
        {
            (int exitCode, long bytes) = await ProcessRunner.RunToFileAsync(
                "sh", ["-c", "for i in $(seq 1 200000); do echo \"line $i padding padding padding\"; done"], path);

            Assert.That(exitCode, Is.Zero);
            Assert.That(bytes, Is.GreaterThan(1_000_000), "the fixture must exceed a pipe buffer to be meaningful");

            string[] lines = await File.ReadAllLinesAsync(path);
            Assert.That(lines, Has.Length.EqualTo(200000), "no output may be dropped");
            Assert.That(lines[^1], Does.StartWith("line 200000"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
