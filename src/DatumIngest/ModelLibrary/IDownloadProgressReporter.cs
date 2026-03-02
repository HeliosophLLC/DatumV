// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

namespace DatumIngest.ModelLibrary;

/// <summary>
/// Sink for model-download lifecycle events. Implementations push these
/// somewhere observable: SignalR clients in the Web host, a logger in CLI
/// tools, a no-op in tests. Core orchestration code talks to this
/// interface; what happens to the events is the host's concern.
/// </summary>
public interface IDownloadProgressReporter
{
    ValueTask OnStartedAsync(ModelDownloadStarted started, CancellationToken ct);

    ValueTask OnProgressAsync(ModelDownloadProgress progress, CancellationToken ct);

    ValueTask OnCompleteAsync(ModelDownloadComplete complete, CancellationToken ct);

    ValueTask OnFailedAsync(ModelDownloadFailed failed, CancellationToken ct);
}
