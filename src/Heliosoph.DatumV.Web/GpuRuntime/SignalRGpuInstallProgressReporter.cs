using Heliosoph.DatumV.GpuRuntime;
using Heliosoph.DatumV.Web.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Heliosoph.DatumV.Web.GpuRuntime;

// Adapter that bridges the core ICudaBundleInstallProgressReporter to the
// Web's SignalR hub. Receives core event records from CudaBundleInstaller,
// converts them to wire DTOs (which carry [Tapper.TranspilationSource]),
// and broadcasts to all connected clients. Single-user desktop today.
//
// Parallel to SignalRDatasetDownloadProgressReporter and
// SignalRDownloadProgressReporter — same boundary-conversion pattern.
internal sealed class SignalRGpuInstallProgressReporter(
    IHubContext<StreamHub, IStreamHubClient> hub) : ICudaBundleInstallProgressReporter
{
    public async ValueTask OnStartedAsync(CudaBundleInstallStarted e, CancellationToken ct)
        => await hub.Clients.All.OnCudaBundleInstallStarted(
            new CudaBundleInstallStartedDto(e.Version, e.TotalBytes))
            .ConfigureAwait(false);

    public async ValueTask OnDownloadProgressAsync(CudaBundleDownloadProgress e, CancellationToken ct)
        => await hub.Clients.All.OnCudaBundleDownloadProgress(
            new CudaBundleDownloadProgressDto(e.Version, e.BytesDownloaded, e.TotalBytes))
            .ConfigureAwait(false);

    public async ValueTask OnExtractStartedAsync(CudaBundleExtractStarted e, CancellationToken ct)
        => await hub.Clients.All.OnCudaBundleExtractStarted(
            new CudaBundleExtractStartedDto(e.Version))
            .ConfigureAwait(false);

    public async ValueTask OnExtractProgressAsync(CudaBundleExtractProgress e, CancellationToken ct)
        => await hub.Clients.All.OnCudaBundleExtractProgress(
            new CudaBundleExtractProgressDto(
                e.Version, e.FilesExtracted, e.TotalFiles, e.BytesExtracted))
            .ConfigureAwait(false);

    public async ValueTask OnInstalledAsync(CudaBundleInstalled e, CancellationToken ct)
        => await hub.Clients.All.OnCudaBundleInstalled(
            new CudaBundleInstalledDto(e.Version, e.InstalledPath))
            .ConfigureAwait(false);

    public async ValueTask OnFailedAsync(CudaBundleInstallFailed e, CancellationToken ct)
        => await hub.Clients.All.OnCudaBundleInstallFailed(
            new CudaBundleInstallFailedDto(e.Version, e.Error))
            .ConfigureAwait(false);
}
