/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using NUnit.Framework;
using Caraxes.Core.Scenario;
using Caraxes.Core.Verdict;

namespace Caraxes.Tests;

/// <summary>
/// The divergent-apply check turns one Kahuna error line into a failed scenario, so what it matches, what it counts and
/// what it does without logs are the contract.
/// </summary>
[TestFixture]
public sealed class DivergentApplyCheckTests
{
    private const string Line1 =
        "2026-09-23 20:37:35 fail: Kahuna.IKahuna[0] Same-revision divergent apply for key 1:2|r/6ab4388f8f11cb0001a71646 " +
        "at revision 15: log entry 13889 (transaction HLC(2:1790195855336:16)) overwrites a different value already recorded at this revision";

    private const string Line2 =
        "2026-09-23 20:37:35 fail: Kahuna.IKahuna[0] Same-revision divergent apply for key 1:2|r/6ab4388f8f11cb0001a71647 " +
        "at revision 7: log entry 13907 (transaction HLC(2:1790195855531:4)) overwrites a different value already recorded at this revision";

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "caraxes-divergent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Test]
    public void CountsLinesKeysAndNodes()
    {
        string dir = TempDir();
        try
        {
            File.WriteAllLines(Path.Combine(dir, "node-log-camus1.txt"), ["startup", Line1, Line2, "other warning"]);
            File.WriteAllLines(Path.Combine(dir, "node-log-camus2.txt"), [Line1, Line2]);
            File.WriteAllLines(Path.Combine(dir, "node-log-camus3.txt"), ["nothing to see"]);

            DivergentApplyResult result = DivergentApplyCheck.Scan(dir);

            Assert.That(result.LogsFound, Is.True);
            Assert.That(result.Lines, Is.EqualTo(4));
            Assert.That(result.Keys, Is.EqualTo(new[] { "1:2|r/6ab4388f8f11cb0001a71646", "1:2|r/6ab4388f8f11cb0001a71647" }));
            Assert.That(result.LinesByNode["camus1"], Is.EqualTo(2));
            Assert.That(result.LinesByNode["camus2"], Is.EqualTo(2));
            Assert.That(result.LinesByNode.ContainsKey("camus3"), Is.False);
            Assert.That(result.Samples[0], Does.StartWith("camus1: "));

            string matches = Path.Combine(dir, "divergent-applies.txt");
            DivergentApplyCheck.WriteMatches(dir, matches);
            Assert.That(File.ReadAllLines(matches), Has.Length.EqualTo(4));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void CleanLogsFindNothing()
    {
        string dir = TempDir();
        try
        {
            File.WriteAllLines(Path.Combine(dir, "node-log-camus1.txt"), ["startup", "Same-revision replay ignored"]);

            DivergentApplyResult result = DivergentApplyCheck.Scan(dir);

            Assert.That(result.LogsFound, Is.True);
            Assert.That(result.Lines, Is.Zero);
            Assert.That(result.Keys, Is.Empty);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void NoLogsMeansNotChecked()
    {
        string dir = TempDir();
        try
        {
            DivergentApplyResult result = DivergentApplyCheck.Scan(dir);
            Assert.That(result.LogsFound, Is.False, "without logs the check must say it did not run, not report a pass");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void RequiredByDefaultAndSwitchableByScenario()
    {
        Assert.That(new ChecksSpec().RequireNoDivergentApply, Is.True);

        ScenarioSpec scenario = ScenarioSpecReader.Read("""
            name: divergent-off
            cluster:
              name: t
              nodes: 3
            checks:
              require_no_divergent_apply: false
            """);

        Assert.That(scenario.Checks.RequireNoDivergentApply, Is.False);
    }
}
