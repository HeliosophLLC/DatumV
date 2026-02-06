using DatumIngest.Web.ModelLibrary;
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
    // fails â€” client should render the message and reset its streaming
    // state.
    Task OnToken(string content);
    Task OnComplete();
    Task OnError(string message);

    // Model-download lifecycle events. Broadcast to all clients (single-
    // user desktop today; future multi-tenant SaaS will scope by group).
    // OnModelDownloadStarted fires once per install; OnModelDownloadProgress
    // streams throttled byte-count updates; OnModelDownloadComplete is the
    // success terminus; OnModelDownloadFailed is the error terminus.
    Task OnModelDownloadStarted(ModelDownloadStarted started);
    Task OnModelDownloadProgress(ModelDownloadProgress progress);
    Task OnModelDownloadComplete(ModelDownloadComplete complete);
    Task OnModelDownloadFailed(ModelDownloadFailed failed);
}
