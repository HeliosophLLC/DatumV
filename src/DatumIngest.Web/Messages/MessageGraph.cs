using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Web.Messages;

// INSERT-only wrapper over TableCatalog. Uses ParameterBinder so user-supplied
// content (which may contain quotes, newlines, anything) doesn't have to be
// escaped — the binder substitutes $name parameters with proper literals at
// the AST level before planning.
internal sealed class MessageGraph : IMessageGraph
{
    private const string InsertSql =
        "INSERT INTO messages (conversation_id, kind, role, content, model, input_tokens, output_tokens) " +
        "VALUES ($conversation_id, $kind, $role, $content, $model, $input_tokens, $output_tokens)";

    private readonly TableCatalog _catalog;

    public MessageGraph(TableCatalog catalog)
    {
        _catalog = catalog;
    }

    public async Task AppendAsync(long conversationId, MessageDraft draft, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        Dictionary<string, ParameterValue> parameters = new()
        {
            ["conversation_id"] = new InlineParameter(DataValue.FromInt64(conversationId)),
            ["kind"] = new StringParameter(draft.Kind),
            ["role"] = new StringParameter(draft.Role),
            ["content"] = new StringParameter(draft.Content),
            ["model"] = draft.Model is null
                ? new InlineParameter(DataValue.Null(DataKind.String))
                : new StringParameter(draft.Model),
            ["input_tokens"] = draft.InputTokens is null
                ? new InlineParameter(DataValue.Null(DataKind.Int32))
                : new InlineParameter(DataValue.FromInt32(draft.InputTokens.Value)),
            ["output_tokens"] = draft.OutputTokens is null
                ? new InlineParameter(DataValue.Null(DataKind.Int32))
                : new InlineParameter(DataValue.FromInt32(draft.OutputTokens.Value)),
        };

        Statement statement = SqlParser.ParseStatement(InsertSql);
        Statement bound = ParameterBinder.Bind(statement, parameters);
        // ExecuteStatementAsync runs INSERT inline and returns EmptyQueryPlan
        // — no need to iterate the result.
        await _catalog.ExecuteStatementAsync(bound).ConfigureAwait(false);
    }
}
