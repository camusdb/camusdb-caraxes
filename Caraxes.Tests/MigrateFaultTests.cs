/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using NUnit.Framework;
using Caraxes.Core.Nemesis;
using Caraxes.Core.Scenario;

namespace Caraxes.Tests;

/// <summary>The <c>migrate</c> fault in a scenario file: what it accepts, and what it refuses.</summary>
[TestFixture]
public sealed class MigrateFaultTests
{
    private static ScenarioSpec Read(string events) => ScenarioSpecReader.Read($"""
        name: s
        cluster:
          name: c
        nemesis:
          events:
        {events}
        """);

    [Test]
    public void AMigrateEventIsBuiltIntoItsFault()
    {
        ScenarioSpec scenario = Read("    - { at: 5s, fault: migrate, target: camus1, migration: 3, rollback: true, sql_timeout_seconds: 60 }");
        NemesisEvent e = scenario.Nemesis!.Events[0];

        Assert.That(e.Migration, Is.EqualTo(3));
        Assert.That(e.Rollback, Is.True);
        Assert.That(FaultFactory.Create(e, new NemesisContext(new Caraxes.Core.Cluster.HttpProbes())).Kind, Is.EqualTo("migrate"));
    }

    [TestCase("    - { at: 5s, fault: migrate, target: camus1 }", "give the migration a number")]
    [TestCase("    - { at: 5s, fault: kill, target: camus1, migration: 1 }", "only meaningful for the migrate fault")]
    [TestCase("    - { at: 5s, fault: kill, target: camus1, rollback: true }", "only meaningful for the migrate fault")]
    public void AnEventThatMakesNoSenseIsRefused(string events, string reason)
    {
        Exception? refused = Assert.Catch(() => Read(events));
        Assert.That(refused!.Message, Does.Contain(reason));
    }

    [Test]
    public void TwoEventsWithOneNumberAreRefused()
    {
        Exception? refused = Assert.Catch(() => Read("""
                - { at: 5s, fault: migrate, target: camus1, migration: 1 }
                - { at: 9s, fault: migrate, target: camus2, migration: 1 }
            """));
        Assert.That(refused!.Message, Does.Contain("scheduled more than once"));
    }

    [Test]
    public void TheObjectsOfAMigrationCarryItsNumber()
    {
        Assert.That(Migration.Statements(4), Has.All.Contains("4"));
        Assert.That(Migration.Statements(4)[0], Does.Contain(Migration.WorkloadTable));
    }
}
