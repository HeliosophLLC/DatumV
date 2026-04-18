using System.Runtime.CompilerServices;
using System.Text;
using DatumIngest.Web.Hosting;
using DatumIngest.Web.Llm;
using DatumIngest.Web.Messages;

namespace DatumIngest.Web.Conversation;

// v1 chat loop: in-memory accumulator + LLM stream + DB persistence.
//
// Lifecycle: singleton per app. The accumulator starts with the system
// prompt at construction and grows with each turn. No DB rehydration on
// startup — fresh each session. When rehydration lands, the constructor
// will run a SCAN query (per project_message_graph_design) to rebuild the
// accumulator from persisted messages.
//
// Concurrency: a lock guards accumulator mutations. v1 is single-conversation
// single-user, so this is largely belt-and-braces — but it does protect
// against a UI bug that issues a second SendAsync before the first completes.
//
// Cancellation: each SendAsync registers its own CTS as the "active"
// cancellation source. CancelActive cancels whichever send is currently
// running; the SendAsync caller observes OperationCanceledException at the
// next iterator step. We deliberately persist the partial assistant
// response on cancel — the user sees what was being said, and the next
// turn carries that context.
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
    private readonly object _accumulatorLock = new();
    private string? _accumulator;
    private long? _conversationId;
    private CancellationTokenSource? _activeCts;

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
        string userContent,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // First send pays the model-load cost — surfaces here as a delay
        // before the first chunk streams. Subsequent sends hit the cached
        // driver and return immediately.
        ILlmDriver driver = await _holder.GetAsync(ct).ConfigureAwait(false);
        Models.Llama.LlamaChatTemplate template = driver.Template;

        // Resolve the conversation id once per process. The hub doesn't
        // expose a conversation picker yet, so every turn lands on the
        // default conversation (newest if any exist, else freshly created).
        // When the UI threads a conversationId through SendAsync the cache
        // here goes away — until then, this keeps persistence well-defined
        // without an eager registry call at startup.
        long conversationId = _conversationId
            ??= await _registry.EnsureDefaultAsync(ct).ConfigureAwait(false);

        // Link the caller's token with our own internal CTS so CancelActive
        // and ConnectionAborted both terminate the same enumeration. The
        // linked token is disposed in the finally; the internal CTS is
        // cleared from _activeCts there too.
        using CancellationTokenSource internalCts = new();
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(ct, internalCts.Token);
        _activeCts = internalCts;

        string prompt;
        lock (_accumulatorLock)
        {
            _accumulator ??= template.Open + template.WrapMessage("system", _systemPrompt);
            _accumulator += template.WrapMessage("user", userContent);
            prompt = _accumulator + template.AssistantTurn;
        }

        // Persist the user turn before we start generating. The user turn
        // is durable even if generation is cancelled or fails — we'd rather
        // have an orphan user message than lose the input.
        await _graph.AppendAsync(
            conversationId,
            new MessageDraft("user", userContent),
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
            _activeCts = null;
        }

        // Always finalize: persist the (possibly partial) assistant turn and
        // extend the in-memory accumulator. On cancel, this captures what
        // was being generated so the next turn carries the truncation as
        // context. CancellationToken.None on the finalize INSERT so we
        // don't lose the partial on the same cancellation that stopped us.
        string assistantContent = StripTrailingStop(assistantBuilder.ToString(), template.StopSequences);

        if (assistantContent.Length > 0)
        {
            lock (_accumulatorLock)
            {
                _accumulator += template.WrapMessage("assistant", assistantContent);
            }

            await _graph.AppendAsync(
                conversationId,
                new MessageDraft("assistant", assistantContent, driver.ModelName),
                CancellationToken.None).ConfigureAwait(false);
        }

        // Bump updated_at so the history list (next commit) sorts by recency.
        // Best-effort: persistence already succeeded, a missed touch is just
        // a slightly stale sort key.
        await _registry.TouchAsync(conversationId, CancellationToken.None).ConfigureAwait(false);

        if (wasCancelled)
        {
            // Surface cancellation to the caller so the hub can pivot to the
            // "cancelled" path without conflating it with a completed turn.
            // The partial state is already persisted above.
            ct.ThrowIfCancellationRequested();
            throw new OperationCanceledException();
        }
    }

    public void CancelActive()
    {
        // Snapshot under no lock: CTS Cancel is thread-safe, and the worst
        // case of a stale reference is a no-op cancel of a CTS that's
        // already done.
        CancellationTokenSource? cts = _activeCts;
        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { /* CTS finalize race — benign */ }
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
}
