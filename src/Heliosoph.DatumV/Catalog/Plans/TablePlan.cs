using System.Runtime.CompilerServices;
using Heliosoph.DatumV.Catalog.Executors;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Catalog.Plans;

/// <summary>
/// <see cref="StatementPlan"/> for the table-DDL family:
/// <c>CREATE TABLE</c> and <c>DROP TABLE</c>. (CTAS has its own
/// <see cref="CtasPlan"/> because it composes a child SELECT subtree.)
/// Both shapes here are zero-children, zero-rows, one-side-effect on
/// the catalog backend / file-system, so a single class with a kind
/// discriminator handles them.
/// </summary>
/// <remarks>
/// <para>
/// Construction is pure: building a <see cref="TablePlan"/> never
/// mutates the catalog. The side effect (provider registration /
/// <c>.datum</c> file creation / deletion through
/// <see cref="TableExecutor"/>) only runs inside
/// <see cref="ExecuteImplAsync"/> on iteration.
/// </para>
/// <para>
/// <b>Idempotency:</b> the apply runs at most once. Subsequent
/// <c>ExecuteAsync</c> calls throw — table DDL is not safe to re-run;
/// re-plan the statement instead.
/// </para>
/// </remarks>
internal sealed class TablePlan : StatementPlan
{
    private readonly Statement _statement;
    private readonly string? _sourceText;
    private int _executed;

    private TablePlan(
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
    }

    /// <summary>Builds a plan for <c>CREATE TABLE</c>.</summary>
    public static TablePlan ForCreateTable(
        TableCatalog catalog, CreateTableStatement statement, string? sourceText)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return new TablePlan(catalog, statement, sourceText, "CreateTable",
            DescribeCreate(statement.SchemaName, statement.TableName, statement.Columns.Count,
                statement.IsTemp, statement.IfNotExists));
    }

    /// <summary>Builds a plan for <c>DROP TABLE</c>.</summary>
    public static TablePlan ForDropTable(
        TableCatalog catalog, DropTableStatement statement, string? sourceText)
    {
        ArgumentNullException.ThrowIfNull(statement);
        string qn = statement.SchemaName is null
            ? statement.TableName
            : $"{statement.SchemaName}.{statement.TableName}";
        return new TablePlan(catalog, statement, sourceText, "DropTable",
            statement.IfExists ? $"IF EXISTS {qn}" : qn);
    }

    /// <inheritdoc />
    public override ExplainPlanNode ExplainTree { get; }

    /// <inheritdoc />
#pragma warning disable CS1998 // Async method lacks 'await' on DROP — leaf yields no rows.
    protected override async IAsyncEnumerable<RowBatch> ExecuteImplAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        Execution.ExecutionContext context)
    {
        if (Interlocked.Exchange(ref _executed, 1) != 0)
        {
            throw new InvalidOperationException(
                $"TablePlan '{ExplainTree.OperatorName}' has already been executed. " +
                "Statement plans represent a single pending execution; re-plan the statement to apply it again.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        switch (_statement)
        {
            case CreateTableStatement create:
                await TableExecutor.CreateTableAsync(Catalog, create, context, _sourceText).ConfigureAwait(false);
                break;
            case DropTableStatement drop:
                TableExecutor.DropTable(Catalog, drop, _sourceText);
                break;
            default:
                throw new InvalidOperationException(
                    $"TablePlan: unrecognised statement type {_statement.GetType().Name}.");
        }

        yield break;
    }
#pragma warning restore CS1998

    private static string DescribeCreate(
        string? schemaName, string tableName, int columnCount, bool isTemp, bool ifNotExists)
    {
        string qn = schemaName is null ? tableName : $"{schemaName}.{tableName}";
        string prefix = isTemp ? "TEMP " : ifNotExists ? "IF NOT EXISTS " : "";
        return $"{prefix}{qn}({columnCount} column{(columnCount == 1 ? "" : "s")})";
    }
}
