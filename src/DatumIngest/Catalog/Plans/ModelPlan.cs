using System.Runtime.CompilerServices;
using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Catalog.Plans;

/// <summary>
/// <see cref="StatementPlan"/> for the model-DDL family:
/// <c>CREATE MODEL</c>, <c>DROP MODEL</c>, <c>EVICT MODEL</c>, and
/// <c>RESET CALIBRATION</c>. All four are structurally identical
/// (zero children, zero rows, one side-effect apply on the model
/// registry / residency manager) so a single class with a kind
/// discriminator handles them.
/// </summary>
/// <remarks>
/// <para>
/// Construction is pure: building a <see cref="ModelPlan"/> never
/// mutates the catalog. The side effect (registration / eviction /
/// reset through <see cref="RoutineRegistrar"/>) only runs inside
/// <see cref="ExecuteImplAsync"/> on iteration.
/// </para>
/// <para>
/// <b>Idempotency:</b> the apply runs at most once. Subsequent
/// <c>ExecuteAsync</c> calls throw — model DDL is not safe to re-run;
/// re-plan the statement instead.
/// </para>
/// </remarks>
internal sealed class ModelPlan : StatementPlan
{
    private readonly Statement _statement;
    private readonly RoutineRegistrar _routineRegistrar;
    private readonly string? _sourceText;
    private int _executed;

    private ModelPlan(
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

    /// <summary>Builds a plan for <c>CREATE MODEL</c>.</summary>
    public static ModelPlan ForCreateModel(
        TableCatalog catalog, CreateModelStatement statement, string? sourceText)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return new ModelPlan(catalog, catalog.Routines, statement, sourceText, "CreateModel", DescribeCreate(
            statement.SchemaName, statement.Name, statement.Parameters.Count,
            statement.IfNotExists, statement.OrReplace));
    }

    /// <summary>Builds a plan for <c>DROP MODEL</c>.</summary>
    public static ModelPlan ForDropModel(
        TableCatalog catalog, DropModelStatement statement, string? sourceText)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return new ModelPlan(catalog, catalog.Routines, statement, sourceText, "DropModel",
            DescribeNameIfExists(statement.SchemaName, statement.Name, statement.IfExists));
    }

    /// <summary>Builds a plan for <c>EVICT MODEL</c>.</summary>
    public static ModelPlan ForEvictModel(
        TableCatalog catalog, EvictModelStatement statement, string? sourceText)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return new ModelPlan(catalog, catalog.Routines, statement, sourceText, "EvictModel",
            DescribeNameIfExists(statement.SchemaName, statement.Name, statement.IfExists));
    }

    /// <summary>Builds a plan for <c>RESET CALIBRATION</c>.</summary>
    public static ModelPlan ForResetCalibration(
        TableCatalog catalog, ResetCalibrationStatement statement, string? sourceText)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return new ModelPlan(catalog, catalog.Routines, statement, sourceText, "ResetCalibration",
            DescribeNameIfExists(statement.SchemaName, statement.Name, statement.IfExists));
    }

    /// <inheritdoc />
    public override ExplainPlanNode ExplainTree { get; }

    /// <inheritdoc />
#pragma warning disable CS1998 // Async method lacks 'await' on non-CreateModel branches — leaf yields no rows.
    protected override async IAsyncEnumerable<RowBatch> ExecuteImplAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        BatchContext batchContext)
    {
        if (Interlocked.Exchange(ref _executed, 1) != 0)
        {
            throw new InvalidOperationException(
                $"ModelPlan '{ExplainTree.OperatorName}' has already been executed. " +
                "Statement plans represent a single pending execution; re-plan the statement to apply it again.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        switch (_statement)
        {
            case CreateModelStatement create:
                await _routineRegistrar.ApplyCreateModelAsync(create, _sourceText).ConfigureAwait(false);
                break;
            case DropModelStatement drop:
                _routineRegistrar.ApplyDropModel(drop, _sourceText);
                break;
            case EvictModelStatement evict:
                _routineRegistrar.ApplyEvictModel(evict, _sourceText);
                break;
            case ResetCalibrationStatement reset:
                _routineRegistrar.ApplyResetCalibration(reset, _sourceText);
                break;
            default:
                throw new InvalidOperationException(
                    $"ModelPlan: unrecognised statement type {_statement.GetType().Name}.");
        }

        yield break;
    }
#pragma warning restore CS1998

    private static string DescribeCreate(
        string? schemaName, string name, int paramCount, bool ifNotExists, bool orReplace)
    {
        string qn = schemaName is null ? name : $"{schemaName}.{name}";
        string prefix = orReplace ? "OR REPLACE " : ifNotExists ? "IF NOT EXISTS " : "";
        return $"{prefix}{qn}({paramCount} param{(paramCount == 1 ? "" : "s")})";
    }

    private static string DescribeNameIfExists(string? schemaName, string name, bool ifExists)
    {
        string qn = schemaName is null ? name : $"{schemaName}.{name}";
        return ifExists ? $"IF EXISTS {qn}" : qn;
    }
}
