/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using CommandLine;

namespace Caraxes.Cli;

/// <summary>Options shared by every verb: the cluster spec that defines the target.</summary>
public abstract class CommonOptions
{
    [Option("spec", Required = true, HelpText = "Path to the cluster spec YAML")]
    public string Spec { get; set; } = "";

    [Option("run-root", Required = false,
        HelpText = "Directory holding per-cluster run artifacts (default: ./runs)")]
    public string? RunRoot { get; set; }
}

[Verb("up", HelpText = "Build the image, generate artifacts, start the cluster, and wait until every node is ready")]
public sealed class UpOptions : CommonOptions
{
    [Option("skip-build", Required = false, Default = false,
        HelpText = "Reuse the existing image instead of rebuilding it from the camusdb repo; ignored when the dev certificate has to be regenerated")]
    public bool SkipBuild { get; set; }

    [Option("ready-timeout", Required = false, Default = 180,
        HelpText = "Seconds to wait for all nodes to report ready before failing")]
    public int ReadyTimeoutSeconds { get; set; }
}

[Verb("down", HelpText = "Stop the cluster and remove its containers, network, and volumes")]
public sealed class DownOptions : CommonOptions
{
    [Option("keep-volumes", Required = false, Default = false,
        HelpText = "Keep the data volumes so a later 'up' resumes with the same data")]
    public bool KeepVolumes { get; set; }
}

[Verb("status", HelpText = "Show per-node health and the cluster's partition placement table")]
public sealed class StatusOptions : CommonOptions
{
}

[Verb("logs", HelpText = "Print a node's container logs")]
public sealed class LogsOptions : CommonOptions
{
    [Option("node", Required = true, HelpText = "Node name (camus1..camusN)")]
    public string Node { get; set; } = "";

    [Option("tail", Required = false, Default = 200, HelpText = "Number of trailing log lines")]
    public int Tail { get; set; }
}

[Verb("run", HelpText = "Run a scenario: bring the cluster up, drive the workload, collect artifacts, render a verdict, and tear down")]
public sealed class RunOptions
{
    [Option("scenario", Required = true, HelpText = "Path to the scenario YAML")]
    public string Scenario { get; set; } = "";

    [Option("run-root", Required = false,
        HelpText = "Directory holding run artifacts (default: ./runs)")]
    public string? RunRoot { get; set; }

    [Option("skip-build", Required = false, Default = false,
        HelpText = "Reuse the existing image instead of rebuilding it from the camusdb repo; ignored when the dev certificate has to be regenerated")]
    public bool SkipBuild { get; set; }
}

[Verb("matrix", HelpText = "Run a cartesian sweep of scenarios and write a cross-cell report")]
public sealed class MatrixOptions
{
    [Option("matrix", Required = true, HelpText = "Path to the matrix YAML")]
    public string Matrix { get; set; } = "";

    [Option("run-root", Required = false,
        HelpText = "Directory holding run artifacts (default: ./runs)")]
    public string? RunRoot { get; set; }
}

[Verb("leader-balance", HelpText = "Test the Raft leader balancer: kill the busiest leader, restart it, and check the balancer moves leadership back")]
public sealed class LeaderBalanceOptions
{
    [Option("spec", Required = true, HelpText = "Path to the cluster spec YAML")]
    public string Spec { get; set; } = "";

    [Option("run-root", Required = false,
        HelpText = "Directory holding run artifacts (default: ./runs)")]
    public string? RunRoot { get; set; }

    [Option("skip-build", Required = false, Default = false,
        HelpText = "Reuse the existing image instead of rebuilding it from the camusdb repo; ignored when the dev certificate has to be regenerated")]
    public bool SkipBuild { get; set; }
}
