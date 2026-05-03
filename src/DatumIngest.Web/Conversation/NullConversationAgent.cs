using System.Runtime.CompilerServices;

namespace DatumIngest.Web.Conversation;

// Stand-in IConversationAgent for builds where the chat surface is
// intentionally disabled (see the commented LLM wiring block in
// WebHostExtensions). StreamHub takes IConversationAgent as a
// constructor dependency, and SignalR instantiates StreamHub on every
// incoming connection — including connections that only carry
// server→client download-progress pushes via IHubContext, which
// nevertheless need the hub class to be constructable. Without a
// registered IConversationAgent, every WebSocket connect to /hubs/stream
// fails with InvalidOperationException during OnConnectedAsync and the
// server closes with an error, taking the download-progress channel
// down with it.
//
// Method semantics:
//   - SendAsync throws on first iteration so a misrouted UI call gets a
//     clear "chat is disabled" error rather than silently hanging.
//   - The lifecycle methods (Cancel/Reload/Compact) are no-ops; they're
//     idempotent in the real ConversationAgent and harmless to no-op
//     here.
internal sealed class NullConversationAgent : IConversationAgent
{
    public async IAsyncEnumerable<string> SendAsync(
        long conversationId,
        string userContent,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Make the method a proper async iterator so the surrounding
        // StreamHub.SendMessage flow (which awaits the enumeration) hits
        // the throw rather than getting a never-yielding sequence.
        await Task.CompletedTask;
        throw new InvalidOperationException(
            "Chat is disabled in this build. The LLM/conversation wiring in "
            + "WebHostExtensions is commented out — re-register a real "
            + "IConversationAgent when chat is ready to ship.");
#pragma warning disable CS0162 // unreachable
        yield break;
#pragma warning restore CS0162
    }

    public void CancelActive(long conversationId)
    {
        // Real agent: cancels any in-flight SendAsync for the id. With
        // no chat, there's nothing in flight — silent no-op.
    }

    public Task ReloadAsync(long conversationId, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<int> CompactAsync(long conversationId, CancellationToken ct) =>
        Task.FromResult(0);
}
