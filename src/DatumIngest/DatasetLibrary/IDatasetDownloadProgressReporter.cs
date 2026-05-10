// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

namespace DatumIngest.DatasetLibrary;

/// <summary>
/// Sink for dataset-download lifecycle events. Implementations push these
/// somewhere observable: SignalR clients in the Web host, a logger in CLI
/// tools, a no-op in tests.
/// </summary>
public interface IDatasetDownloadProgressReporter
{
    ValueTask OnStartedAsync(DatasetDownloadStarted started, CancellationToken ct);

    ValueTask OnProgressAsync(DatasetDownloadProgress progress, CancellationToken ct);

    ValueTask OnCompleteAsync(DatasetDownloadComplete complete, CancellationToken ct);

    // Emitted at the start of each ingest job — once per CatalogIngestJob
    // in the version's `ingest[]` array.
    ValueTask OnIngestingAsync(DatasetIngesting ingesting, CancellationToken ct);

    // Periodic in-flight row count while an ingest job is running.
    // Optional from the consumer's perspective — phase transitions still
    // flow through OnIngestingAsync / OnTableIngestedAsync.
    ValueTask OnIngestProgressAsync(DatasetIngestProgress progress, CancellationToken ct);

    // Emitted after each ingest job's .datum file lands on disk. The
    // terminal success event is OnInstalledAsync, fired after every
    // job's OnIngestedAsync.
    ValueTask OnTableIngestedAsync(DatasetTableIngested ingested, CancellationToken ct);

    ValueTask OnInstalledAsync(DatasetInstalled installed, CancellationToken ct);

    ValueTask OnFailedAsync(DatasetDownloadFailed failed, CancellationToken ct);
}

/// <summary>
/// Default no-op reporter for tests and CLI consumers that don't need to
/// surface install progress anywhere.
/// </summary>
public sealed class NullDatasetDownloadProgressReporter : IDatasetDownloadProgressReporter
{
    public static NullDatasetDownloadProgressReporter Instance { get; } = new();
    private NullDatasetDownloadProgressReporter() { }

    public ValueTask OnStartedAsync(DatasetDownloadStarted started, CancellationToken ct)
        => ValueTask.CompletedTask;
    public ValueTask OnProgressAsync(DatasetDownloadProgress progress, CancellationToken ct)
        => ValueTask.CompletedTask;
    public ValueTask OnCompleteAsync(DatasetDownloadComplete complete, CancellationToken ct)
        => ValueTask.CompletedTask;
    public ValueTask OnIngestingAsync(DatasetIngesting ingesting, CancellationToken ct)
        => ValueTask.CompletedTask;
    public ValueTask OnIngestProgressAsync(DatasetIngestProgress progress, CancellationToken ct)
        => ValueTask.CompletedTask;
    public ValueTask OnTableIngestedAsync(DatasetTableIngested ingested, CancellationToken ct)
        => ValueTask.CompletedTask;
    public ValueTask OnInstalledAsync(DatasetInstalled installed, CancellationToken ct)
        => ValueTask.CompletedTask;
    public ValueTask OnFailedAsync(DatasetDownloadFailed failed, CancellationToken ct)
        => ValueTask.CompletedTask;
}
