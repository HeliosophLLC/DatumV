using System.Runtime.CompilerServices;
using DatumIngest.Catalog.Executors;
using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Catalog.Plans;

/// <summary>
/// <see cref="StatementPlan"/> for the schema-DDL family:
/// <c>CREATE SCHEMA</c>, <c>DROP SCHEMA</c>, and <c>SET search_path</c>.
/// All three are structurally identical (zero children, zero rows, one
/// side-effect apply on the catalog's backend routing table or session
/// search path) so a single class with a kind discriminator handles them.
/// </summary>
/// <remarks>
/// <para>
/// Construction is pure: building a <see cref="SchemaPlan"/> never
/// mutates the catalog. The side effect (mount / unmount / search-path
/// swap through <see cref="SchemaExecutor"/>) only runs inside
/// <see cref="ExecuteImplAsync"/> on iteration.
/// </para>
/// <para>
/// <b>Idempotency:</b> the apply runs at most once. Subsequent
/// <c>ExecuteAsync</c> calls throw — schema DDL is not safe to re-run;
/// re-plan the statement instead.
/// </para>
/// </remarks>
internal sealed class SchemaPlan : StatementPlan
{
    private readonly Statement _statement;
    private readonly string? _sourceText;
    private int _executed;

    private SchemaPlan(
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

    /// <summary>Builds a plan for <c>CREATE SCHEMA</c>.</summary>
    public static SchemaPlan ForCreateSchema(
        TableCatalog catalog, CreateSchemaStatement statement, string? sourceText)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return new SchemaPlan(catalog, statement, sourceText, "CreateSchema",
            statement.IfNotExists ? $"IF NOT EXISTS {statement.SchemaName}" : statement.SchemaName);
    }

    /// <summary>Builds a plan for <c>DROP SCHEMA</c>.</summary>
    public static SchemaPlan ForDropSchema(
        TableCatalog catalog, DropSchemaStatement statement, string? sourceText)
    {
        ArgumentNullException.ThrowIfNull(statement);
        string prefix = statement.IfExists ? "IF EXISTS " : "";
        string suffix = statement.Cascade ? " CASCADE" : "";
        return new SchemaPlan(catalog, statement, sourceText, "DropSchema",
            $"{prefix}{statement.SchemaName}{suffix}");
    }

    /// <summary>Builds a plan for <c>SET search_path</c>.</summary>
    public static SchemaPlan ForSetSearchPath(
        TableCatalog catalog, SetSearchPathStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return new SchemaPlan(catalog, statement, sourceText: null, "SetSearchPath",
            string.Join(", ", statement.Schemas));
    }

    /// <inheritdoc />
    public override ExplainPlanNode ExplainTree { get; }

    /// <inheritdoc />
#pragma warning disable CS1998 // Async method lacks 'await' — leaf yields no rows.
    protected override async IAsyncEnumerable<RowBatch> ExecuteImplAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        BatchContext batchContext)
    {
        if (Interlocked.Exchange(ref _executed, 1) != 0)
        {
            throw new InvalidOperationException(
                $"SchemaPlan '{ExplainTree.OperatorName}' has already been executed. " +
                "Statement plans represent a single pending execution; re-plan the statement to apply it again.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        switch (_statement)
        {
            case CreateSchemaStatement create:
                SchemaExecutor.CreateSchema(Catalog, create, _sourceText);
                break;
            case DropSchemaStatement drop:
                SchemaExecutor.DropSchema(Catalog, drop, _sourceText);
                break;
            case SetSearchPathStatement setSearchPath:
                SchemaExecutor.SetSearchPath(Catalog, setSearchPath);
                break;
            default:
                throw new InvalidOperationException(
                    $"SchemaPlan: unrecognised statement type {_statement.GetType().Name}.");
        }

        yield break;
    }
#pragma warning restore CS1998
}
