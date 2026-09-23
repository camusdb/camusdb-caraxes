/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

namespace Caraxes.Core.Scenario;

/// <summary>
/// The Elle check of a <c>workload.kind: append</c> run: elle-cli, in a JRE container, reads the
/// workload's <c>history.edn</c> and searches it for dependency cycles. It applies to the append shape
/// only; the other shapes write no history.
///
/// <para>The check runs after the cluster is gone and needs nothing from it. It gates the verdict: a
/// history Elle calls invalid fails the scenario, and so does one it could not decide
/// (<c>unknown</c>) or could not read, because each of those leaves the run's isolation claim
/// unproven.</para>
/// </summary>
public sealed class ElleSpec
{
    /// <summary>Default elle-cli build; <c>tools/elle/fetch.sh</c> downloads this one.</summary>
    public const string DefaultJar = "tools/elle/elle-cli-0.1.11-standalone.jar";

    /// <summary>Run the check. Default true. Turning it off leaves an append run with no isolation
    /// verdict at all, and the scenario says so in its notes.</summary>
    public bool Enabled { get; set; } = true;

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
