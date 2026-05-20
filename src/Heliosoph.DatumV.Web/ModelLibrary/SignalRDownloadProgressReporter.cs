using Heliosoph.DatumV.ModelLibrary;
using Heliosoph.DatumV.Web.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Heliosoph.DatumV.Web.ModelLibrary;

// Adapter that bridges the core IDownloadProgressReporter to the Web's
// SignalR hub. Receives core event records from ModelDownloadService,
// converts them to wire DTOs (which carry [Tapper.TranspilationSource]),
// and broadcasts to all connected clients. Single-user desktop today;
// future multi-tenant deployments will scope by group instead of All.
internal sealed class SignalRDownloadProgressReporter(
    IHubContext<StreamHub, IStreamHubClient> hub) : IDownloadProgressReporter
{
    public async ValueTask OnStartedAsync(ModelDownloadStarted e, CancellationToken ct)
        => await hub.Clients.All.OnModelDownloadStarted(
            new ModelDownloadStartedDto(e.ModelId, e.FileCount, e.TotalBytes))
            .ConfigureAwait(false);

    public async ValueTask OnProgressAsync(ModelDownloadProgress e, CancellationToken ct)
        => await hub.Clients.All.OnModelDownloadProgress(
            new ModelDownloadProgressDto(
                e.ModelId, e.CurrentFile, e.FileIndex, e.FileCount,
                e.BytesReadInFile, e.BytesTotalInFile,
                e.BytesReadTotal, e.BytesTotalAcrossModel))
            .ConfigureAwait(false);

    public async ValueTask OnCompleteAsync(ModelDownloadComplete e, CancellationToken ct)
        => await hub.Clients.All.OnModelDownloadComplete(
            new ModelDownloadCompleteDto(e.ModelId))
            .ConfigureAwait(false);

    public async ValueTask OnInstallingAsync(ModelInstalling e, CancellationToken ct)
        => await hub.Clients.All.OnModelInstalling(
            new ModelInstallingDto(e.ModelId))
            .ConfigureAwait(false);

    public async ValueTask OnInstalledAsync(ModelInstalled e, CancellationToken ct)
        => await hub.Clients.All.OnModelInstalled(
            new ModelInstalledDto(e.ModelId))
            .ConfigureAwait(false);

    public async ValueTask OnFailedAsync(ModelDownloadFailed e, CancellationToken ct)
        => await hub.Clients.All.OnModelDownloadFailed(
            new ModelDownloadFailedDto(e.ModelId, e.Error))
            .ConfigureAwait(false);
}
