/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using Caraxes.Core.Workload;

namespace Caraxes.Tests;

/// <summary>
/// Covers reading the two evidence artifacts a cluster run now writes. The shapes here are copied
/// from files a real run produced, because the workload serializes them with different naming
/// conventions — camelCase for the cluster facts, PascalCase for the client resources — and a reader
/// that only ever saw one of them would silently return defaults for the other.
/// </summary>
[TestFixture]
public sealed class ClusterEvidenceArtifactsTests
{
    private string dir = "";

    [SetUp]
    public void SetUp()
    {
        dir = Path.Combine(Path.GetTempPath(), "caraxes-artifacts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    private void Write(string name, string content) => File.WriteAllText(Path.Combine(dir, name), content);

    [Test]
    public void ReadsClusterFactsInTheCamelCaseTheWorkloadEmits()
    {
        Write("cluster-facts.json", """
            {
              "capturedAtUtc": "2026-08-29T01:00:00.0000000Z",
              "nodes": [
                {
                  "node": "camus1",
                  "baseUrl": "http://camus1:5095",
                  "server": "0.11.1+abc",
                  "runtime": "10.0.10",
                  "components": [
                    { "name": "Kahuna.Core", "version": "1.4.14+def" },
                    { "name": "Kommander", "version": "1.4.2+ghi" }
                  ],
                  "ready": true,
                  "variables": { "kahuna.wal_sync_writes": "" },
                  "errors": []
                }
              ],
              "ranges": [],
              "errors": [],
              "durabilityFingerprint": "sha256:9aec68bc71b3828b"
            }
            """);

        ClusterFactsSummary? facts = WorkloadArtifacts.ReadClusterFacts(dir);

        Assert.That(facts, Is.Not.Null);
        Assert.That(facts!.DurabilityFingerprint, Is.EqualTo("sha256:9aec68bc71b3828b"));
        Assert.That(facts.Nodes, Has.Count.EqualTo(1));
        Assert.That(facts.Nodes[0].Ready, Is.True);
        Assert.That(facts.Nodes[0].Components.Select(c => c.Name), Does.Contain("Kommander"));
    }

    [Test]
    public void KeepsANodeThatCouldNotAnswerDistinctFromOneThatSaidNo()
    {
        // A null `ready` means the probe never got an answer; false means the node answered that it
        // was not serving. Both fail the check, and the note must be able to tell them apart.
        Write("cluster-facts.json", """
            {
              "capturedAtUtc": "2026-08-29T01:00:00Z",
              "nodes": [
                { "node": "camus1", "baseUrl": "u", "components": [], "variables": {},
                  "errors": ["/v1/version: HttpRequestException: refused"] },
                { "node": "camus2", "baseUrl": "u", "components": [], "ready": false, "variables": {}, "errors": [] }
              ],
              "ranges": [], "errors": [], "durabilityFingerprint": "sha256:x"
            }
            """);

        ClusterFactsSummary facts = WorkloadArtifacts.ReadClusterFacts(dir)!;

        Assert.That(facts.Nodes[0].Ready, Is.Null);
        Assert.That(facts.Nodes[0].Errors, Has.Count.EqualTo(1));
        Assert.That(facts.Nodes[1].Ready, Is.False);
    }

    [Test]
    public void ReadsClientResourcesInThePascalCaseTheWorkloadEmits()
    {
        Write("client-resources.json", """
            {
              "MeasuredSeconds": 4.9996,
              "ProcessorCount": 10,
              "CpuUtilization": 0.179,
              "AllocatedMbPerSecond": 138.9,
              "GcPauseFraction": 0.013,
              "PeakThreadPoolQueue": 3,
              "RequiredInFlight": 61.66,
              "Warnings": [],
              "HeadroomAvailable": true
            }
            """);

        ClientResourcesSummary? client = WorkloadArtifacts.ReadClientResources(dir);

        Assert.That(client, Is.Not.Null);
        Assert.That(client!.ProcessorCount, Is.EqualTo(10));
        Assert.That(client.CpuUtilization, Is.EqualTo(0.179).Within(0.0001));
        Assert.That(client.PeakThreadPoolQueue, Is.EqualTo(3));
        Assert.That(client.HeadroomAvailable, Is.True);
    }

    [Test]
    public void TreatsAWarnedGeneratorAsHavingNoHeadroom()
    {
        Write("client-resources.json", """
            {
              "ProcessorCount": 4,
              "CpuUtilization": 0.93,
              "Warnings": ["the load generator used 93% of its 4 core(s)"]
            }
            """);

        ClientResourcesSummary client = WorkloadArtifacts.ReadClientResources(dir)!;

        Assert.That(client.HeadroomAvailable, Is.False);
        Assert.That(client.Warnings, Has.Count.EqualTo(1));
    }

    [Test]
    public void ReturnsNullWhenAnArtifactWasNeverWritten()
    {
        // "The run did not capture this" and "it captured nothing" are different findings.
        Assert.That(WorkloadArtifacts.ReadClusterFacts(dir), Is.Null);
        Assert.That(WorkloadArtifacts.ReadClientResources(dir), Is.Null);
    }

    [Test]
    public void ReturnsNullForAMalformedArtifact()
    {
        Write("cluster-facts.json", "{ not json");
        Write("client-resources.json", "{ not json");

        Assert.That(WorkloadArtifacts.ReadClusterFacts(dir), Is.Null);
        Assert.That(WorkloadArtifacts.ReadClientResources(dir), Is.Null);
    }
}
