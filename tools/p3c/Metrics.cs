namespace P3c;

/// <summary>
/// The rows every mode reports, in one place so `extract` and `compare` cannot drift apart. Each is a
/// label and how to read it from a run.
/// </summary>
public static class Metrics
{
    public const string FinalizePrepare = "kahuna_durable_tx_finalize_prepare_ms_milliseconds";
    public const string FinalizeDecision = "kahuna_durable_tx_finalize_decision_ms_milliseconds";
    public const string FinalizeValidate = "kahuna_durable_tx_finalize_validate_ms_milliseconds";
    public const string ReplicaFence = "kahuna_durable_tx_finalize_replica_fence_ms_milliseconds";
    public const string RaftDuration = "kahuna_kv_write_raft_duration_milliseconds";
    public const string QueueAge = "kahuna_kv_write_queue_age_milliseconds";
    public const string SubmissionQueueDelay = "kahuna_kv_write_submission_queue_delay_milliseconds";
    public const string CompletionDelay = "kahuna_kv_write_completion_delay_milliseconds";

    public static readonly (string Label, Func<RunArtifacts, double?> Read)[] Rows =
    [
        ("throughput ops/s",          r => r.Ops),
        ("write p50 ms",              r => r.Latency("WriteLatency", "P50")),
        ("write p99 ms",              r => r.Latency("WriteLatency", "P99")),
        ("read p50 ms",               r => r.Latency("ReadLatency", "P50")),
        ("finalize prepare mean ms",  r => r.Metrics.Mean(FinalizePrepare)),
        ("finalize prepare p99 ms",   r => r.Metrics.Quantile(FinalizePrepare, 0.99)),
        ("finalize decision mean ms", r => r.Metrics.Mean(FinalizeDecision)),
        ("finalize validate mean ms", r => r.Metrics.Mean(FinalizeValidate)),
        ("replica fence mean ms",     r => r.Metrics.Mean(ReplicaFence)),
        ("kv queue age p99 ms",       r => r.Metrics.Quantile(QueueAge, 0.99)),
        // Admissibility, not an outcome: see Run.RaftWriteMeanMs.
        ("raft write mean ms",        r => r.RaftWriteMeanMs),
        ("late commit rejections",    r => r.Metrics.Sum("kahuna_durable_tx_late_commit_rejections_total")),
        ("one-phase commits",         r => r.Metrics.Sum("kahuna_durable_tx_one_phase_commits_total")),
        ("one-phase fallbacks",       r => r.Metrics.Sum("kahuna_durable_tx_one_phase_fallbacks_total")),
        ("one-phase gate entered",    r => r.Metrics.Value("kahuna_durable_tx_one_phase_gate_total", "outcome=entered")),
        ("conflicts",                 r => r.Conflicts),
        ("resident records (end)",    r => r.Metrics.Sum("kahuna_durable_tx_resident_records")),
        ("resident intents (end)",    r => r.Metrics.Sum("kahuna_durable_tx_resident_prepared_intents")),
        ("resident receipts (end)",   r => r.Metrics.Sum("kahuna_durable_tx_resident_receipts")),
    ];

    /// <summary>The gauges whose post-load behaviour the feature's acceptance asks about.</summary>
    public static readonly HashSet<string> RetentionGauges =
    [
        "kahuna_durable_tx_resident_records",
        "kahuna_durable_tx_resident_prepared_intents",
        "kahuna_durable_tx_resident_receipts",
        "kahuna_durable_tx_outstanding",
    ];

    /// <summary>
    /// The five kinds of durable work, as (class, type) pairs on the scheduler's per-submission delay.
    /// Kahuna task 7 measured these in process, where every kind waited only the linger; the production
    /// cell is a RocksDB synchronous WAL, where the Raft round is the unit of waiting.
    /// </summary>
    public static readonly (string Kind, string Labels)[] WorkKinds =
    [
        ("init",        "class=ordinary,type=record"),
        ("prepare",     "class=ordinary,type=intent"),
        ("decision",    "class=terminal,type=record"),
        ("materialize", "class=terminal,type=kv"),
        ("settle",      "class=terminal,type=intent"),
    ];
}
