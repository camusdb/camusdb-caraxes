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
/// outcome reads in the timeline and the verdict. The split itself needs a live node, so its run is the
/// scenario's business (<c>scenarios/bank-split-kill-window.yml</c>); this pins the part that fails at
/// parse time instead of after an image build and a cluster boot.
///
/// <para>The order of the asks is pinned here too, with the call to the node replaced by a script of
/// answers: which node is asked after a <c>NotLeader</c>, after an error of the leader and after a node
/// that does not answer, and what a failed outcome says. A wrong order costs the whole budget of a
/// split, which a live run shows only as a split that did not commit.</para>
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

    // ---- The order of the asks, against a script of answers ----

    private static ClusterPlan ThreeNodes() => ClusterPlan.FromSpec(ClusterSpecReader.Read("name: t\nnodes: 3"));

    private static SplitRangeResult NotLeader(NodePlan? hint) => new()
    {
        Status = "NotLeader",
        KeySpace = "1:1|r",
        DescriptorsBefore = 1,
        LeaderHint = hint is null ? null : LeaderTransferFault.RaftEndpoint(hint),
        Reason = "This node does not lead the partition that owns the range map, so no split was attempted. Retry against the leader.",
    };

    private static SplitRangeResult Committed() => new()
    {
        Success = true, Status = "Succeeded", KeySpace = "1:1|r", SplitKey = "1:1|r/6af0", DescriptorsBefore = 1, NewPartitionId = 8,
    };

    private const string ScanDidNotSettle =
        "Range scan page over '1:1|r' did not settle within 5000 ms (last response: MustRetry); a key in the page may hold an unresolved write intent.";

    /// <summary>
    /// Runs one split event against <paramref name="script"/>, which answers each ask in turn, and returns
    /// the nodes asked, the pauses taken and the recorded outcome. A script entry returns a result or
    /// throws what the call to the node throws.
    /// </summary>
    private static async Task<(List<string> Asked, int Pauses, SplitOutcome Outcome, bool Threw)> RunAsync(
        ClusterPlan plan, string target, int retries, params Func<NodePlan, SplitRangeResult>[] script)
    {
        List<string> asked = [];
        int pauses = 0;
        NemesisContext context = new(new HttpProbes());

        SplitFault fault = new(
            context, "workload_accounts", null, TimeSpan.FromSeconds(30), retries,
            ask: (node, _) =>
            {
                asked.Add(node.Name);
                Assert.That(asked.Count, Is.LessThanOrEqualTo(script.Length), $"an ask beyond the script, of {node.Name}");
                return Task.FromResult(script[asked.Count - 1](node));
            },
            pause: _ =>
            {
                pauses++;
                return Task.CompletedTask;
            });

        bool threw = false;

        try
        {
            await fault.InjectAsync(plan.Nodes.Single(n => n.Name == target), plan, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        return (asked, pauses, context.SplitOutcomes.Single(), threw);
    }

    [Test]
    public async Task AnErrorOfTheNamedLeader_IsAskedOfTheLeaderAgain()
    {
        ClusterPlan plan = ThreeNodes();
        NodePlan camus2 = plan.Nodes.Single(n => n.Name == "camus2");

        // The night of 2026-11-23: camus3 names camus2, whose scan for the split key fails with an HTTP 500.
        (List<string> asked, int pauses, SplitOutcome outcome, bool threw) = await RunAsync(plan, "camus3", retries: 24,
            _ => NotLeader(camus2),
            node => throw new SplitAnswerException($"split-range on {node.Name} answered something that is not its result: {ScanDidNotSettle}"),
            _ => Committed());

        Assert.That(asked, Is.EqualTo(new[] { "camus3", "camus2", "camus2" }), "the leader is asked again, not the node that named it");
        Assert.That(pauses, Is.EqualTo(1), "one pause, before the second ask of the leader");
        Assert.That(threw, Is.False);
        Assert.That(outcome.Ok, Is.True);
        Assert.That(outcome.Node, Is.EqualTo("camus2"));
        Assert.That(outcome.Attempts, Is.EqualTo(3));
    }

    [Test]
    public async Task ANodeThatDoesNotAnswer_GivesWayToTheNext_EvenWhenItWasNamedTheLeader()
    {
        ClusterPlan plan = ThreeNodes();
        NodePlan camus1 = plan.Nodes.Single(n => n.Name == "camus1");

        // camus3 still names the killed camus1. No answer comes from it, so the next node is asked.
        (List<string> asked, _, SplitOutcome outcome, _) = await RunAsync(plan, "camus3", retries: 24,
            _ => NotLeader(camus1),
            _ => throw new InvalidOperationException("'docker run' failed with exit code 7"),
            _ => Committed());

        Assert.That(asked, Is.EqualTo(new[] { "camus3", "camus1", "camus2" }));
        Assert.That(outcome.Ok, Is.True);
        Assert.That(outcome.Node, Is.EqualTo("camus2"));
    }

    [Test]
    public async Task ARetryableRefusalOfTheScan_IsAskedOfTheSameNodeAgain()
    {
        ClusterPlan plan = ThreeNodes();

        (List<string> asked, int pauses, SplitOutcome outcome, _) = await RunAsync(plan, "camus2", retries: 24,
            _ => new SplitRangeResult { Status = "MedianScanFailed", KeySpace = "1:1|r", DescriptorsBefore = 1, Reason = ScanDidNotSettle },
            _ => Committed());

        Assert.That(asked, Is.EqualTo(new[] { "camus2", "camus2" }));
        Assert.That(pauses, Is.EqualTo(1));
        Assert.That(outcome.Ok, Is.True);
    }

    [Test]
    public async Task AFailedOutcome_NamesTheLastAsk_AndKeepsTheErrorOfAnEarlierOne()
    {
        ClusterPlan plan = ThreeNodes();
        NodePlan camus2 = plan.Nodes.Single(n => n.Name == "camus2");

        // The asks run out on the leader's error: the outcome is that error, on that node.
        (_, _, SplitOutcome onTheLeader, bool threw) = await RunAsync(plan, "camus3", retries: 2,
            _ => NotLeader(camus2),
            node => throw new SplitAnswerException($"split-range on {node.Name} answered something that is not its result: {ScanDidNotSettle}"),
            node => throw new SplitAnswerException($"split-range on {node.Name} answered something that is not its result: {ScanDidNotSettle}"));

        Assert.That(threw, Is.True, "a split that did not commit fails the injection");
        Assert.That(onTheLeader.Ok, Is.False);
        Assert.That(onTheLeader.Status, Is.EqualTo("NodeError"));
        Assert.That(onTheLeader.Node, Is.EqualTo("camus2"));
        Assert.That(onTheLeader.Describe(), Does.Contain("did not settle within 5000 ms"));

        // An error of a node nobody named, then a NotLeader of the next: the last ask is the NotLeader,
        // and the error stays in the reason.
        (List<string> asked, _, SplitOutcome afterAnError, _) = await RunAsync(plan, "camus2", retries: 1,
            node => throw new SplitAnswerException($"split-range on {node.Name} answered something that is not its result: {ScanDidNotSettle}"),
            _ => NotLeader(null));

        Assert.That(asked, Is.EqualTo(new[] { "camus2", "camus1" }));
        Assert.That(afterAnError.Status, Is.EqualTo("NotLeader"));
        Assert.That(afterAnError.Node, Is.EqualTo("camus1"));
        Assert.That(afterAnError.Reason, Does.Contain("An earlier ask failed: split-range on camus2"));
        Assert.That(afterAnError.Reason, Does.Contain("did not settle within 5000 ms"));
    }

    [Test]
    public async Task NotLeaderOnEveryNode_PausesBeforeANodeIsAskedASecondTime()
    {
        ClusterPlan plan = ThreeNodes();

        // An election: no node leads and no node names a leader. Six asks are two rounds of the fleet.
        (List<string> asked, int pauses, SplitOutcome outcome, _) = await RunAsync(plan, "camus3", retries: 5,
            _ => NotLeader(null), _ => NotLeader(null), _ => NotLeader(null),
            _ => NotLeader(null), _ => NotLeader(null), _ => NotLeader(null));

        Assert.That(asked, Is.EqualTo(new[] { "camus3", "camus1", "camus2", "camus3", "camus1", "camus2" }));
        Assert.That(pauses, Is.EqualTo(4), "no pause inside the first round; one before each ask of a node that already answered NotLeader, and one before the ask the retries cut off");
        Assert.That(outcome.Ok, Is.False);
        Assert.That(outcome.Status, Is.EqualTo("NotLeader"));
    }

    [Test]
    public void ParseAnswer_TellsASplitResultFromAnotherAnswer()
    {
        SplitRangeResult result = SplitFault.ParseAnswer("camus2",
            """{"success":false,"status":"NotLeader","determinate":true,"keySpace":"1:1|r","descriptorsBefore":1,"leaderHint":"10.101.0.3:7072"}""");
        Assert.That(result.Status, Is.EqualTo("NotLeader"));
        Assert.That(result.LeaderHint, Is.EqualTo("10.101.0.3:7072"));

        SplitAnswerException errorPage = Assert.Throws<SplitAnswerException>(
            () => SplitFault.ParseAnswer("camus2", "<!DOCTYPE html><html><body>Error. Request ID: 0HN</body></html>"))!;
        Assert.That(errorPage.Message, Does.Contain("camus2"));
        Assert.That(errorPage.Message, Does.Contain("Request ID"), "the body is in the message, for the timeline");

        Assert.Throws<SplitAnswerException>(() => SplitFault.ParseAnswer("camus2", ""));
        Assert.Throws<SplitAnswerException>(() => SplitFault.ParseAnswer("camus2", """{"title":"Internal Server Error","traceId":"00-1"}"""),
            "a JSON body without a status is not a split result");
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
