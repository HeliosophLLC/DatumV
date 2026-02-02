namespace DatumIngest.Web.Conversation;

// Reactive loop: user input → model → response. Per project_proactive_not_reactive
// this is one of two loops; IProactiveAgent (event → maybe-message) is the other,
// not implemented v1.
public interface IConversationAgent
{
    // Persists the user turn, calls the LLM, streams response chunks back to
    // the caller, persists the final assistant turn. Yielded strings are raw
    // model output fragments — consumers concatenate to recover the full
    // response. Cancellation honored between chunks. On cancellation, the
    // partial assistant response collected so far is still persisted and
    // appended to the accumulator — the conversation captures "I started
    // saying X but you stopped me" rather than pretending the turn never
    // happened.
    IAsyncEnumerable<string> SendAsync(string userContent, CancellationToken ct);

    // Cancels the active SendAsync if any. No-op when no send is in flight.
    // Idempotent — calling twice during the same send still results in one
    // cancellation. Returns synchronously; the SendAsync caller observes
    // the cancellation on its next iterator step.
    void CancelActive();
}
