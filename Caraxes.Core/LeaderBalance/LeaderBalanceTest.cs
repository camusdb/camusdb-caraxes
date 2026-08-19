/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Text;
using System.Text.Json;
using Caraxes.Core.Cluster;

namespace Caraxes.Core.LeaderBalance;

/// <summary>Verdict of a leader-balance test, plus the metrics it turned on.</summary>
public sealed record LeaderBalanceVerdict(
    bool Passed,
    int FairShare,
    int RegainedLeaders,
    int FinalImbalance,
    string RunDirectory,
    IReadOnlyList<string> Notes);

/// <summary>
/// Tests the Raft leader balancer directly. It measures how partition leadership is spread, kills the
/// node that leads the most partitions (so its leaderships pile onto the survivors), restarts it, and
/// watches whether the balancer moves leadership back to it. The rejoined node starts leading nothing;
/// with the balancer on it should regain roughly its fair share within a few balancer passes, and the
/// overall spread should return to near-even. With the balancer off it would stay at zero — which is
/// what makes this a real test of the balancer, not just of re-election.
/// </summary>
public sealed class LeaderBalanceTest
{
    private readonly ClusterSpec spec;

    private readonly string runDir;

    private readonly ClusterOrchestrator orchestrator;

    // Defaults chosen so the whole test runs in a few minutes on a laptop while giving the balancer
    // (whose pass interval the test spec shortens) several passes to act.
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ReelectionWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RebalanceTimeout = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    public LeaderBalanceTest(ClusterSpec spec, string? runRoot = null)
    {
        this.spec = spec;
        string root = runRoot ?? Path.Combine(Environment.CurrentDirectory, "runs");
        runDir = Path.Combine(root, "leader-balance", spec.Name);
        orchestrator = new ClusterOrchestrator(spec, Path.Combine(root, "clusters"));
    }

    public async Task<LeaderBalanceVerdict> RunAsync(bool skipBuild = false, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(runDir);
        List<string> notes = [];
        List<LeaderSnapshot> timeline = [];
        ClusterPlan plan = orchestrator.Plan;

        if (!spec.LeaderBalancer)
            notes.Add("warning: leader_balancer is OFF for this cluster — the rejoined node is expected NOT to regain leadership");

        using HttpProbes probes = new();
        var allNodes = plan.Nodes.Select(n => n.Name).ToList();

        try
        {
            await orchestrator.UpAsync(skipBuild, cancellationToken: cancellationToken).ConfigureAwait(false);

            // Baseline: wait for leadership to settle, then measure the even spread.
            LeaderSnapshot baseline = await LeaderObservation.WaitForStableAsync(plan, probes, SettleTimeout, cancellationToken).ConfigureAwait(false);
            timeline.Add(baseline);
            int fairShare = baseline.TotalPartitions == 0 ? 0 : baseline.TotalPartitions / plan.Nodes.Count;
            notes.Add($"baseline: {baseline.Format(allNodes)}; fair share ~{fairShare} leaders/node");

            // Kill the busiest leader so its leaderships pile onto the survivors.
            NodePlan victim = plan.Nodes.OrderByDescending(n => baseline.LeadersOn(n.Name)).First();
            int victimBefore = baseline.LeadersOn(victim.Name);
            Console.WriteLine($"==> killing {victim.Name} (leads {victimBefore} partition(s))");
            await ProcessRunner.RunCheckedAsync("docker", ["kill", "--signal", "KILL", victim.ContainerName], cancellationToken: cancellationToken).ConfigureAwait(false);

            List<string> survivors = allNodes.Where(n => n != victim.Name).ToList();
            await Task.Delay(ReelectionWait, cancellationToken).ConfigureAwait(false);
            LeaderSnapshot concentrated = await LeaderObservation.MeasureAsync(plan, probes, cancellationToken).ConfigureAwait(false);
            timeline.Add(concentrated);
            notes.Add($"after kill: {concentrated.Format(allNodes)}; imbalance among survivors = {concentrated.Imbalance(survivors)}");

            // Restart the node. It rejoins leading nothing; the balancer must move leadership back.
            Console.WriteLine($"==> restarting {victim.Name}; watching the balancer for {RebalanceTimeout.TotalSeconds:0}s");
            await ProcessRunner.RunCheckedAsync("docker", ["start", victim.ContainerName], cancellationToken: cancellationToken).ConfigureAwait(false);

            LeaderSnapshot last = concentrated;
            int bestRegained = 0;
            DateTime deadline = DateTime.UtcNow + RebalanceTimeout;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                last = await LeaderObservation.MeasureAsync(plan, probes, cancellationToken).ConfigureAwait(false);
                timeline.Add(last);
                bestRegained = Math.Max(bestRegained, last.LeadersOn(victim.Name));
                Console.WriteLine($"    {last.Format(allNodes)}");

                // Early exit once the rejoined node is back to its fair share and the spread is even.
                if (last.LeadersOn(victim.Name) >= fairShare && last.Imbalance(allNodes) <= 1)
                    break;
            }

            int finalImbalance = last.Imbalance(allNodes);
            notes.Add($"after restart: {last.Format(allNodes)}; rejoined node regained up to {bestRegained} leader(s); final imbalance = {finalImbalance}");

            // Pass when the rejoined node got back at least half its fair share AND the final spread is
            // near-even. Half fair share is deliberately lenient: the balancer is load-weighted and
            // moves gradually, and with no client load all partitions weigh the same so it balances by
            // count. When the balancer is off, the rejoined node stays at 0 and this fails, as intended.
            int minRegain = Math.Max(1, fairShare / 2);
            bool regainedEnough = bestRegained >= minRegain;
            bool evenEnough = finalImbalance <= fairShare + 1;
            bool passed = fairShare > 0 && regainedEnough && evenEnough;

            if (fairShare == 0)
                notes.Add("CHECK FAILED: could not establish a baseline leader distribution (no partitions resolved)");
            if (!regainedEnough)
                notes.Add($"CHECK FAILED: rejoined node regained only {bestRegained} leader(s), below the {minRegain} expected — the balancer did not move leadership back");
            if (!evenEnough)
                notes.Add($"CHECK FAILED: final leader imbalance {finalImbalance} exceeds the {fairShare + 1} bound — leadership stayed concentrated");

            LeaderBalanceVerdict verdict = new(passed, fairShare, bestRegained, finalImbalance, runDir, notes);
            WriteArtifacts(timeline, verdict, allNodes);
            PrintVerdict(verdict);
            return verdict;
        }
        finally
        {
            Console.WriteLine("==> tearing down cluster");
            try
            {
                await orchestrator.DownAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                notes.Add($"teardown reported: {e.Message}");
            }
        }
    }

    private void WriteArtifacts(IReadOnlyList<LeaderSnapshot> timeline, LeaderBalanceVerdict verdict, IReadOnlyList<string> nodeOrder)
    {
        using (StreamWriter jsonl = new(Path.Combine(runDir, "leaders.jsonl"), append: false))
        {
            foreach (LeaderSnapshot s in timeline)
                jsonl.WriteLine(JsonSerializer.Serialize(new { ts = s.Utc.ToString("o"), leaders = s.LeadersByNode, resolved = s.ResolvedPartitions, total = s.TotalPartitions }));
        }

        StringBuilder sb = new();
        sb.AppendLine($"# Leader-balance test — {spec.Name}");
        sb.AppendLine();
        sb.AppendLine($"Verdict: **{(verdict.Passed ? "PASS" : "FAIL")}** — fair share ~{verdict.FairShare}/node, rejoined node regained up to {verdict.RegainedLeaders}, final imbalance {verdict.FinalImbalance}.");
        sb.AppendLine();
        sb.AppendLine("| time | " + string.Join(" | ", nodeOrder) + " | resolved |");
        sb.AppendLine("|---|" + string.Concat(nodeOrder.Select(_ => "---|")) + "---|");
        foreach (LeaderSnapshot s in timeline)
            sb.AppendLine($"| {s.Utc:HH:mm:ss} | " + string.Join(" | ", nodeOrder.Select(n => s.LeadersOn(n))) + $" | {s.ResolvedPartitions}/{s.TotalPartitions} |");
        sb.AppendLine();
        foreach (string note in verdict.Notes)
            sb.AppendLine($"- {note}");

        File.WriteAllText(Path.Combine(runDir, "leader-balance.md"), sb.ToString());
    }

    private static void PrintVerdict(LeaderBalanceVerdict verdict)
    {
        Console.WriteLine();
        Console.WriteLine($"leader-balance verdict: {(verdict.Passed ? "PASS" : "FAIL")}");
        foreach (string note in verdict.Notes)
            Console.WriteLine($"  - {note}");
        Console.WriteLine($"  artifacts: {verdict.RunDirectory}");
    }
}
