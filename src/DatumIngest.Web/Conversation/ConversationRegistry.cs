using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Web.Conversation;

// Reads / writes against the `conversations` table. INSERTs use RETURNING
// so we get the IDENTITY-assigned id back without a follow-up SELECT.
//
// EnsureDefaultAsync is the only path the agent exercises today: it picks
// the most-recently-touched row if any exist, otherwise creates a fresh
// one. The UI in the next commits will start calling CreateAsync / ListAsync.
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
            .PlanAsync("SELECT id FROM conversations ORDER BY updated_at DESC LIMIT 1")
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
                "SELECT id, title, model, created_at, updated_at FROM conversations " +
                "ORDER BY updated_at DESC")
            .ConfigureAwait(false);

        List<ConversationSummary> results = new();
        await foreach (RowBatch batch in plan.ExecuteAsync(ct).ConfigureAwait(false))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                results.Add(ReadSummary(batch[i]));
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
            "SELECT id, title, model, created_at, updated_at FROM conversations WHERE id = $id");
        Statement bound = ParameterBinder.Bind(statement, parameters);

        IQueryPlan plan = await _catalog.ExecuteStatementAsync(bound).ConfigureAwait(false);
        await foreach (RowBatch batch in plan.ExecuteAsync(ct).ConfigureAwait(false))
        {
            if (batch.Count == 0) continue;
            return ReadSummary(batch[0]);
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
            "UPDATE conversations SET title = $title, updated_at = now() WHERE id = $id");
        Statement bound = ParameterBinder.Bind(statement, parameters);
        await _catalog.ExecuteStatementAsync(bound).ConfigureAwait(false);
        _ = ct;
    }

    public async Task TouchAsync(long id, CancellationToken ct)
    {
        Dictionary<string, ParameterValue> parameters = new()
        {
            ["id"] = new InlineParameter(DataValue.FromInt64(id)),
        };
        Statement statement = SqlParser.ParseStatement(
            "UPDATE conversations SET updated_at = now() WHERE id = $id");
        Statement bound = ParameterBinder.Bind(statement, parameters);
        await _catalog.ExecuteStatementAsync(bound).ConfigureAwait(false);
        _ = ct;
    }

    private static ConversationSummary ReadSummary(Row row)
    {
        long id = row[0].AsInt64();
        string? title = row[1].IsNull ? null : row[1].AsString();
        string? model = row[2].IsNull ? null : row[2].AsString();
        DateTime createdAt = row[3].AsTimestamp();
        DateTime updatedAt = row[4].AsTimestamp();
        return new ConversationSummary(id, title, model, createdAt, updatedAt);
    }
}
