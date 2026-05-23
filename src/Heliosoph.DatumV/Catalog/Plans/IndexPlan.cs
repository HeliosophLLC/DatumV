using System.Runtime.CompilerServices;
using Heliosoph.DatumV.Catalog.Executors;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Catalog.Plans;

/// <summary>
/// <see cref="StatementPlan"/> for the index-DDL + table-maintenance
/// family: <c>CREATE INDEX</c>, <c>DROP INDEX</c>, <c>REINDEX</c>, and
/// <c>ANALYZE</c>. All four touch a table's acceleration sidecars
/// (<c>.datum-cindex-{name}</c>, <c>.datum-index</c>, <c>.datum-manifest</c>)
/// with zero children and zero rows, so a single class with a kind
/// discriminator handles them.
/// </summary>
/// <remarks>
/// <para>
/// Construction is pure: building an <see cref="IndexPlan"/> never
/// mutates the catalog. The side effect (sidecar create / drop / rebuild
/// through <see cref="IndexExecutor"/> or <see cref="AnalyzeExecutor"/>)
/// only runs inside <see cref="ExecuteImplAsync"/> on iteration.
/// </para>
/// <para>
/// <b>Idempotency:</b> the apply runs at most once. Subsequent
/// <c>ExecuteAsync</c> calls throw — re-plan the statement to apply it
/// again.
/// </para>
/// </remarks>
internal sealed class IndexPlan : StatementPlan
{
    private readonly Statement _statement;
    private readonly string? _sourceText;
    private int _executed;

    private IndexPlan(
        TableCatalog catalog,
        Statement statement,
        string? sourceText,
        string operatorName,
        string details) : base(catalog)
    {
        ArgumentNullException.ThrowIfNull(statement);

        _statement = statement;
        _sourceText = sourceText;

        ExplainTree = new ExplainPlanNode
        {
            OperatorName = operatorName,
            Details = details,
            EstimatedRows = 0,
        };
        Kind = operatorName.ToLowerInvariant();
    }

    public override string Kind { get; }

    /// <summary>Builds a plan for <c>CREATE INDEX</c>.</summary>
    public static IndexPlan ForCreateIndex(
        TableCatalog catalog, CreateIndexStatement statement, string? sourceText)
    {
        ArgumentNullException.ThrowIfNull(statement);
        string tableQn = statement.SchemaName is null
            ? statement.TableName
            : $"{statement.SchemaName}.{statement.TableName}";
        string prefix = statement.IsUnique ? "UNIQUE " : "";
        string method = statement.Method is null ? "" : $" USING {statement.Method}";
        return new IndexPlan(catalog, statement, sourceText, "CreateIndex",
            $"{prefix}{statement.IndexName} ON {tableQn}({string.Join(", ", statement.Columns)}){method}");
    }

    /// <summary>Builds a plan for <c>DROP INDEX</c>.</summary>
    public static IndexPlan ForDropIndex(
        TableCatalog catalog, DropIndexStatement statement, string? sourceText)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return new IndexPlan(catalog, statement, sourceText, "DropIndex",
            statement.IfExists ? $"IF EXISTS {statement.IndexName}" : statement.IndexName);
    }

    /// <summary>Builds a plan for <c>REINDEX</c>.</summary>
    public static IndexPlan ForReindex(
        TableCatalog catalog, ReindexTableStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        string qn = statement.SchemaName is null
            ? statement.TableName
            : $"{statement.SchemaName}.{statement.TableName}";
        return new IndexPlan(catalog, statement, sourceText: null, "Reindex", qn);
    }

    /// <summary>Builds a plan for <c>ANALYZE</c>.</summary>
    public static IndexPlan ForAnalyze(
        TableCatalog catalog, AnalyzeTableStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        string qn = statement.SchemaName is null
            ? statement.TableName
            : $"{statement.SchemaName}.{statement.TableName}";
        return new IndexPlan(catalog, statement, sourceText: null, "Analyze", qn);
    }

    /// <inheritdoc />
    public override ExplainPlanNode ExplainTree { get; }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<RowBatch> ExecuteImplAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        Heliosoph.DatumV.Execution.ExecutionContext context)
    {
        if (Interlocked.Exchange(ref _executed, 1) != 0)
        {
            throw new InvalidOperationException(
                $"IndexPlan '{ExplainTree.OperatorName}' has already been executed. " +
                "Statement plans represent a single pending execution; re-plan the statement to apply it again.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        switch (_statement)
        {
            case CreateIndexStatement create:
                await IndexExecutor.CreateIndexAsync(Catalog, create, _sourceText).ConfigureAwait(false);
                break;
            case DropIndexStatement drop:
                IndexExecutor.DropIndex(Catalog, drop, _sourceText);
                break;
            case ReindexTableStatement reindex:
                await IndexExecutor.ReindexAsync(Catalog, reindex).ConfigureAwait(false);
                break;
            case AnalyzeTableStatement analyze:
                await AnalyzeExecutor.ExecuteAsync(Catalog, analyze).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException(
                    $"IndexPlan: unrecognised statement type {_statement.GetType().Name}.");
        }

        yield break;
    }
}
