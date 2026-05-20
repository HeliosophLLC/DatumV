// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

namespace Heliosoph.DatumV.ModelLibrary;

/// <summary>
/// Default <see cref="IDownloadProgressReporter"/> implementation that drops
/// every event. Suitable for tests, CLI one-shots, and any consumer that
/// just wants the bytes without telemetry. Use <see cref="Instance"/> rather
/// than constructing; the type is stateless.
/// </summary>
public sealed class NullDownloadProgressReporter : IDownloadProgressReporter
{
    public static readonly NullDownloadProgressReporter Instance = new();

    private NullDownloadProgressReporter() { }

    public ValueTask OnStartedAsync(ModelDownloadStarted started, CancellationToken ct)
        => ValueTask.CompletedTask;

    public ValueTask OnProgressAsync(ModelDownloadProgress progress, CancellationToken ct)
        => ValueTask.CompletedTask;

    public ValueTask OnCompleteAsync(ModelDownloadComplete complete, CancellationToken ct)
        => ValueTask.CompletedTask;

    public ValueTask OnInstallingAsync(ModelInstalling installing, CancellationToken ct)
        => ValueTask.CompletedTask;

    public ValueTask OnInstalledAsync(ModelInstalled installed, CancellationToken ct)
        => ValueTask.CompletedTask;

    public ValueTask OnFailedAsync(ModelDownloadFailed failed, CancellationToken ct)
        => ValueTask.CompletedTask;
}
