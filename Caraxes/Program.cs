/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using CommandLine;
using Caraxes.Cli;
using Caraxes.Core.Cluster;
using Caraxes.Core.LeaderBalance;
using Caraxes.Core.Matrix;
using Caraxes.Core.Scenario;

using CancellationTokenSource shutdown = new();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};

try
{
    return await Parser.Default.ParseArguments<UpOptions, DownOptions, StatusOptions, LogsOptions, RunOptions, MatrixOptions, LeaderBalanceOptions>(args)
        .MapResult(
            (UpOptions o) => RunClusterAsync(o, orch => orch.UpAsync(o.SkipBuild, TimeSpan.FromSeconds(o.ReadyTimeoutSeconds), shutdown.Token)),
            (DownOptions o) => RunClusterAsync(o, orch => orch.DownAsync(o.KeepVolumes, shutdown.Token)),
            (StatusOptions o) => RunClusterAsync(o, orch => orch.PrintStatusAsync(shutdown.Token)),
            (LogsOptions o) => RunClusterAsync(o, orch => orch.LogsAsync(o.Node, o.Tail, shutdown.Token)),
            (RunOptions o) => RunScenarioAsync(o, shutdown.Token),
            (MatrixOptions o) => RunMatrixAsync(o, shutdown.Token),
            (LeaderBalanceOptions o) => RunLeaderBalanceAsync(o, shutdown.Token),
            _ => Task.FromResult(2)).ConfigureAwait(false);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Interrupted.");
    return 130;
}

static async Task<int> RunClusterAsync(CommonOptions options, Func<ClusterOrchestrator, Task> action)
{
    try
    {
        ClusterSpec spec = ClusterSpecReader.ReadFile(options.Spec);
        ClusterOrchestrator orchestrator = new(spec, options.RunRoot);
        await action(orchestrator).ConfigureAwait(false);
        return 0;
    }
    catch (ClusterSpecException e)
    {
        Console.Error.WriteLine($"Spec error: {e.Message}");
        return 2;
    }
    catch (OperationCanceledException)
    {
        throw;
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"Caraxes failed: {e.GetType().Name}: {e.Message}");
        return 1;
    }
}

static async Task<int> RunScenarioAsync(RunOptions options, CancellationToken cancellationToken)
{
    try
    {
        ScenarioSpec scenario = ScenarioSpecReader.ReadFile(options.Scenario);
        ScenarioRunner runner = new(scenario, options.RunRoot);
        ScenarioVerdict verdict = await runner.RunAsync(options.SkipBuild, cancellationToken).ConfigureAwait(false);
        // A failed scenario is a non-zero exit so a matrix driver or CI can branch on it.
        return verdict.Passed ? 0 : 1;
    }
    catch (ScenarioException e)
    {
        Console.Error.WriteLine($"Scenario error: {e.Message}");
        return 2;
    }
    catch (OperationCanceledException)
    {
        throw;
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"Caraxes failed: {e.GetType().Name}: {e.Message}");
        return 1;
    }
}

static async Task<int> RunLeaderBalanceAsync(LeaderBalanceOptions options, CancellationToken cancellationToken)
{
    try
    {
        ClusterSpec spec = ClusterSpecReader.ReadFile(options.Spec);
        LeaderBalanceTest test = new(spec, options.RunRoot);
        LeaderBalanceVerdict verdict = await test.RunAsync(options.SkipBuild, cancellationToken).ConfigureAwait(false);
        return verdict.Passed ? 0 : 1;
    }
    catch (ClusterSpecException e)
    {
        Console.Error.WriteLine($"Spec error: {e.Message}");
        return 2;
    }
    catch (OperationCanceledException)
    {
        throw;
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"Caraxes failed: {e.GetType().Name}: {e.Message}");
        return 1;
    }
}

static async Task<int> RunMatrixAsync(MatrixOptions options, CancellationToken cancellationToken)
{
    try
    {
        MatrixSpec matrix = MatrixReader.ReadFile(options.Matrix);
        MatrixRunner runner = new(matrix, options.RunRoot);
        IReadOnlyList<MatrixCellResult> results = await runner.RunAsync(cancellationToken).ConfigureAwait(false);
        // Non-zero when any cell failed, so CI can gate on a whole sweep.
        return results.All(r => r.Verdict.Passed) ? 0 : 1;
    }
    catch (ScenarioException e)
    {
        Console.Error.WriteLine($"Matrix error: {e.Message}");
        return 2;
    }
    catch (OperationCanceledException)
    {
        throw;
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"Caraxes failed: {e.GetType().Name}: {e.Message}");
        return 1;
    }
}
