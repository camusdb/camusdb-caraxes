/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

namespace Caraxes.Core.Scenario;

/// <summary>
/// The Elle check of an Elle shape's run (<c>workload.kind</c> <c>append</c>, <c>rw</c> or
/// <c>predicate</c>): elle-cli, in a JDK container, reads the workload's <c>history.edn</c> and
/// searches it for dependency cycles. The append shape is checked by the list-append model, the rw
/// and predicate shapes by the rw-register model. The other shapes write no history.
///
/// <para>The check runs after the cluster is gone and needs nothing from it. It gates the verdict: a
/// history Elle calls invalid fails the scenario, and so does one it could not decide
/// (<c>unknown</c>) or could not read, because each of those leaves the run's isolation claim
/// unproven. <see cref="Gated"/> turns a finding into a measurement for an arm whose claim is not
/// made, such as strict serializability.</para>
/// </summary>
public sealed class ElleSpec
{
    /// <summary>
    /// Default elle-cli build; <c>tools/elle/build.sh</c> builds it. It is elle-cli at the commit that
    /// moved to Elle 0.2.8 (2026-10-16), which carries the rw-register fix of Jepsen 0.3.15 and the
    /// repaired Bifurcan. The 0.1.11 release of 2026-09-01 bundles Elle 0.2.7, before that fix.
    /// </summary>
    public const string DefaultJar = "tools/elle/elle-cli-0.1.11-elle0.2.8-standalone.jar";

    /// <summary>Run the check. Default true. Turning it off leaves an Elle run with no isolation
    /// verdict at all, and the scenario says so in its notes.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Fail the run on an Elle finding, a predicate count mismatch, or a real-time stale final value.
    /// Default true. When false, each of those is reported as <c>MEASURED, NOT GATED</c> and the run
    /// passes on its other checks.
    ///
    /// <para>For an arm that measures a claim CamusDB does not make. A strict-serializable arm records
    /// how far real-time order is violated, and a sudden change in that measurement is a regression
    /// signal even when the claim is absent; a durability arm with synchronous WAL writes off measures
    /// what a kill costs under a setting whose contract allows loss. The workload's own final-state
    /// check (lost appends, refused-but-visible values, duplicate rows) is never affected: those fail
    /// reconciliation under every claim.</para>
    /// </summary>
    public bool Gated { get; set; } = true;

    /// <summary>The checker model for a workload kind: <c>list-append</c> for the append shape,
    /// <c>rw-register</c> for the rw and predicate shapes.</summary>
    public static string ModelFor(string kind) => kind == "append" ? "list-append" : "rw-register";

    /// <summary>Path to the elle-cli standalone jar. A relative path resolves against the directory
    /// Caraxes runs from, as <c>runs/</c> does.</summary>
    public string Jar { get; set; } = DefaultJar;

    /// <summary>
    /// Image that supplies the JVM. Only <c>java</c> is used from it.
    ///
    /// <para>A JDK image, not a JRE: Jepsen's util namespace creates an <c>L64X128MixRandom</c> generator
    /// when it loads, and that algorithm lives in the <c>jdk.random</c> module, which the Temurin JRE image
    /// leaves out. On the JRE every check dies before it reads the history.</para>
    /// </summary>
    public string Image { get; set; } = "eclipse-temurin:21-jdk";

    /// <summary>
    /// Draw a diagram of each cycle Elle finds. Default false: Elle draws with Graphviz <c>dot</c>, the
    /// default image has none, and a missing <c>dot</c> crashes the check (exit 255) exactly when it has
    /// found an anomaly. The text explanations under <c>run/elle/</c> are written either way. Set true
    /// only with an <see cref="Image"/> that has Graphviz installed.
    /// </summary>
    public bool Plots { get; set; }

    /// <summary>
    /// Comma-separated models the history must satisfy, e.g. <c>serializable</c> or
    /// <c>strict-serializable</c>. Empty (the default) derives it from the run's isolation:
    /// <c>serializable</c> for serializable, <c>read-committed</c> for read committed.
    ///
    /// <para>The default is deliberately not <c>strict-serializable</c>. CamusDB claims serializability;
    /// a read-only snapshot may lag a write that already returned, which strict serializability forbids.
    /// Ask for it by name when that is the claim under test.</para>
    /// </summary>
    public string ConsistencyModels { get; set; } = "";

    /// <summary>Comma-separated extra anomalies to search for beyond what the models forbid, e.g.
    /// <c>G1a,G1b</c>. Empty (the default) adds none.</summary>
    public string Anomalies { get; set; } = "";

    /// <summary>Milliseconds Elle may spend searching one strongly connected component for a cycle.
    /// 0 leaves elle-cli's default (1000). A search that times out makes the verdict <c>unknown</c>,
    /// so raise it for long histories rather than accept a verdict that decided nothing.</summary>
    public int CycleSearchTimeoutMs { get; set; }

    /// <summary>JVM heap for the check, in MiB (<c>-Xmx</c>). 0 leaves the JVM default, a quarter of
    /// the container's memory. Elle holds the whole history in memory, so a long soak needs more.</summary>
    public int HeapMb { get; set; }

    /// <summary>An independent copy, field for field, memberwise for the reason given on
    /// <see cref="WorkloadSpec.Clone"/>. Every field is a value type or a string.</summary>
    public ElleSpec Clone() => (ElleSpec)MemberwiseClone();

    /// <summary>The models to pass, the explicit setting first, else the one derived from isolation.</summary>
    public string EffectiveConsistencyModels(string isolation)
        => !string.IsNullOrWhiteSpace(ConsistencyModels)
            ? ConsistencyModels.Replace(" ", "")
            : isolation == "read_committed" ? "read-committed" : "serializable";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Jar))
            throw new ScenarioException("'elle.jar' must name the elle-cli standalone jar");
        if (string.IsNullOrWhiteSpace(Image))
            throw new ScenarioException("'elle.image' must name an image that provides java");
        if (CycleSearchTimeoutMs < 0)
            throw new ScenarioException($"'elle.cycle_search_timeout_ms' must be >= 0, got {CycleSearchTimeoutMs}");
        if (HeapMb < 0)
            throw new ScenarioException($"'elle.heap_mb' must be >= 0, got {HeapMb}");
    }
}
