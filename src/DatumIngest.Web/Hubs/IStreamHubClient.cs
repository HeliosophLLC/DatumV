using TypedSignalR.Client;

namespace Heliosoph.DatumV.Web.Hubs;

// Methods the server invokes on connected clients.
[Receiver]
public interface IStreamHubClient
{
    Task OnPong(string message);

    // Streamed chat events. OnToken fires per LLM output chunk (typically
    // one or a few tokens). OnComplete fires after the final chunk + DB
    // persistence. OnError fires (instead of OnComplete) when generation
    // fails — client should render the message and reset its streaming
    // state.
    Task OnToken(string content);
    Task OnComplete();
    Task OnError(string message);

    // Model-download lifecycle events. Broadcast to all clients (single-
    // user desktop today; future multi-tenant SaaS will scope by group).
    // Parameters are Web-side DTOs (with [Tapper.TranspilationSource]),
    // populated by SignalRDownloadProgressReporter from the core
    // ModelLibrary records.
    Task OnModelDownloadStarted(ModelDownloadStartedDto started);
    Task OnModelDownloadProgress(ModelDownloadProgressDto progress);
    Task OnModelDownloadComplete(ModelDownloadCompleteDto complete);
    // Emitted only for catalog entries with installSql set. Lifecycle:
    // OnModelDownloadComplete -> OnModelInstalling -> OnModelInstalled (or
    // OnModelDownloadFailed if the SQL install throws).
    Task OnModelInstalling(ModelInstallingDto installing);
    Task OnModelInstalled(ModelInstalledDto installed);
    Task OnModelDownloadFailed(ModelDownloadFailedDto failed);

    // Python-environment install events. Fired during venv provisioning
    // for kind:"python" catalog entries, after OnModelInstalling. The
    // uv + python install events are machine-scoped (one-time setup per
    // host); venv events carry VenvName == catalog id so the client can
    // surface sub-steps on the specific model card that's installing.
    // Populated by SignalRPythonEnvironmentReporter from the core
    // Heliosoph.DatumV.Models.Python event records.
    Task OnUvDownloadStarted(UvDownloadStartedDto started);
    Task OnUvDownloadProgress(UvDownloadProgressDto progress);
    Task OnUvDownloadComplete(UvDownloadCompleteDto complete);
    Task OnPythonInstallStarted(PythonInstallStartedDto started);
    Task OnPythonInstallProgress(PythonInstallProgressDto progress);
    Task OnPythonInstallComplete(PythonInstallCompleteDto complete);
    Task OnVenvInstallStarted(VenvInstallStartedDto started);
    Task OnVenvInstallProgress(VenvInstallProgressDto progress);
    Task OnVenvInstallComplete(VenvInstallCompleteDto complete);
    Task OnPythonEnvironmentFailed(PythonEnvironmentFailedDto failed);

    // Dataset-download lifecycle events. Broadcast to all clients (same
    // single-user-desktop assumption as the model events). Populated by
    // SignalRDatasetDownloadProgressReporter from the core
    // Heliosoph.DatumV.DatasetLibrary event records. Lifecycle:
    //   OnDatasetDownloadStarted -> N x OnDatasetDownloadProgress ->
    //   OnDatasetDownloadComplete -> N x (OnDatasetIngesting ->
    //   OnDatasetTableIngested) -> OnDatasetInstalled.
    // OnDatasetDownloadFailed replaces any later event on failure.
    Task OnDatasetDownloadStarted(DatasetDownloadStartedDto started);
    Task OnDatasetDownloadProgress(DatasetDownloadProgressDto progress);
    Task OnDatasetDownloadComplete(DatasetDownloadCompleteDto complete);
    Task OnDatasetIngesting(DatasetIngestingDto ingesting);
    Task OnDatasetIngestProgress(DatasetIngestProgressDto progress);
    Task OnDatasetTableIngested(DatasetTableIngestedDto ingested);
    Task OnDatasetInstalled(DatasetInstalledDto installed);
    Task OnDatasetDownloadFailed(DatasetDownloadFailedDto failed);
}
