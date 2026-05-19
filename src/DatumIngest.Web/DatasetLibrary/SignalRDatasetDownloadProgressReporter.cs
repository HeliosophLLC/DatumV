using Heliosoph.DatumV.DatasetLibrary;
using Heliosoph.DatumV.Web.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Heliosoph.DatumV.Web.DatasetLibrary;

// Adapter that bridges the core IDatasetDownloadProgressReporter to the
// Web's SignalR hub. Receives core event records from DatasetDownloadService,
// converts them to wire DTOs (which carry [Tapper.TranspilationSource]),
// and broadcasts to all connected clients. Single-user desktop today;
// future multi-tenant deployments will scope by group instead of All.
//
// Parallel to SignalRDownloadProgressReporter (the model-side adapter) —
// distinct types throughout so the UI can subscribe to dataset events
// without a kind discriminator on every event.
internal sealed class SignalRDatasetDownloadProgressReporter(
    IHubContext<StreamHub, IStreamHubClient> hub) : IDatasetDownloadProgressReporter
{
    public async ValueTask OnStartedAsync(DatasetDownloadStarted e, CancellationToken ct)
        => await hub.Clients.All.OnDatasetDownloadStarted(
            new DatasetDownloadStartedDto(e.DatasetId, e.FileCount, e.TotalBytes))
            .ConfigureAwait(false);

    public async ValueTask OnProgressAsync(DatasetDownloadProgress e, CancellationToken ct)
        => await hub.Clients.All.OnDatasetDownloadProgress(
            new DatasetDownloadProgressDto(
                e.DatasetId, e.CurrentFile, e.FileIndex, e.FileCount,
                e.BytesReadInFile, e.BytesTotalInFile,
                e.BytesReadTotal, e.BytesTotalAcrossDataset))
            .ConfigureAwait(false);

    public async ValueTask OnCompleteAsync(DatasetDownloadComplete e, CancellationToken ct)
        => await hub.Clients.All.OnDatasetDownloadComplete(
            new DatasetDownloadCompleteDto(e.DatasetId))
            .ConfigureAwait(false);

    public async ValueTask OnIngestingAsync(DatasetIngesting e, CancellationToken ct)
        => await hub.Clients.All.OnDatasetIngesting(
            new DatasetIngestingDto(e.DatasetId, e.CurrentTable, e.JobIndex, e.JobCount))
            .ConfigureAwait(false);

    public async ValueTask OnIngestProgressAsync(DatasetIngestProgress e, CancellationToken ct)
        => await hub.Clients.All.OnDatasetIngestProgress(
            new DatasetIngestProgressDto(e.DatasetId, e.CurrentTable, e.RowsWrittenSoFar))
            .ConfigureAwait(false);

    public async ValueTask OnTableIngestedAsync(DatasetTableIngested e, CancellationToken ct)
        => await hub.Clients.All.OnDatasetTableIngested(
            new DatasetTableIngestedDto(e.DatasetId, e.Table, e.RowsWritten, e.BytesWritten))
            .ConfigureAwait(false);

    public async ValueTask OnInstalledAsync(DatasetInstalled e, CancellationToken ct)
        => await hub.Clients.All.OnDatasetInstalled(
            new DatasetInstalledDto(e.DatasetId))
            .ConfigureAwait(false);

    public async ValueTask OnFailedAsync(DatasetDownloadFailed e, CancellationToken ct)
        => await hub.Clients.All.OnDatasetDownloadFailed(
            new DatasetDownloadFailedDto(e.DatasetId, e.Error))
            .ConfigureAwait(false);
}
