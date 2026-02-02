using TypedSignalR.Client;

namespace DatumIngest.Web.Hubs;

// Methods clients invoke on the server.
[Hub]
public interface IStreamHub
{
    Task Ping(string message);

    // Posts a user turn and streams the assistant response back via
    // IStreamHubClient.OnToken / OnComplete / OnError. The Task itself
    // resolves when generation is finished and both turns are persisted.
    Task SendMessage(string content);

    // Cancels the active SendMessage if any. Whatever partial response was
    // generated up to the cancel point is persisted to the message graph —
    // the truncation is visible in history. Returns immediately; the in-
    // flight SendMessage observes cancellation on its next token loop.
    Task CancelMessage();
}
