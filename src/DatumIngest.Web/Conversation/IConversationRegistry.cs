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

    // Bumps `updated_at` to now. Called by the agent after each turn so the
    // history list can sort by recency without a per-row JOIN against messages.
    Task TouchAsync(long id, CancellationToken ct);
}

public sealed record ConversationSummary(
    long Id,
    string? Title,
    string? Model,
    DateTime CreatedAt,
    DateTime UpdatedAt);
