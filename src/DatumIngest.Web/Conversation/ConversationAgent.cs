using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using DatumIngest.Models.Llama;
using DatumIngest.Web.Hosting;
using DatumIngest.Web.Llm;
using DatumIngest.Web.Messages;

namespace DatumIngest.Web.Conversation;

// Per-conversation chat loop. One Session per conversation owns its own
// in-memory accumulator + active-send CTS; the agent is a singleton that
// routes calls by conversationId.
//
// Rehydration: a Session's accumulator is lazy — null until the first
// SendAsync or after ReloadAsync. The rebuild reads every persisted
// message via IMessageGraph.ReadHistoryAsync, replays each turn through
// the chat template, and resets-to-summary on each checkpoint row. So a
// fresh process picks up where the previous one left off, and the user
// can drop a "reload" to pick up DB edits made while the process was
// running (e.g. via the SQL panel).
//
// Cancellation: same shape as before — each SendAsync registers an
// internal CTS on its Session, CancelActive triggers it, and the partial
// assistant response is persisted regardless. The CTS field is
// belt-and-braces last-writer-wins; two concurrent sends on the same
// conversation is a UI bug.
internal sealed class ConversationAgent : IConversationAgent
{
    private const string BuiltInSystemPrompt =
        "You are an assistant inside DatumIngest, a local data tool, but you can " +
        "help with anything the user asks — coding, writing, general questions, " +
        "not just data. " +
        "Default to short answers: one or two sentences for simple questions, a " +
        "short paragraph when it genuinely needs it. The user will ask for more if " +
        "they want it; don't preempt. " +
        "When writing SQL, put the query in a fenced ```sql block and add at most " +
        "one sentence of context. Skip column-by-column walkthroughs unless asked.";

    private readonly LlmDriverHolder _holder;
    private readonly IMessageGraph _graph;
    private readonly IConversationRegistry _registry;
    private readonly string _systemPrompt;
    private readonly ConcurrentDictionary<long, Session> _sessions = new();

    public ConversationAgent(
        LlmDriverHolder holder,
        IMessageGraph graph,
        IConversationRegistry registry,
        WebHostOptions options)
    {
        _holder = holder;
        _graph = graph;
        _registry = registry;
        _systemPrompt = options.SystemPrompt ?? BuiltInSystemPrompt;
    }

    public async IAsyncEnumerable<string> SendAsync(
        long conversationId,
        string userContent,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // First send pays the model-load cost — surfaces here as a delay
        // before the first chunk streams. Subsequent sends hit the cached
        // driver and return immediately.
        ILlmDriver driver = await _holder.GetAsync(ct).ConfigureAwait(false);
        LlamaChatTemplate template = driver.Template;

        Session session = _sessions.GetOrAdd(conversationId, _ => new Session());

        // Lazy rebuild from DB. Held outside the per-session lock because
        // it awaits and locks don't span awaits cleanly. The dictionary
        // entry is in place either way; two concurrent first-sends both
        // see the null accumulator and both rebuild, but the second's
        // result overwrites the first — same content, just wasted work.
        if (session.Accumulator is null)
        {
            string rebuilt = await BuildAccumulatorFromHistoryAsync(
                conversationId, template, ct).ConfigureAwait(false);
            lock (session.Lock)
            {
                session.Accumulator ??= rebuilt;
            }
        }

        // Link the caller's token with our own internal CTS so CancelActive
        // and ConnectionAborted both terminate the same enumeration.
        using CancellationTokenSource internalCts = new();
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(ct, internalCts.Token);
        session.ActiveCts = internalCts;

        string prompt;
        lock (session.Lock)
        {
            session.Accumulator += template.WrapMessage("user", userContent);
            prompt = session.Accumulator + template.AssistantTurn;
        }

        // Persist the user turn before we start generating. Durable even
        // if generation is cancelled or fails — we'd rather have an
        // orphan user message than lose the input.
        await _graph.AppendAsync(
            conversationId,
            new MessageDraft(
                Role: "user",
                Content: userContent,
                InputTokens: driver.CountTokens(userContent)),
            linked.Token).ConfigureAwait(false);

        StringBuilder assistantBuilder = new();
        bool wasCancelled = false;
        try
        {
            await foreach (string chunk in driver
                .StreamAsync(prompt, linked.Token)
                .ConfigureAwait(false))
            {
                assistantBuilder.Append(chunk);
                yield return chunk;
            }
        }
        finally
        {
            wasCancelled = linked.Token.IsCancellationRequested;
            session.ActiveCts = null;
        }

        // Always finalize: persist the (possibly partial) assistant turn and
        // extend the in-memory accumulator. CancellationToken.None on the
        // finalize INSERT so we don't lose the partial on the same
        // cancellation that stopped us.
        string assistantContent = StripTrailingStop(assistantBuilder.ToString(), template.StopSequences);

        if (assistantContent.Length > 0)
        {
            lock (session.Lock)
            {
                session.Accumulator += template.WrapMessage("assistant", assistantContent);
            }

            await _graph.AppendAsync(
                conversationId,
                new MessageDraft(
                    Role: "assistant",
                    Content: assistantContent,
                    Model: driver.ModelName,
                    OutputTokens: driver.CountTokens(assistantContent)),
                CancellationToken.None).ConfigureAwait(false);
        }

        await _registry.TouchAsync(conversationId, CancellationToken.None).ConfigureAwait(false);

        if (wasCancelled)
        {
            ct.ThrowIfCancellationRequested();
            throw new OperationCanceledException();
        }
    }

    public void CancelActive(long conversationId)
    {
        if (!_sessions.TryGetValue(conversationId, out Session? session)) return;
        CancellationTokenSource? cts = session.ActiveCts;
        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { /* CTS finalize race — benign */ }
    }

    public Task ReloadAsync(long conversationId, CancellationToken ct)
    {
        // Drop the session entirely; the next SendAsync rebuilds the
        // accumulator from DB. Cheaper than rebuilding here — the user
        // may reload then start a new conversation, in which case the
        // rebuild for the old one would be wasted.
        _sessions.TryRemove(conversationId, out _);
        _ = ct;
        return Task.CompletedTask;
    }

    private async Task<string> BuildAccumulatorFromHistoryAsync(
        long conversationId,
        LlamaChatTemplate template,
        CancellationToken ct)
    {
        IReadOnlyList<MessageRecord> history = await _graph
            .ReadHistoryAsync(conversationId, ct)
            .ConfigureAwait(false);

        StringBuilder sb = new();
        sb.Append(template.Open);
        sb.Append(template.WrapMessage("system", _systemPrompt));

        foreach (MessageRecord msg in history)
        {
            switch (msg.Kind)
            {
                case "hidden":
                    continue;
                case "checkpoint":
                    // Reset: drop everything we accumulated, restart with
                    // system prompt + the checkpoint summary as a synthetic
                    // system addendum. Turns after this row append below.
                    sb.Clear();
                    sb.Append(template.Open);
                    sb.Append(template.WrapMessage(
                        "system",
                        _systemPrompt + "\n\nSummary of earlier conversation:\n" + msg.Content));
                    break;
                default:
                    sb.Append(template.WrapMessage(msg.Role, msg.Content));
                    break;
            }
        }

        return sb.ToString();
    }

    private static string StripTrailingStop(string text, IReadOnlyList<string> stopSequences)
    {
        foreach (string stop in stopSequences)
        {
            if (stop.Length > 0 && text.EndsWith(stop, StringComparison.Ordinal))
            {
                return text[..^stop.Length];
            }
        }
        return text;
    }

    private sealed class Session
    {
        public readonly object Lock = new();
        public string? Accumulator;
        public CancellationTokenSource? ActiveCts;
    }
}
