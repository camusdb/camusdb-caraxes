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
/// The per-node container memory limit exists because CamusDB runs with Server GC: without a
/// cgroup limit each node sizes its heap against the whole Docker VM, and three nodes grew
/// ~2-3 GiB of mostly-empty committed heap each until the VM OOM-killed one (the run-C/E soak
/// losses). A <c>mem_limit</c> alone is not enough: the runtime's default 75% heap self-cap
/// plus ~350-400 MiB of native memory (RocksDB) meets the limit, and a loaded node still dies
/// (soak run G). So the generator pairs every <c>mem_limit</c> with an explicit 60% heap
/// budget (<c>DOTNET_GCHeapHardLimitPercent=3C</c> — GC env vars parse as hexadecimal, and
/// the 0x prefix is omitted so YAML keeps the value a string), which leaves the remaining
/// ~40% for native memory.
/// </summary>
[TestFixture]
public sealed class MemoryLimitTests
{
    [Test]
    public void MemoryLimitFlowsIntoCompose()
    {
        ClusterPlan plan = ClusterPlan.FromSpec(ClusterSpecReader.Read(string.Join('\n',
            "name: mem",
            "nodes: 3",
            "memory_limit_mb: 1536")));

        string yml = ComposeGenerator.Generate(plan, "./config");

        Assert.That(plan.Spec.MemoryLimitMb, Is.EqualTo(1536));
        Assert.That(yml, Does.Contain("mem_limit: 1536m"));
        Assert.That(yml, Does.Contain("DOTNET_GCHeapHardLimitPercent: 3C"));
    }

    [Test]
    public void DefaultKeepsNoLimit()
    {
        ClusterPlan plan = ClusterPlan.FromSpec(ClusterSpecReader.Read("name: mem"));

        string yml = ComposeGenerator.Generate(plan, "./config");

        Assert.That(plan.Spec.MemoryLimitMb, Is.EqualTo(0));
        Assert.That(yml, Does.Not.Contain("mem_limit"));
        Assert.That(yml, Does.Not.Contain("DOTNET_GCHeapHardLimitPercent"));
    }

    [Test]
    public void RejectsAStarvationLimit()
    {
        ClusterSpecException ex = Assert.Throws<ClusterSpecException>(() =>
            ClusterSpecReader.Read("name: mem\nmemory_limit_mb: 256"))!;

        Assert.That(ex.Message, Does.Contain("memory_limit_mb"));
    }
}
