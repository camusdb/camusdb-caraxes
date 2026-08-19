/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using NUnit.Framework;
using Caraxes.Core.LeaderBalance;

namespace Caraxes.Tests;

[TestFixture]
public sealed class LeaderSnapshotTests
{
    private static LeaderSnapshot Snapshot(int a, int b, int c) => new(
        new DateTime(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc),
        new Dictionary<string, int> { ["camus1"] = a, ["camus2"] = b, ["camus3"] = c },
        a + b + c, 9);

    private static readonly string[] AllNodes = ["camus1", "camus2", "camus3"];

    [Test]
    public void ImbalanceIsMaxMinusMin()
    {
        Assert.That(Snapshot(3, 3, 3).Imbalance(AllNodes), Is.EqualTo(0), "even spread → 0");
        Assert.That(Snapshot(5, 4, 0).Imbalance(AllNodes), Is.EqualTo(5), "concentrated → wide");
    }

    [Test]
    public void ImbalanceExcludesDownNodes()
    {
        // camus3 is down (killed): measuring only the survivors must not read camus3's 0 as the min.
        LeaderSnapshot afterKill = Snapshot(5, 4, 0);
        Assert.That(afterKill.Imbalance(new[] { "camus1", "camus2" }), Is.EqualTo(1), "5 vs 4 among survivors");
    }

    [Test]
    public void LeadersOnDefaultsToZero()
    {
        Assert.That(Snapshot(3, 3, 3).LeadersOn("camus9"), Is.EqualTo(0));
        Assert.That(Snapshot(3, 3, 3).LeadersOn("camus2"), Is.EqualTo(3));
    }

    [Test]
    public void FormatListsEveryNode()
    {
        Assert.That(Snapshot(3, 2, 4).Format(AllNodes),
            Is.EqualTo("camus1=3  camus2=2  camus3=4  (resolved 9/9)"));
    }
}
