namespace DatumIngest.Web.Messages;

// v1 chat persistence over a flat `messages` table. Named for the eventual
// shape (per project_message_graph_design memory) so that when message_links
// land we don't have to chase down naming. AppendAsync is the only mutation
// surface today — reads happen in-process via the agent's accumulator. When
// rehydration arrives, GetAllAsync / WalkAncestorsAsync grow alongside.
public interface IMessageGraph
{
    Task AppendAsync(long conversationId, MessageDraft draft, CancellationToken ct);
}

// `Kind` defaults to "turn"; checkpoint rows (compaction summaries) and
// hidden rows use the other two values. The agent today only writes turns;
// compaction will add checkpoint inserts in a later step.
public sealed record MessageDraft(
    string Role,
    string Content,
    string? Model = null,
    int? InputTokens = null,
    int? OutputTokens = null,
    string Kind = "turn");
