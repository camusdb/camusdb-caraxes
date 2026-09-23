/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using NUnit.Framework;
using Caraxes.Core.Cluster;
using Caraxes.Core.Scenario;
using Caraxes.Core.Verdict;
using Caraxes.Core.Workload;

namespace Caraxes.Tests;

/// <summary>
/// The Elle step decides an append run's isolation verdict, so what it passes to elle-cli and how it
/// reads the answer are both the contract. Neither needs Docker to test.
/// </summary>
[TestFixture]
public sealed class ElleCheckTests
{
    private const string AppendScenario = """
        name: append-test
        cluster:
          name: t
          nodes: 3
          isolation: serializable
        workload:
          kind: append
          append_keys: 5
          append_max_txn_length: 6
        """;

    private static string? ValueAfter(IReadOnlyList<string> args, string flag)
    {
        int at = args.ToList().IndexOf(flag);
        return at < 0 || at + 1 >= args.Count ? null : args[at + 1];
    }

    [Test]
    public void AppendKindPassesOnlyTheAppendSettingsItSets()
    {
        ScenarioSpec scenario = ScenarioSpecReader.Read(AppendScenario);
        IReadOnlyList<string> args = WorkloadRunner.BuildRunArgs(ClusterPlan.FromSpec(scenario.Cluster), scenario, "run").Args;

        Assert.That(ValueAfter(args, "--workload"), Is.EqualTo("append"));
        Assert.That(ValueAfter(args, "--append-keys"), Is.EqualTo("5"));
        Assert.That(ValueAfter(args, "--append-max-txn-length"), Is.EqualTo("6"));
        Assert.That(args, Does.Not.Contain("--append-max-writes-per-key"), "0 must leave the workload default");
    }

    [Test]
    public void AppendSettingsOnAnotherKindAreRejected()
    {
        Assert.Throws<ScenarioException>(() => ScenarioSpecReader.Read("""
            name: bank-test
            cluster:
              name: t
              nodes: 3
            workload:
              kind: bank
              append_keys: 5
            """));
    }

    [TestCase("serializable", "", "serializable")]
    [TestCase("read_committed", "", "read-committed")]
    [TestCase("serializable", "strict-serializable, snapshot-isolation", "strict-serializable,snapshot-isolation")]
    public void ConsistencyModelFollowsIsolationUnlessNamed(string isolation, string configured, string expected)
    {
        ElleSpec spec = new() { ConsistencyModels = configured };
        Assert.That(spec.EffectiveConsistencyModels(isolation), Is.EqualTo(expected));
    }

    [Test]
    public void DockerArgsRunListAppendAsJsonOnTheHistory()
    {
        ElleSpec spec = new() { Anomalies = "G1a, G1b", CycleSearchTimeoutMs = 5000, HeapMb = 2048 };
        IReadOnlyList<string> args = ElleCheck.BuildDockerArgs(spec, "serializable", "/tmp/run", "/tmp/elle.jar", "c-elle");

        Assert.That(args.Take(4), Is.EqualTo(new[] { "run", "--rm", "--name", "c-elle" }));
        Assert.That(args, Does.Contain("/tmp/run:/work"));
        Assert.That(args, Does.Contain("/tmp/elle.jar:/elle/elle-cli.jar:ro"));
        Assert.That(ValueAfter(args, "--entrypoint"), Is.EqualTo("java"));
        Assert.That(args, Does.Contain("-Xmx2048m"));
        Assert.That(ValueAfter(args, "--model"), Is.EqualTo("list-append"));
        Assert.That(ValueAfter(args, "--output"), Is.EqualTo("json"));
        Assert.That(ValueAfter(args, "--consistency-models"), Is.EqualTo("serializable"));
        Assert.That(ValueAfter(args, "--anomalies"), Is.EqualTo("G1a,G1b"));
        Assert.That(ValueAfter(args, "--cycle-search-timeout"), Is.EqualTo("5000"));
        Assert.That(ValueAfter(args, "--max-plot-bytes"), Is.EqualTo("0"), "plots are off unless asked for");
        Assert.That(args[^1], Is.EqualTo("/work/history.edn"));

        // The JVM flag must come before -jar, or java hands it to elle-cli as an argument.
        Assert.That(args.ToList().IndexOf("-Xmx2048m"), Is.LessThan(args.ToList().IndexOf("-jar")));
    }

    [Test]
    public void PlotsOnLeavesElleItsDefaultPlotLimit()
    {
        IReadOnlyList<string> args = ElleCheck.BuildDockerArgs(
            new ElleSpec { Plots = true }, "serializable", "/tmp/run", "/tmp/elle.jar", "c-elle");

        Assert.That(args, Does.Not.Contain("--max-plot-bytes"));
    }

    [Test]
    public void DefaultImageIsAJdk()
    {
        // The JRE image lacks jdk.random, and elle-cli cannot load without it.
        Assert.That(new ElleSpec().Image, Does.EndWith("-jdk"));
    }

    [Test]
    public void ValidHistoryPasses()
    {
        ElleResult result = ElleCheck.Parse("""{"valid?": true, "anomaly-types": [], "not": []}""", 0);

        Assert.That(result.Passed, Is.True);
        Assert.That(result.Error, Is.Null);
    }

    [Test]
    public void InvalidHistoryFailsAndNamesTheAnomalies()
    {
        ElleResult result = ElleCheck.Parse("""
            WARNING: something from the JVM
            {"valid?": false,
             "anomaly-types": ["G-single", "G2-item"],
             "not": ["serializable", "snapshot-isolation"],
             "anomalies": {}}
            """, 1);

        Assert.That(result.Passed, Is.False);
        Assert.That(result.Valid, Is.EqualTo("false"));
        Assert.That(result.AnomalyTypes, Is.EqualTo(new[] { "G-single", "G2-item" }));
        Assert.That(result.NotModels, Is.EqualTo(new[] { "serializable", "snapshot-isolation" }));
    }

    [Test]
    public void UnknownFailsEvenThoughElleCliExitsZero()
    {
        ElleResult result = ElleCheck.Parse("""{"valid?": "unknown", "anomaly-types": ["cycle-search-timeout"]}""", 0);

        Assert.That(result.Passed, Is.False);
        Assert.That(result.Valid, Is.EqualTo("unknown"));
    }

    [TestCase("", 0)]
    [TestCase("java.lang.OutOfMemoryError", 255)]
    [TestCase("{\"valid?\": tru", 0)]
    [TestCase("{\"anomaly-types\": []}", 0)]
    [TestCase("{\"valid?\": true}", 1)]
    public void AnythingElseIsNoVerdict(string stdout, int exitCode)
    {
        ElleResult result = ElleCheck.Parse(stdout, exitCode);

        Assert.That(result.Passed, Is.False);
        Assert.That(result.Error, Is.Not.Null);
    }

    [Test]
    public async Task MissingHistoryIsNoVerdictWithoutDocker()
    {
        string dir = Path.Combine(Path.GetTempPath(), "caraxes-elle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            ElleResult result = await ElleCheck.RunAsync(new ElleSpec(), "serializable", dir, "unused", CancellationToken.None);
            Assert.That(result.Passed, Is.False);
            Assert.That(result.Error, Does.Contain("history.edn"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
