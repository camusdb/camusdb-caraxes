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
/// Per-category log levels exist because CamusDB defaults Kommander to <c>Warning</c>, which
/// hides every Kommander <c>Information</c> line. That filter misled two investigations:
/// "snapshot mentions: 0" could not distinguish "never attempted" from "attempted silently"
/// (Kommander feature d11fd5f9), and the run-H stall left a silent tail because election wins
/// log at Information.
///
/// The override goes through <c>CAMUS_LOG_FILTERS</c>, not <c>Logging__LogLevel__*</c>:
/// CamusDB's Program.cs calls <c>ClearProviders()</c> and installs explicit <c>AddFilter</c>
/// rules for Kahuna, Kommander and Grpc, so the standard configuration path never reaches those
/// categories. Verified empirically — the <c>Logging__LogLevel__Kommander</c> variable arrived
/// in the container and Kommander still logged only at Warning.
/// </summary>
[TestFixture]
public sealed class LogLevelTests
{
    [Test]
    public void LogLevelFlowsIntoComposeAsAnEnvironmentOverride()
    {
        ClusterPlan plan = ClusterPlan.FromSpec(ClusterSpecReader.Read(string.Join('\n',
            "name: logs",
            "nodes: 3",
            "log_levels:",
            "  Kommander: Information")));

        string yml = ComposeGenerator.Generate(plan, "./config");

        Assert.That(plan.Spec.LogLevels["Kommander"], Is.EqualTo("Information"));
        Assert.That(yml, Does.Contain("CAMUS_LOG_FILTERS: Kommander=Information"));
    }

    [Test]
    public void SeveralCategoriesAllAppear()
    {
        ClusterPlan plan = ClusterPlan.FromSpec(ClusterSpecReader.Read(string.Join('\n',
            "name: logs",
            "log_levels:",
            "  Kommander: Information",
            "  Kahuna: Debug")));

        string yml = ComposeGenerator.Generate(plan, "./config");

        Assert.That(yml, Does.Contain("CAMUS_LOG_FILTERS:"));
        Assert.That(yml, Does.Contain("Kommander=Information"));
        Assert.That(yml, Does.Contain("Kahuna=Debug"));
    }

    [Test]
    public void DefaultEmitsNoOverride()
    {
        ClusterPlan plan = ClusterPlan.FromSpec(ClusterSpecReader.Read("name: logs"));

        string yml = ComposeGenerator.Generate(plan, "./config");

        Assert.That(plan.Spec.LogLevels, Is.Empty);
        Assert.That(yml, Does.Not.Contain("CAMUS_LOG_FILTERS"));
    }
}
