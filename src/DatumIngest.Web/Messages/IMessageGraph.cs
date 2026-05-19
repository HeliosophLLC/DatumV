namespace Heliosoph.DatumV.Web.Messages;

// v1 chat persistence over a flat `messages` table. Named for the eventual
// shape (per project_message_graph_design memory) so that when message_links
// land we don't have to chase down naming. AppendAsync is the only mutation
// surface today — reads happen in-process via the agent's accumulator. When
// rehydration arrives, GetAllAsync / WalkAncestorsAsync grow alongside.
public interface IMessageGraph
{
    Task AppendAsync(long conversationId, MessageDraft draft, CancellationToken ct);

    // Reads every message for the conversation in id order. Used by both
    // the agent's accumulator rebuild (post-reload, post-restart) and the
    // client's history fetch. Hidden rows are returned too — filtering is
    // the caller's job since "show in UI but skip in prompt" is the
    // expected semantic divide.
    Task<IReadOnlyList<MessageRecord>> ReadHistoryAsync(long conversationId, CancellationToken ct);
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

public sealed record MessageRecord(
    long Id,
    long ConversationId,
    string Kind,
    string Role,
    string Content,
    string? Model,
    int? InputTokens,
    int? OutputTokens,
    DateTime CreatedAt);
