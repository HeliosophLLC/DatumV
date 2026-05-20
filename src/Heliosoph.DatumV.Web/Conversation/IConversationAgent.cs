namespace Heliosoph.DatumV.Web.Conversation;

// Reactive loop: user input → model → response. Per project_proactive_not_reactive
// this is one of two loops; IProactiveAgent (event → maybe-message) is the other,
// not implemented v1.
public interface IConversationAgent
{
    // Persists the user turn, calls the LLM, streams response chunks back to
    // the caller, persists the final assistant turn against the given
    // conversation. Yielded strings are raw model output fragments —
    // consumers concatenate to recover the full response. Cancellation
    // honored between chunks. On cancellation, the partial assistant
    // response collected so far is still persisted and appended to the
    // accumulator.
    IAsyncEnumerable<string> SendAsync(long conversationId, string userContent, CancellationToken ct);

    // Cancels the active SendAsync on the given conversation if any. No-op
    // when no send is in flight for that id. Idempotent.
    void CancelActive(long conversationId);

    // Discards the in-memory accumulator for the given conversation. The
    // next SendAsync rebuilds it from the persisted message history — so
    // hand-edits made to the messages table (or earlier checkpoints) take
    // effect on the following turn.
    Task ReloadAsync(long conversationId, CancellationToken ct);

    // Generates a summary of every turn since the latest checkpoint (or
    // every turn, if none), persists it as a new checkpoint row, and
    // drops the session so the next send rebuilds the prompt with the
    // checkpoint summary in effect. The user still sees the full history
    // in the UI; the LLM only sees system + summary + post-checkpoint
    // turns. Returns the number of turns compacted (0 = nothing to do).
    Task<int> CompactAsync(long conversationId, CancellationToken ct);
}
