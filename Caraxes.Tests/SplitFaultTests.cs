/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using NUnit.Framework;
using Caraxes.Core.Cluster;
using Caraxes.Core.Nemesis;
using Caraxes.Core.Scenario;

namespace Caraxes.Tests;

/// <summary>
/// The <c>split</c> fault's scenario surface: what an event parses to, what the reader refuses, and how an
/// outcome reads in the timeline and the verdict. The fault itself talks to a live node, so its run is the
/// scenario's business (<c>scenarios/bank-split-kill-window.yml</c>); this pins the part that fails at
/// parse time instead of after an image build and a cluster boot.
/// </summary>
[TestFixture]
public sealed class SplitFaultTests
{
    private const string Base = """
        name: s
        cluster:
          name: sc
          key_range_sharding: true
        workload:
          kind: bank
          rows: 100

        """;

    [Test]
    public void SplitEvent_ParsesItsFields_AndDefaultsToTheBankTableRowSpace()
    {
        ScenarioSpec spec = ScenarioSpecReader.Read(Base + """
            nemesis:
              events:
                - { at: 76s, fault: split, target: camus3, duration: 10s }
                - { at: 90s, fault: split, target: camus2, duration: 5s, split_table: t2, split_index: t2_owner, split_timeout_seconds: 12, split_retries: 24 }
            """);

        NemesisEvent first = spec.Nemesis!.Events[0];
        Assert.That(first.SplitTable, Is.EqualTo("workload_accounts"));
        Assert.That(first.SplitIndex, Is.Null);
        Assert.That(first.SplitTimeoutSeconds, Is.EqualTo(60));
        Assert.That(first.SplitRetries, Is.EqualTo(60));

        NemesisEvent second = spec.Nemesis.Events[1];
        Assert.That(second.SplitTable, Is.EqualTo("t2"));
        Assert.That(second.SplitIndex, Is.EqualTo("t2_owner"));
        Assert.That(second.SplitTimeoutSeconds, Is.EqualTo(12));
        Assert.That(second.SplitRetries, Is.EqualTo(24));

        IFault fault = FaultFactory.Create(first, new NemesisContext(new HttpProbes()));
        Assert.That(fault, Is.InstanceOf<SplitFault>());
        Assert.That(fault.Kind, Is.EqualTo("split"));
        Assert.That(fault.Healable, Is.True, "the heal is a no-op that closes the window");
        Assert.That(FaultFactory.KnownKinds, Does.Contain("split"));
    }

    [Test]
    public void SplitKeys_AreRefusedOnOtherFaults_AndBoundsAreChecked()
    {
        NemesisException onKill = Assert.Throws<NemesisException>(() => ScenarioSpecReader.Read(Base + """
            nemesis:
              events:
                - { at: 10s, fault: kill, target: camus1, duration: 5s, split_index: x }
            """))!;
        Assert.That(onKill.Message, Does.Contain("split_index"));

        NemesisException zeroTimeout = Assert.Throws<NemesisException>(() => ScenarioSpecReader.Read(Base + """
            nemesis:
              events:
                - { at: 10s, fault: split, target: camus1, duration: 5s, split_timeout_seconds: 0 }
            """))!;
        Assert.That(zeroTimeout.Message, Does.Contain("split_timeout_seconds"));

        NemesisException noTable = Assert.Throws<NemesisException>(() => ScenarioSpecReader.Read(Base + """
            nemesis:
              events:
                - { at: 10s, fault: split, target: camus1, duration: 5s, split_table: "" }
            """))!;
        Assert.That(noTable.Message, Does.Contain("split_table"));
    }

    [Test]
    public void SplitOutcome_DescribesACommitAndARefusal()
    {
        SplitOutcome committed = new("workload_accounts", null, "camus2", "Succeeded", "1:1|r", "1:1|r/6af0", 7, 3, 4.2, true, null);
        Assert.That(committed.Describe(), Does.Contain("split rows of workload_accounts (1:1|r) at 1:1|r/6af0 on camus2"));
        Assert.That(committed.Describe(), Does.Contain("upper half on P7"));
        Assert.That(committed.Describe(), Does.Contain("after 3 asks"));

        SplitOutcome refused = new("workload_accounts", "workload_accounts_owner", "camus3", "QuiesceFailed", "1:1|i:x", null, 0, 24, 12.0, false, "the leader did not confirm the quiesce");
        Assert.That(refused.Describe(), Does.StartWith("split of index workload_accounts_owner of workload_accounts (1:1|i:x) did NOT commit: QuiesceFailed on camus3"));
        Assert.That(refused.Describe(), Does.Contain("the leader did not confirm the quiesce"));
    }

    [Test]
    public void KillWindowScenario_SchedulesTheSplitInsideTheKill()
    {
        string file = Path.Combine(FindScenariosDirectory(), "bank-split-kill-window.yml");
        ScenarioSpec spec = ScenarioSpecReader.Read(File.ReadAllText(file));

        NemesisEvent kill = spec.Nemesis!.Events.Single(e => e.Fault == "kill");
        NemesisEvent split = spec.Nemesis.Events.Single(e => e.Fault == "split");

        TimeSpan killAt = DurationParser.Parse(kill.At);
        TimeSpan killEnd = killAt + DurationParser.Parse(kill.Duration);
        TimeSpan splitAt = DurationParser.Parse(split.At);
        TimeSpan splitEnd = splitAt + TimeSpan.FromSeconds(split.SplitTimeoutSeconds);

        Assert.That(kill.Target, Is.Not.EqualTo(split.Target), "the split is asked of a node that is up");
        Assert.That(splitAt, Is.GreaterThan(killAt), "the split starts after the kill");
        Assert.That(splitEnd, Is.LessThan(killEnd), "every ask of the split ends before the killed node returns");
        Assert.That(spec.Checks.RequireSplit, Is.True);
    }

    private static string FindScenariosDirectory()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, "scenarios");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "bank-split.yml")))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException("scenarios directory not found above " + AppContext.BaseDirectory);
    }
}
