using DatumIngest.Catalog;
using DatumIngest.Data;
using DatumIngest.Model;

namespace DatumIngest.Web.Messages;

// INSERT-only wrapper over TableCatalog via the InProcessDatumDb*
// command/reader surface. Parameters round-trip through the command's
// Parameters collection (which the command hands to ParameterBinder)
// so user-supplied content (which may contain quotes, newlines,
// anything) doesn't need to be escaped — substitution happens at the
// AST level before planning.
internal sealed class MessageGraph : IMessageGraph
{
    private const string InsertSql =
        "INSERT INTO messages (conversation_id, kind, role, content, model, input_tokens, output_tokens) " +
        "VALUES ($conversation_id, $kind, $role, $content, $model, $input_tokens, $output_tokens)";

    private const string SelectHistorySql =
        "SELECT id, conversation_id, kind, role, content, model, " +
        "       input_tokens, output_tokens, created_at " +
        "FROM messages WHERE conversation_id = $conversation_id ORDER BY id ASC";

    private readonly TableCatalog _catalog;

    public MessageGraph(TableCatalog catalog)
    {
        _catalog = catalog;
    }

    public async Task AppendAsync(long conversationId, MessageDraft draft, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using InProcessDatumDbConnection conn = new(_catalog);
        using InProcessDatumDbCommand cmd = conn.CreateCommand(InsertSql);
        cmd.Parameters
            .AddInt64("conversation_id", conversationId)
            .AddString("kind", draft.Kind)
            .AddString("role", draft.Role)
            .AddString("content", draft.Content)
            .AddString("model", draft.Model)
            .AddInt32("input_tokens", draft.InputTokens)
            .AddInt32("output_tokens", draft.OutputTokens);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MessageRecord>> ReadHistoryAsync(
        long conversationId,
        CancellationToken ct)
    {
        using InProcessDatumDbConnection conn = new(_catalog);
        using InProcessDatumDbCommand cmd = conn.CreateCommand(SelectHistorySql);
        cmd.Parameters.AddInt64("conversation_id", conversationId);

        await using InProcessDatumDbReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        List<MessageRecord> results = new();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new MessageRecord(
                Id: reader.GetInt64(0),
                ConversationId: reader.GetInt64(1),
                Kind: reader.GetString(2),
                Role: reader.GetString(3),
                Content: reader.GetString(4),
                Model: reader.IsDBNull(5) ? null : reader.GetString(5),
                InputTokens: reader.IsDBNull(6) ? null : reader.GetInt32(6),
                OutputTokens: reader.IsDBNull(7) ? null : reader.GetInt32(7),
                CreatedAt: reader.GetValue(8).AsTimestamp()));
        }
        return results;
    }
}
