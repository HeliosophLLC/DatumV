using DatumIngest.Catalog;
using DatumIngest.Catalog.Executors;
using DatumIngest.Catalog.Plans;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Adapts an INSERT / UPDATE / DELETE … RETURNING into an
/// <see cref="QueryOperator"/> so a data-modifying CTE body —
/// <c>WITH cte AS (INSERT/UPDATE/DELETE … RETURNING …)</c> — can act as a
/// row source in the surrounding plan tree.
/// </summary>
/// <remarks>
/// The DML side effect fires on first <see cref="QueryOperator.ExecuteAsync(ExecutionContext)"/>, exactly
/// once per surrounding query execution — matching PostgreSQL's
/// modifying-CTE semantics. <c>EXPLAIN WITH cte AS (UPDATE …) SELECT …</c>
/// does not commit the mutation at plan time. Multi-reference CTEs are
/// memoised by <see cref="CommonTableExpressionOperator"/>, so the
/// mutation still runs only once even when the CTE is referenced
/// multiple times.
/// </remarks>
internal sealed class DmlReturningOperator : QueryOperator
{
    private readonly TableCatalog _catalog;
    private readonly Func<TableCatalog, ExecutionContext, CapturedRowsSource, Task> _applyAsync;
    private readonly DmlReturningKind _kind;
    private readonly string _tableName;
    private readonly Schema _targetSchema;
    private readonly IReadOnlyList<SelectColumn> _returningColumns;
    private readonly string _operatorName;
    private readonly string _explainDetails;

    private DmlReturningOperator(
        TableCatalog catalog,
        Func<TableCatalog, ExecutionContext, CapturedRowsSource, Task> applyAsync,
        DmlReturningKind kind,
        string tableName,
        Schema targetSchema,
        IReadOnlyList<SelectColumn> returningColumns,
        string operatorName,
        string explainDetails)
        : base(false)
    {
        _catalog = catalog;
        _applyAsync = applyAsync;
        _kind = kind;
        _tableName = tableName;
        _targetSchema = targetSchema;
        _returningColumns = returningColumns;
        _operatorName = operatorName;
        _explainDetails = explainDetails;
    }

    public static DmlReturningOperator ForInsert(TableCatalog catalog, InsertStatement insert) =>
        Build(
            catalog, insert.TableName, insert.SchemaName, insert.Returning,
            DmlReturningKind.Insert,
            applyAsync: (cat, bctx, sink) => InsertExecutor.ApplyAsync(cat, insert, preplannedSource: null, sink, bctx),
            operatorName: "InsertReturning",
            explainDetails: $"INSERT INTO {insert.TableName} … RETURNING …");

    public static DmlReturningOperator ForUpdate(TableCatalog catalog, UpdateStatement update) =>
        Build(
            catalog, update.TableName, update.SchemaName, update.Returning,
            DmlReturningKind.Update,
            applyAsync: (cat, bctx, sink) => UpdateExecutor.ApplyAsync(cat, update, sink, bctx),
            operatorName: "UpdateReturning",
            explainDetails: $"UPDATE {update.TableName} … RETURNING …");

    public static DmlReturningOperator ForDelete(TableCatalog catalog, DeleteStatement delete) =>
        Build(
            catalog, delete.TableName, delete.SchemaName, delete.Returning,
            DmlReturningKind.Delete,
            applyAsync: (cat, bctx, sink) => DeleteExecutor.ApplyAsync(cat, delete, sink, bctx),
            operatorName: "DeleteReturning",
            explainDetails: $"DELETE FROM {delete.TableName} … RETURNING …");

    private static DmlReturningOperator Build(
        TableCatalog catalog,
        string tableName,
        string? schemaName,
        IReadOnlyList<SelectColumn>? returning,
        DmlReturningKind kind,
        Func<TableCatalog, ExecutionContext, CapturedRowsSource, Task> applyAsync,
        string operatorName,
        string explainDetails)
    {
        if (returning is null)
        {
            throw new InvalidOperationException(
                $"{operatorName}: cannot build a DML-returning operator for a statement with no RETURNING clause.");
        }

        // Resolve target schema once at construction. Modifying-CTE
        // execute fires once per surrounding query but the schema is
        // stable across that lifetime.
        SchemaResolver resolver = new(catalog, catalog.SearchPath);
        QualifiedName qn = resolver.Resolve(schemaName, tableName);
        if (!catalog.TryGetTable(qn.ToString(), out ITableProvider? provider))
        {
            throw new InvalidOperationException(
                $"{operatorName}: target table '{qn}' is not registered in the catalog.");
        }
        Schema targetSchema = provider.GetSchema();

        return new DmlReturningOperator(
            catalog, applyAsync, kind, tableName, targetSchema, returning,
            operatorName, explainDetails);
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<RowBatch> ExecuteAsyncImpl(ExecutionContext context)
    {
        // Build a capture sink, apply the DML side effect into it, then
        // construct a post-captured DmlReturningPlan and stream its
        // projected rows.
        CapturedRowsSource sink = new(_catalog);
        await _applyAsync(_catalog, context, sink).ConfigureAwait(false);

        DmlReturningPlan projection = new(
            _catalog, _kind, _tableName, _targetSchema,
            capturedBatches: sink.Batches,
            returningColumns: _returningColumns);

        await foreach (RowBatch batch in projection
            .ExecuteAsync(context.CancellationToken, context)
            .ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    /// <inheritdoc />
    protected override OperatorPlanDescription DescribeForExplainImpl() => new(_operatorName)
    {
        Properties = new Dictionary<string, string>
        {
            ["statement"] = _explainDetails,
            ["timing"] = "side effect on first execute (modifying-CTE semantics)",
        },
    };
}
