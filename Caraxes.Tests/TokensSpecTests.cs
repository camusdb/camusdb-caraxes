/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using NUnit.Framework;
using Caraxes.Core.Scenario;
using Caraxes.Core.Workload;

namespace Caraxes.Tests;

[TestFixture]
public sealed class TokensSpecTests
{
    private static ScenarioSpec Read(string workload) => ScenarioSpecReader.Read($"""
        name: s
        cluster:
          name: c
        workload:
        {workload}
        """);

    [Test]
    public void TokensKind_ParsesItsCount()
    {
        ScenarioSpec spec = Read("  kind: tokens\n  tokens: 500");
        Assert.That(spec.Workload.Kind, Is.EqualTo("tokens"));
        Assert.That(spec.Workload.Tokens, Is.EqualTo(500));
    }

    [Test]
    public void TokensCount_OnAnotherKind_IsRefused()
    {
        ScenarioException? e = Assert.Throws<ScenarioException>(() => Read("  kind: bank\n  tokens: 500"));
        Assert.That(e!.Message, Does.Contain("applies only to 'workload.kind: tokens'"));
    }

    [Test]
    public void NegativeTokenCount_IsRefused()
    {
        ScenarioException? e = Assert.Throws<ScenarioException>(() => Read("  kind: tokens\n  tokens: -1"));
        Assert.That(e!.Message, Does.Contain("'workload.tokens' must be >= 0"));
    }

    [Test]
    public void TokenVerdict_DeserializesFromTheWorkloadsReconciliation()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"caraxes-tokens-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "reconciliation.json"), """
                {"Passed":false,"Failures":["tokens: 1 slot(s) have more than one row"],
                 "ScanProbe":{"Probes":10,"Exact":9,"Short":0,"Over":1,"Errors":0,"Passed":false},
                 "Tokens":{"Status":"verified","Tokens":500,"Rows":501,"Committed":12345,"Indeterminate":3,
                           "DuplicateSlots":1,"MovesByKind":{"fresh":4000,"same":4100,"back":4245},"Passed":false}}
                """);
            ReconciliationSummary r = WorkloadArtifacts.ReadReconciliation(dir)!;
            Assert.That(r.ScanProbe!.Over, Is.EqualTo(1));
            Assert.That(r.Tokens!.Rows, Is.EqualTo(501));
            Assert.That(r.Tokens.DuplicateSlots, Is.EqualTo(1));
            Assert.That(r.Tokens.MovesByKind["back"], Is.EqualTo(4245));
            Assert.That(r.Tokens.Passed, Is.False);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
