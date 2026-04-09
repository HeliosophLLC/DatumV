using DatumIngest.Models.Python;
using DatumIngest.Web.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace DatumIngest.Web.ModelLibrary;

// Adapter that bridges the core IPythonEnvironmentReporter to the Web's
// SignalR hub. Same shape as SignalRDownloadProgressReporter — receives
// core event records, converts to wire DTOs (Tapper-tagged for TS
// codegen), and broadcasts to Clients.All. Single-user desktop today.
internal sealed class SignalRPythonEnvironmentReporter(
    IHubContext<StreamHub, IStreamHubClient> hub) : IPythonEnvironmentReporter
{
    public async ValueTask OnUvDownloadStartedAsync(UvDownloadStarted e, CancellationToken ct)
        => await hub.Clients.All.OnUvDownloadStarted(
            new UvDownloadStartedDto(e.Version, e.TotalBytes))
            .ConfigureAwait(false);

    public async ValueTask OnUvDownloadProgressAsync(UvDownloadProgress e, CancellationToken ct)
        => await hub.Clients.All.OnUvDownloadProgress(
            new UvDownloadProgressDto(e.BytesDownloaded, e.TotalBytes))
            .ConfigureAwait(false);

    public async ValueTask OnUvDownloadCompleteAsync(UvDownloadComplete e, CancellationToken ct)
        => await hub.Clients.All.OnUvDownloadComplete(new UvDownloadCompleteDto())
            .ConfigureAwait(false);

    public async ValueTask OnPythonInstallStartedAsync(PythonInstallStarted e, CancellationToken ct)
        => await hub.Clients.All.OnPythonInstallStarted(
            new PythonInstallStartedDto(e.Version))
            .ConfigureAwait(false);

    public async ValueTask OnPythonInstallProgressAsync(PythonInstallProgress e, CancellationToken ct)
        => await hub.Clients.All.OnPythonInstallProgress(
            new PythonInstallProgressDto(e.Stage, e.BytesProcessed, e.TotalBytes))
            .ConfigureAwait(false);

    public async ValueTask OnPythonInstallCompleteAsync(PythonInstallComplete e, CancellationToken ct)
        => await hub.Clients.All.OnPythonInstallComplete(
            new PythonInstallCompleteDto(e.Version))
            .ConfigureAwait(false);

    public async ValueTask OnVenvInstallStartedAsync(VenvInstallStarted e, CancellationToken ct)
        => await hub.Clients.All.OnVenvInstallStarted(
            new VenvInstallStartedDto(e.VenvName, e.Requirements))
            .ConfigureAwait(false);

    public async ValueTask OnVenvInstallProgressAsync(VenvInstallProgress e, CancellationToken ct)
        => await hub.Clients.All.OnVenvInstallProgress(
            new VenvInstallProgressDto(e.VenvName, e.Stage, e.Detail))
            .ConfigureAwait(false);

    public async ValueTask OnVenvInstallCompleteAsync(VenvInstallComplete e, CancellationToken ct)
        => await hub.Clients.All.OnVenvInstallComplete(
            new VenvInstallCompleteDto(e.VenvName))
            .ConfigureAwait(false);

    public async ValueTask OnFailedAsync(PythonEnvironmentFailed e, CancellationToken ct)
        => await hub.Clients.All.OnPythonEnvironmentFailed(
            new PythonEnvironmentFailedDto(e.Stage, e.VenvNameOrEmpty, e.Error))
            .ConfigureAwait(false);
}
