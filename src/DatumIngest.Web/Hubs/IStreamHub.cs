using TypedSignalR.Client;

namespace DatumIngest.Web.Hubs;

// Methods clients invoke on the server.
[Hub]
public interface IStreamHub
{
    Task Ping(string message);

    // Posts a user turn against `conversationId` and streams the assistant
    // response back via IStreamHubClient.OnToken / OnComplete / OnError.
    // The Task itself resolves when generation is finished and both turns
    // are persisted.
    Task SendMessage(long conversationId, string content);

    // Cancels the active SendMessage on `conversationId` if any. Whatever
    // partial response was generated up to the cancel point is persisted —
    // the truncation is visible in history. Returns immediately; the in-
    // flight SendMessage observes cancellation on its next token loop.
    Task CancelMessage(long conversationId);

    // Drops the agent's in-memory accumulator for `conversationId` so the
    // next SendMessage rebuilds it from the persisted message history.
    // Pair with hand-edits to the messages table when you want the model
    // to see the new state on the following turn.
    Task ReloadConversation(long conversationId);
}
