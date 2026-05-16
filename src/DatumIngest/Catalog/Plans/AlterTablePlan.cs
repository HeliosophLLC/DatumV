using System.Runtime.CompilerServices;
using DatumIngest.Catalog.Executors;
using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Catalog.Plans;

/// <summary>
/// <see cref="StatementPlan"/> for the <c>ALTER TABLE</c> family:
/// <c>ADD COLUMN</c>, <c>DROP COLUMN</c>, <c>DROP CONSTRAINT</c>,
/// <c>ALTER COLUMN DROP</c>, and <c>ALTER COLUMN SET</c>. All five
/// share the same target-table shape (qualified name + <c>TableIfExists</c>
/// guard) so a single class with a kind discriminator handles them.
/// </summary>
/// <remarks>
/// <para>
/// Construction is pure: building an <see cref="AlterTablePlan"/> never
/// mutates the catalog. The side effect (schema mutation through
/// <see cref="AlterTableExecutor"/>) only runs inside
/// <see cref="ExecuteImplAsync"/> on iteration. The <c>ALTER TABLE IF EXISTS</c>
/// missing-table short-circuit is evaluated at execute time, not at plan
/// time, so a plan built before the target table exists still applies
/// correctly if the table is created in between.
/// </para>
/// <para>
/// <b>Idempotency:</b> the apply runs at most once. Subsequent
/// <c>ExecuteAsync</c> calls throw — re-plan the statement to apply it
/// again.
/// </para>
/// </remarks>
internal sealed class AlterTablePlan : StatementPlan
{
    private readonly Statement _statement;
    private readonly string _tableName;
    private readonly string? _schemaName;
    private readonly bool _tableIfExists;
    private readonly string? _sourceText;
    private int _executed;

    private AlterTablePlan(
        TableCatalog catalog,
        Statement statement,
        string tableName,
        string? schemaName,
        bool tableIfExists,
        string? sourceText,
        string operatorName,
        string details) : base(catalog)
    {
        ArgumentNullException.ThrowIfNull(statement);

        _statement = statement;
        _tableName = tableName;
        _schemaName = schemaName;
        _tableIfExists = tableIfExists;
        _sourceText = sourceText;

        ExplainTree = new ExplainPlanNode
        {
            OperatorName = operatorName,
            Details = details,
            EstimatedRows = 0,
        };
    }

    /// <summary>Builds a plan for <c>ALTER TABLE ADD COLUMN</c>.</summary>
    public static AlterTablePlan ForAddColumn(
        TableCatalog catalog, AlterTableAddColumnStatement statement, string? sourceText)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return new AlterTablePlan(
            catalog, statement, statement.TableName, statement.SchemaName,
            statement.TableIfExists, sourceText,
            "AlterTable",
            $"{Qualify(statement.SchemaName, statement.TableName)} ADD COLUMN {statement.ColumnName} {statement.TypeName}");
    }

    /// <summary>Builds a plan for <c>ALTER TABLE DROP COLUMN</c>.</summary>
    public static AlterTablePlan ForDropColumn(
        TableCatalog catalog, AlterTableDropColumnStatement statement, string? sourceText)
    {
        ArgumentNullException.ThrowIfNull(statement);
        string columnIfExists = statement.IfExists ? "IF EXISTS " : "";
        return new AlterTablePlan(
            catalog, statement, statement.TableName, statement.SchemaName,
            statement.TableIfExists, sourceText,
            "AlterTable",
            $"{Qualify(statement.SchemaName, statement.TableName)} DROP COLUMN {columnIfExists}{statement.ColumnName}");
    }

    /// <summary>Builds a plan for <c>ALTER TABLE DROP CONSTRAINT</c>.</summary>
    public static AlterTablePlan ForDropConstraint(
        TableCatalog catalog, AlterTableDropConstraintStatement statement, string? sourceText)
    {
        ArgumentNullException.ThrowIfNull(statement);
        string constraintIfExists = statement.IfExists ? "IF EXISTS " : "";
        return new AlterTablePlan(
            catalog, statement, statement.TableName, statement.SchemaName,
            statement.TableIfExists, sourceText,
            "AlterTable",
            $"{Qualify(statement.SchemaName, statement.TableName)} DROP CONSTRAINT {constraintIfExists}{statement.ConstraintName}");
    }

    /// <summary>Builds a plan for <c>ALTER TABLE ALTER COLUMN DROP</c>.</summary>
    public static AlterTablePlan ForAlterColumnDrop(
        TableCatalog catalog, AlterTableAlterColumnDropStatement statement, string? sourceText)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return new AlterTablePlan(
            catalog, statement, statement.TableName, statement.SchemaName,
            statement.TableIfExists, sourceText,
            "AlterTable",
            $"{Qualify(statement.SchemaName, statement.TableName)} ALTER COLUMN {statement.ColumnName} DROP {statement.Target}");
    }

    /// <summary>Builds a plan for <c>ALTER TABLE ALTER COLUMN SET</c>.</summary>
    public static AlterTablePlan ForAlterColumnSet(
        TableCatalog catalog, AlterTableAlterColumnSetStatement statement, string? sourceText)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return new AlterTablePlan(
            catalog, statement, statement.TableName, statement.SchemaName,
            statement.TableIfExists, sourceText,
            "AlterTable",
            $"{Qualify(statement.SchemaName, statement.TableName)} ALTER COLUMN {statement.ColumnName} SET {statement.Target}");
    }

    /// <inheritdoc />
    public override ExplainPlanNode ExplainTree { get; }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<RowBatch> ExecuteImplAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        BatchContext batchContext)
    {
        if (Interlocked.Exchange(ref _executed, 1) != 0)
        {
            throw new InvalidOperationException(
                $"AlterTablePlan '{ExplainTree.OperatorName}' has already been executed. " +
                "Statement plans represent a single pending execution; re-plan the statement to apply it again.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // ALTER TABLE IF EXISTS: evaluate at execute time, not plan time,
        // so a plan built before the table was created still applies.
        if (_tableIfExists
            && !Catalog.TryGetTable(Catalog.ResolveDdlName(_schemaName, _tableName).ToString(), out _))
        {
            yield break;
        }

        switch (_statement)
        {
            case AlterTableAddColumnStatement add:
                await AlterTableExecutor.AddColumnAsync(Catalog, add, _sourceText).ConfigureAwait(false);
                break;
            case AlterTableDropColumnStatement dropCol:
                AlterTableExecutor.DropColumn(Catalog, dropCol, _sourceText);
                break;
            case AlterTableDropConstraintStatement dropConstraint:
                await AlterTableExecutor.DropConstraintAsync(Catalog, dropConstraint, _sourceText).ConfigureAwait(false);
                break;
            case AlterTableAlterColumnDropStatement alterDrop:
                await AlterTableExecutor.AlterColumnDropAsync(Catalog, alterDrop, _sourceText).ConfigureAwait(false);
                break;
            case AlterTableAlterColumnSetStatement alterSet:
                await AlterTableExecutor.AlterColumnSetAsync(Catalog, alterSet, _sourceText).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException(
                    $"AlterTablePlan: unrecognised statement type {_statement.GetType().Name}.");
        }

        yield break;
    }

    private static string Qualify(string? schemaName, string tableName)
        => schemaName is null ? tableName : $"{schemaName}.{tableName}";
}
