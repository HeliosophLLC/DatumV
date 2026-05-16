using DatumIngest.Catalog;
using DatumIngest.Data;
using DatumIngest.Model;

namespace DatumIngest.Web.Conversation;

// Reads / writes against the `conversations` table via the
// InProcessDatumDb* command/reader surface. INSERTs use RETURNING so we
// get the IDENTITY-assigned id back without a follow-up SELECT.
//
// EnsureDefaultAsync is the only path the agent exercises by default:
// it picks the newest conversation by id if any exist, otherwise
// creates a fresh one. Id is monotonic via IDENTITY, so "highest id =
// newest" — the v1 schema doesn't carry an updated_at column, which
// means "most recently used" isn't surfaced today. A future migration
// can add the column back and switch the ordering here.
internal sealed class ConversationRegistry : IConversationRegistry
{
    private readonly TableCatalog _catalog;

    public ConversationRegistry(TableCatalog catalog)
    {
        _catalog = catalog;
    }

    public async Task<long> EnsureDefaultAsync(CancellationToken ct)
    {
        using InProcessDatumDbConnection conn = new(_catalog);
        using InProcessDatumDbCommand cmd = conn.CreateCommand(
            "SELECT id FROM conversations ORDER BY id DESC LIMIT 1");

        DataValue? scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (scalar is { IsNull: false } cell) return cell.AsInt64();

        return await CreateAsync(title: null, model: null, ct).ConfigureAwait(false);
    }

    public async Task<long> CreateAsync(string? title, string? model, CancellationToken ct)
    {
        using InProcessDatumDbConnection conn = new(_catalog);
        using InProcessDatumDbCommand cmd = conn.CreateCommand(
            "INSERT INTO conversations (title, model) VALUES ($title, $model) RETURNING id");
        cmd.Parameters
            .AddString("title", title)
            .AddString("model", model);

        DataValue? id = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (id is null)
        {
            throw new InvalidOperationException(
                "INSERT INTO conversations ... RETURNING id yielded no rows.");
        }
        return id.Value.AsInt64();
    }

    public async Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken ct)
    {
        using InProcessDatumDbConnection conn = new(_catalog);
        using InProcessDatumDbCommand cmd = conn.CreateCommand(
            "SELECT id, title, model, created_at FROM conversations ORDER BY id DESC");

        List<ConversationSummary> results = new();
        await using InProcessDatumDbReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(ReadSummary(reader));
        }
        return results;
    }

    public async Task<ConversationSummary?> GetAsync(long id, CancellationToken ct)
    {
        using InProcessDatumDbConnection conn = new(_catalog);
        using InProcessDatumDbCommand cmd = conn.CreateCommand(
            "SELECT id, title, model, created_at FROM conversations WHERE id = $id");
        cmd.Parameters.AddInt64("id", id);

        await using InProcessDatumDbReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return ReadSummary(reader);
        }
        return null;
    }

    public async Task SetTitleAsync(long id, string? title, CancellationToken ct)
    {
        using InProcessDatumDbConnection conn = new(_catalog);
        using InProcessDatumDbCommand cmd = conn.CreateCommand(
            "UPDATE conversations SET title = $title WHERE id = $id");
        cmd.Parameters
            .AddInt64("id", id)
            .AddString("title", title);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static ConversationSummary ReadSummary(InProcessDatumDbReader reader)
    {
        long id = reader.GetInt64(0);
        string? title = reader.IsDBNull(1) ? null : reader.GetString(1);
        string? model = reader.IsDBNull(2) ? null : reader.GetString(2);
        DateTime createdAt = reader.GetValue(3).AsTimestamp();
        return new ConversationSummary(id, title, model, createdAt);
    }
}
