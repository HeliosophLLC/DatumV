// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member

namespace Heliosoph.DatumV.GpuRuntime;

/// <summary>
/// Sink for CUDA-bundle install lifecycle events. Implementations push
/// these somewhere observable: SignalR clients in the Web host, a logger
/// in CLI tools, a no-op in tests.
/// </summary>
public interface ICudaBundleInstallProgressReporter
{
    ValueTask OnStartedAsync(CudaBundleInstallStarted started, CancellationToken ct);
    ValueTask OnDownloadProgressAsync(CudaBundleDownloadProgress progress, CancellationToken ct);
    ValueTask OnExtractStartedAsync(CudaBundleExtractStarted started, CancellationToken ct);
    ValueTask OnExtractProgressAsync(CudaBundleExtractProgress progress, CancellationToken ct);
    ValueTask OnInstalledAsync(CudaBundleInstalled installed, CancellationToken ct);
    ValueTask OnFailedAsync(CudaBundleInstallFailed failed, CancellationToken ct);
}

/// <summary>
/// Default no-op reporter for tests and CLI consumers that don't need to
/// surface install progress anywhere.
/// </summary>
public sealed class NullCudaBundleInstallProgressReporter : ICudaBundleInstallProgressReporter
{
    public static NullCudaBundleInstallProgressReporter Instance { get; } = new();
    private NullCudaBundleInstallProgressReporter() { }

    public ValueTask OnStartedAsync(CudaBundleInstallStarted started, CancellationToken ct)
        => ValueTask.CompletedTask;
    public ValueTask OnDownloadProgressAsync(CudaBundleDownloadProgress progress, CancellationToken ct)
        => ValueTask.CompletedTask;
    public ValueTask OnExtractStartedAsync(CudaBundleExtractStarted started, CancellationToken ct)
        => ValueTask.CompletedTask;
    public ValueTask OnExtractProgressAsync(CudaBundleExtractProgress progress, CancellationToken ct)
        => ValueTask.CompletedTask;
    public ValueTask OnInstalledAsync(CudaBundleInstalled installed, CancellationToken ct)
        => ValueTask.CompletedTask;
    public ValueTask OnFailedAsync(CudaBundleInstallFailed failed, CancellationToken ct)
        => ValueTask.CompletedTask;
}
