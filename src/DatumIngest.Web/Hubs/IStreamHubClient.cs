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
}
