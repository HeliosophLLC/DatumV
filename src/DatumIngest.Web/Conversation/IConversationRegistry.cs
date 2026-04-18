namespace DatumIngest.Web.Conversation;

// Read/write surface for the `conversations` table. Keeps the agent ignorant
// of how rows are stored — it asks for an id, the registry hands one back.
//
// Until the UI grows a conversation picker, EnsureDefaultAsync is the only
// path actually exercised: the agent calls it on first send and reuses the
// returned id for the process lifetime. ListAsync / CreateAsync / GetAsync
// are wired now because they're cheap and the next commit lights them up.
public interface IConversationRegistry
{
    // Returns the id of an existing conversation if one exists, otherwise
    // inserts a new untitled conversation and returns its id. Cheap to call
    // repeatedly — the agent caches the result for the session.
    Task<long> EnsureDefaultAsync(CancellationToken ct);

    Task<long> CreateAsync(string? title, string? model, CancellationToken ct);

    Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken ct);

    Task<ConversationSummary?> GetAsync(long id, CancellationToken ct);

    // Sets the conversation's title. Used today only by the auto-title
    // path after the first assistant turn; the UI doesn't yet expose a
    // rename action. A null `title` clears the field — kept symmetric
    // with the column being nullable.
    Task SetTitleAsync(long id, string? title, CancellationToken ct);
}

// CreatedAt doubles as the recency sort key — the v1 schema doesn't
// track an updated_at column, so a "last touched" notion would need a
// follow-up migration. Id ordering is monotonic via IDENTITY, which
// matches "newest first by insertion" perfectly fine for the popover.
public sealed record ConversationSummary(
    long Id,
    string? Title,
    string? Model,
    DateTime CreatedAt);
