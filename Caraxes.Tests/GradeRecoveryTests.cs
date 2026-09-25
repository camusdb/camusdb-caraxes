/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using NUnit.Framework;
using Caraxes.Core.Scenario;

namespace Caraxes.Tests;

/// <summary>
/// <c>grade_recovery: false</c> is what lets CI gate on correctness alone, so what it turns off, and that it keeps the
/// evidence, are the contract.
/// </summary>
[TestFixture]
public sealed class GradeRecoveryTests
{
    [Test]
    public void GradedByDefaultAndSwitchableByScenario()
    {
        Assert.That(new ChecksSpec().GradeRecovery, Is.True);

        ScenarioSpec scenario = ScenarioSpecReader.Read("""
            name: recovery-off
            cluster:
              name: t
              nodes: 3
            checks:
              grade_recovery: false
            """);

        Assert.That(scenario.Checks.GradeRecovery, Is.False);
    }

    [Test]
    public void AGradedFindingFailsTheRun()
    {
        List<string> notes = [];

        bool passed = ScenarioRunner.GradeRecoveryFinding(new ChecksSpec(), notes, "fault kill/camus1 never recovered");

        Assert.That(passed, Is.False);
        Assert.That(notes, Is.EqualTo(new[] { "  CHECK FAILED: fault kill/camus1 never recovered" }));
    }

    [Test]
    public void AnUngradedFindingIsReportedButDoesNotFailTheRun()
    {
        List<string> notes = [];

        bool passed = ScenarioRunner.GradeRecoveryFinding(
            new ChecksSpec { GradeRecovery = false }, notes, "fault kill/camus1 never recovered");

        Assert.That(passed, Is.True);
        Assert.That(notes, Has.Count.EqualTo(1));
        Assert.That(notes[0], Does.StartWith("  NOT GRADED: fault kill/camus1 never recovered"));
        Assert.That(notes[0], Does.Not.Contain("CHECK FAILED"), "an ungraded finding must not read as a failure");
    }
}
