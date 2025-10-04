namespace DatumIngest.Compute;

/// <summary>
/// Configuration options for the DatumIngest gRPC compute backend.
/// Bind from a configuration section (typically <c>"DatumCompute"</c>)
/// or populate programmatically before calling
/// <see cref="DatumComputeServiceExtensions.AddDatumCompute"/>.
/// </summary>
public sealed class DatumComputeOptions
{
    /// <summary>
    /// Gets or sets the API key that clients must supply in the
    /// <c>x-api-key</c> gRPC metadata header. Set to an empty string
    /// to disable API key authentication entirely.
    /// </summary>
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// Gets or sets the maximum gRPC receive message size in bytes.
    /// Defaults to 64 MB.
    /// </summary>
    public int MaxReceiveMessageSize { get; set; } = 64 * 1024 * 1024;

    /// <summary>
    /// Gets or sets the maximum gRPC send message size in bytes.
    /// Defaults to 64 MB.
    /// </summary>
    public int MaxSendMessageSize { get; set; } = 64 * 1024 * 1024;

    /// <summary>
    /// Gets or sets the server-wide default maximum query execution time
    /// in seconds. Set to <see langword="null"/> (the default) to impose
    /// no deadline. Clients may override per-session via <c>CreateSession</c>.
    /// </summary>
    public int? QueryTimeoutSeconds { get; set; }

    /// <summary>
    /// Gets or sets the server-wide default maximum number of rows the
    /// server will stream back for a single query. Set to
    /// <see langword="null"/> (the default) for no limit. Clients may
    /// override per-session via <c>CreateSession</c>.
    /// </summary>
    public long? MaxOutputRows { get; set; }

    /// <summary>
    /// Gets or sets the server-wide default throttle delay in milliseconds,
    /// injected periodically during row streaming to yield CPU to other
    /// sessions. Set to <see langword="null"/> (the default) for no
    /// throttle. Clients may override per-session via <c>CreateSession</c>.
    /// </summary>
    public int? ThrottleDelayMilliseconds { get; set; }

    /// <summary>
    /// Gets or sets the server-wide default maximum Query Units a single
    /// query may accumulate from function invocations. Set to
    /// <see langword="null"/> (the default) for no limit. Clients may
    /// override per-session via <c>CreateSession</c>.
    /// </summary>
    public long? MaxQueryUnits { get; set; }

    /// <summary>
    /// Gets or sets the server-wide default memory budget in bytes for
    /// spill-to-disk joins. Hash joins that exceed this budget will partition
    /// and spill to temporary files instead of failing. Defaults to 256 MB,
    /// which is conservative enough for multi-tenant deployments where many
    /// concurrent sessions share a single host. Clients may override
    /// per-session via <c>CreateSession</c>. Set to <see langword="null" />
    /// to disable the budget (fully in-memory joins — not recommended in
    /// production).
    /// </summary>
    public long? MemoryBudgetBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>
    /// Gets or sets the server-wide default maximum number of queries that
    /// may execute simultaneously on a single session. Defaults to 3.
    /// Set to <see langword="null"/> to allow unlimited concurrent queries.
    /// Clients may override per-session via <c>CreateSession</c>.
    /// </summary>
    public int? MaxConcurrentQueries { get; set; } = 3;

    /// <summary>
    /// Gets or sets the file path for the execution trace log. When set,
    /// <see cref="DatumIngest.Diagnostics.ExecutionTracer"/> writes timestamped
    /// diagnostic events (join strategy decisions, build/probe phases, batch
    /// streaming) to this file. The trace is invaluable for diagnosing slow
    /// or hanging queries. Set to <see langword="null"/> (the default) to
    /// disable tracing. The <c>DATUM_TRACE_FILE</c> environment variable is
    /// still honoured as a fallback when this property is not set.
    /// </summary>
    public string? TraceFilePath { get; set; }

    /// <summary>
    /// Gets or sets whether gRPC error responses include full exception details
    /// (type, message, and stack trace). Enable for in-process or development
    /// use; disable in production to avoid leaking internal details to clients.
    /// Defaults to <see langword="false"/>.
    /// </summary>
    public bool EnableDetailedErrors { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of parallel operator workers allowed
    /// across all concurrent queries. Parallel operators (e.g. parallel hash
    /// join probe, parallel hash aggregate) acquire slots from a shared
    /// <see cref="DatumIngest.Execution.ParallelismBudget"/> before starting worker tasks.
    /// Defaults to <see cref="Environment.ProcessorCount"/>.
    /// Set to <see langword="null"/> to disable the budget (each query spawns
    /// up to <c>ProcessorCount</c> workers independently — not recommended
    /// for multi-tenant deployments).
    /// </summary>
    public int? MaxParallelWorkers { get; set; } = Environment.ProcessorCount;
}
