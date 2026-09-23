/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using NUnit.Framework;
using Caraxes.Core.Cluster;

namespace Caraxes.Tests;

/// <summary>
/// The <c>env:</c> passthrough puts arbitrary environment variables on every node container.
///
/// It exists because a node's resident set is mostly native memory that no application setting
/// reaches. CamusDB's Phase 5 fault soaks lost a node to container OOM three times with ~3 GB of
/// native memory per node, while the managed heap, RocksDB's block cache, its memtables, its table
/// readers and Kahuna's durable-2PC stores together accounted for under a third of it. Reading
/// <c>/proc/1/smaps</c> in a live node found 116 anonymous mappings of 60-70 MB holding 1,227 MB
/// resident — glibc's secondary malloc arenas. The lever is <c>MALLOC_ARENA_MAX</c>, which has no
/// representation in CamusDB, Kahuna or Kommander configuration, so the harness needs a way to set
/// a raw variable.
/// </summary>
[TestFixture]
public sealed class ContainerEnvTests
{
    private static ClusterPlan Plan(params string[] lines) =>
        ClusterPlan.FromSpec(ClusterSpecReader.Read(string.Join('\n', lines)));

    [Test]
    public void EnvEntriesReachEveryNodeContainer()
    {
        ClusterPlan plan = Plan(
            "name: envtest",
            "nodes: 3",
            "env:",
            "  MALLOC_ARENA_MAX: \"2\"");

        string yml = ComposeGenerator.Generate(plan, "./config");

        Assert.That(plan.Spec.Env["MALLOC_ARENA_MAX"], Is.EqualTo("2"));

        // One per node, not one for the file.
        Assert.That(
            yml.Split("MALLOC_ARENA_MAX").Length - 1,
            Is.EqualTo(3),
            "every node container should carry the variable");
    }

    [Test]
    public void AnEmptyEnvChangesNothing()
    {
        string without = ComposeGenerator.Generate(Plan("name: envtest", "nodes: 1"), "./config");
        string with = ComposeGenerator.Generate(Plan("name: envtest", "nodes: 1", "env: {}"), "./config");

        Assert.That(with, Is.EqualTo(without));
    }

    /// <summary>
    /// The passthrough is applied after the variables Caraxes derives, so an experiment can override
    /// one deliberately. Asserted on the GC budget because that is the one an operator is most likely
    /// to want to move, and because a silent failure to override would be invisible in a run's output.
    /// </summary>
    [Test]
    public void AnExplicitEnvOverridesADerivedVariable()
    {
        ClusterPlan plan = Plan(
            "name: envtest",
            "nodes: 1",
            "memory_limit_mb: 4096",
            "env:",
            "  DOTNET_GCHeapHardLimitPercent: \"1E\"");

        string yml = ComposeGenerator.Generate(plan, "./config");

        Assert.That(yml, Does.Contain("DOTNET_GCHeapHardLimitPercent: 1E"));
        Assert.That(yml, Does.Not.Contain("DOTNET_GCHeapHardLimitPercent: 3C"));
    }

    [Test]
    public void AnUnknownClusterKeyIsStillRejected()
    {
        Assert.Throws<ClusterSpecException>(() =>
            ClusterSpecReader.Read(string.Join('\n', "name: envtest", "nodes: 1", "envs:", "  X: \"1\"")));
    }
}
