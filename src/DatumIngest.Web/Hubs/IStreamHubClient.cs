using TypedSignalR.Client;

namespace DatumIngest.Web.Hubs;

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
}
