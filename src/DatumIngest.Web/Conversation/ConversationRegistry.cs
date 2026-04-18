using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Web.Conversation;

// Reads / writes against the `conversations` table. INSERTs use RETURNING
// so we get the IDENTITY-assigned id back without a follow-up SELECT.
//
// EnsureDefaultAsync is the only path the agent exercises by default: it
// picks the newest conversation by id if any exist, otherwise creates a
// fresh one. Id is monotonic via IDENTITY, so "highest id = newest" —
// the v1 schema doesn't carry an updated_at column, which means "most
// recently used" isn't surfaced today. A future migration can add the
// column back and switch the ordering here.
internal sealed class ConversationRegistry : IConversationRegistry
{
    private readonly TableCatalog _catalog;

    public ConversationRegistry(TableCatalog catalog)
    {
        _catalog = catalog;
    }

    public async Task<long> EnsureDefaultAsync(CancellationToken ct)
    {
        IQueryPlan selectPlan = await _catalog
            .PlanAsync("SELECT id FROM conversations ORDER BY id DESC LIMIT 1")
            .ConfigureAwait(false);

        await foreach (RowBatch batch in selectPlan.ExecuteAsync(ct).ConfigureAwait(false))
        {
            if (batch.Count == 0) continue;
            DataValue cell = batch[0][0];
            if (!cell.IsNull) return cell.AsInt64();
        }

        return await CreateAsync(title: null, model: null, ct).ConfigureAwait(false);
    }

    public async Task<long> CreateAsync(string? title, string? model, CancellationToken ct)
    {
        Dictionary<string, ParameterValue> parameters = new()
        {
            ["title"] = title is null
                ? new InlineParameter(DataValue.Null(DataKind.String))
                : new StringParameter(title),
            ["model"] = model is null
                ? new InlineParameter(DataValue.Null(DataKind.String))
                : new StringParameter(model),
        };

        Statement statement = SqlParser.ParseStatement(
            "INSERT INTO conversations (title, model) VALUES ($title, $model) RETURNING id");
        Statement bound = ParameterBinder.Bind(statement, parameters);

        IQueryPlan plan = await _catalog
            .ExecuteStatementAsync(bound)
            .ConfigureAwait(false);

        await foreach (RowBatch batch in plan.ExecuteAsync(ct).ConfigureAwait(false))
        {
            if (batch.Count == 0) continue;
            return batch[0][0].AsInt64();
        }

        throw new InvalidOperationException(
            "INSERT INTO conversations ... RETURNING id yielded no rows.");
    }

    public async Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken ct)
    {
        IQueryPlan plan = await _catalog
            .PlanAsync(
                "SELECT id, title, model, created_at FROM conversations " +
                "ORDER BY id DESC")
            .ConfigureAwait(false);

        List<ConversationSummary> results = new();
        await foreach (RowBatch batch in plan.ExecuteAsync(ct).ConfigureAwait(false))
        {
            Arena arena = batch.Arena;
            for (int i = 0; i < batch.Count; i++)
            {
                results.Add(ReadSummary(batch[i], arena));
            }
        }
        return results;
    }

    public async Task<ConversationSummary?> GetAsync(long id, CancellationToken ct)
    {
        Dictionary<string, ParameterValue> parameters = new()
        {
            ["id"] = new InlineParameter(DataValue.FromInt64(id)),
        };
        Statement statement = SqlParser.ParseStatement(
            "SELECT id, title, model, created_at FROM conversations WHERE id = $id");
        Statement bound = ParameterBinder.Bind(statement, parameters);

        IQueryPlan plan = await _catalog.ExecuteStatementAsync(bound).ConfigureAwait(false);
        await foreach (RowBatch batch in plan.ExecuteAsync(ct).ConfigureAwait(false))
        {
            if (batch.Count == 0) continue;
            return ReadSummary(batch[0], batch.Arena);
        }
        return null;
    }

    public async Task SetTitleAsync(long id, string? title, CancellationToken ct)
    {
        Dictionary<string, ParameterValue> parameters = new()
        {
            ["id"] = new InlineParameter(DataValue.FromInt64(id)),
            ["title"] = title is null
                ? new InlineParameter(DataValue.Null(DataKind.String))
                : new StringParameter(title),
        };
        Statement statement = SqlParser.ParseStatement(
            "UPDATE conversations SET title = $title WHERE id = $id");
        Statement bound = ParameterBinder.Bind(statement, parameters);
        await _catalog.ExecuteStatementAsync(bound).ConfigureAwait(false);
        _ = ct;
    }

    private static ConversationSummary ReadSummary(Row row, Arena arena)
    {
        long id = row[0].AsInt64();
        string? title = row[1].IsNull ? null : row[1].AsString(arena);
        string? model = row[2].IsNull ? null : row[2].AsString(arena);
        DateTime createdAt = row[3].AsTimestamp();
        return new ConversationSummary(id, title, model, createdAt);
    }
}
