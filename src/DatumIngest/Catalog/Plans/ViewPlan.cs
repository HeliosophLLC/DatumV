using System.Runtime.CompilerServices;
using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Catalog.Plans;

/// <summary>
/// <see cref="StatementPlan"/> for the view-DDL family:
/// <c>CREATE VIEW</c> and <c>DROP VIEW</c>. Both are structurally identical
/// (zero children, zero rows, one side-effect apply on the view registry)
/// so a single class with a kind discriminator handles them.
/// </summary>
/// <remarks>
/// <para>
/// Construction is pure: building a <see cref="ViewPlan"/> never mutates
/// the catalog. The side effect (registration through
/// <see cref="RoutineRegistrar"/>) only runs inside
/// <see cref="ExecuteImplAsync"/> on iteration.
/// </para>
/// <para>
/// <b>Idempotency:</b> the apply runs at most once. Subsequent
/// <c>ExecuteAsync</c> calls throw — view DDL is not safe to re-run;
/// re-plan the statement instead.
/// </para>
/// </remarks>
internal sealed class ViewPlan : StatementPlan
{
    private readonly Statement _statement;
    private readonly RoutineRegistrar _routineRegistrar;
    private readonly string? _sourceText;
    private int _executed;

    private ViewPlan(
        TableCatalog catalog,
        RoutineRegistrar routineRegistrar,
        Statement statement,
        string? sourceText,
        string operatorName,
        string details) : base(catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(routineRegistrar);
        ArgumentNullException.ThrowIfNull(statement);

        _statement = statement;
        _sourceText = sourceText;
        _routineRegistrar = routineRegistrar;

        ExplainTree = new ExplainPlanNode
        {
            OperatorName = operatorName,
            Details = details,
            EstimatedRows = 0,
        };
    }

    /// <summary>Builds a plan for <c>CREATE VIEW</c>.</summary>
    public static ViewPlan ForCreateView(
        TableCatalog catalog, CreateViewStatement statement, string? sourceText)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return new ViewPlan(catalog, catalog.Routines, statement, sourceText, "CreateView",
            DescribeCreate(statement.SchemaName, statement.Name, statement.IfNotExists, statement.OrReplace));
    }

    /// <summary>Builds a plan for <c>DROP VIEW</c>.</summary>
    public static ViewPlan ForDropView(
        TableCatalog catalog, DropViewStatement statement, string? sourceText)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return new ViewPlan(catalog, catalog.Routines, statement, sourceText, "DropView",
            DescribeDrop(statement.SchemaName, statement.Name, statement.IfExists));
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
                $"ViewPlan '{ExplainTree.OperatorName}' has already been executed. " +
                "Statement plans represent a single pending execution; re-plan the statement to apply it again.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        switch (_statement)
        {
            case CreateViewStatement create:
                _routineRegistrar.ApplyCreateView(create, _sourceText);
                break;
            case DropViewStatement drop:
                _routineRegistrar.ApplyDropView(drop, _sourceText);
                break;
            default:
                throw new InvalidOperationException(
                    $"ViewPlan: unrecognised statement type {_statement.GetType().Name}.");
        }

        yield break;
    }
#pragma warning restore CS1998

    private static string DescribeCreate(string? schemaName, string name, bool ifNotExists, bool orReplace)
    {
        string qn = schemaName is null ? name : $"{schemaName}.{name}";
        string prefix = orReplace ? "OR REPLACE " : ifNotExists ? "IF NOT EXISTS " : "";
        return $"{prefix}{qn}";
    }

    private static string DescribeDrop(string? schemaName, string name, bool ifExists)
    {
        string qn = schemaName is null ? name : $"{schemaName}.{name}";
        return ifExists ? $"IF EXISTS {qn}" : qn;
    }
}
