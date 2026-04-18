using DatumIngest.Web.Conversation;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace DatumIngest.Web.Hubs;

public sealed class StreamHub : Hub<IStreamHubClient>, IStreamHub
{
    private readonly IConversationAgent _agent;
    private readonly ILogger<StreamHub> _logger;

    public StreamHub(IConversationAgent agent, ILogger<StreamHub> logger)
    {
        _agent = agent;
        _logger = logger;
    }

    public Task Ping(string message) =>
        Clients.Caller.OnPong($"pong: {message}");

    // Per-connection cancellation: SignalR aborts Context.ConnectionAborted
    // when the client disconnects, which propagates through the agent's
    // SendAsync and stops generation. We don't need to push anything on
    // disconnect — there's no one listening.
    public async Task SendMessage(long conversationId, string content)
    {
        CancellationToken ct = Context.ConnectionAborted;
        try
        {
            await foreach (string token in _agent.SendAsync(conversationId, content, ct).ConfigureAwait(false))
            {
                await Clients.Caller.OnToken(token).ConfigureAwait(false);
            }
            await Clients.Caller.OnComplete().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Two cancellation paths:
            //   1. Client disconnect (Context.ConnectionAborted) — there's
            //      nobody to OnComplete to; SignalR will tear down.
            //   2. Server-side CancelMessage — the connection is still up
            //      and the client expects OnComplete so it can transition
            //      to idle. The partial assistant turn is already persisted
            //      by the agent's finally block.
            // We try OnComplete unconditionally and swallow if it fails.
            try { await Clients.Caller.OnComplete().ConfigureAwait(false); }
            catch { /* connection broken — nothing to do */ }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chat turn failed.");
            try
            {
                await Clients.Caller.OnError(ex.Message).ConfigureAwait(false);
            }
            catch
            {
                // OnError itself may fail if the connection is broken;
                // swallow — we already logged the underlying error.
            }
        }
    }

    public Task CancelMessage(long conversationId)
    {
        _agent.CancelActive(conversationId);
        return Task.CompletedTask;
    }

    // Drops the in-memory accumulator for the conversation so the next
    // SendAsync rebuilds from the messages table. Use this after editing
    // rows directly (e.g. via the SQL panel) so the model sees the new
    // state on the following turn.
    public Task ReloadConversation(long conversationId) =>
        _agent.ReloadAsync(conversationId, Context.ConnectionAborted);
}
