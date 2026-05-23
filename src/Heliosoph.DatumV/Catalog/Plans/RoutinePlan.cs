using System.Runtime.CompilerServices;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Catalog.Plans;

/// <summary>
/// <see cref="StatementPlan"/> for the routine-DDL family: <c>CREATE
/// FUNCTION</c>, <c>DROP FUNCTION</c>, <c>CREATE PROCEDURE</c>, and
/// <c>DROP PROCEDURE</c>. All four are structurally identical (zero
/// children, zero rows, one side-effect apply on the routine registries)
/// so a single class with a kind discriminator handles them; the
/// per-statement <see cref="ExplainPlanNode.OperatorName"/> and
/// <c>Details</c> distinguish them in <c>EXPLAIN</c> output.
/// </summary>
/// <remarks>
/// <para>
/// Construction is pure: building a <see cref="RoutinePlan"/> never
/// mutates the catalog. The side effect (registration through
/// <see cref="RoutineRegistrar"/>) only runs inside
/// <see cref="ExecuteImplAsync"/> on iteration.
/// </para>
/// <para>
/// <b>Idempotency:</b> the apply runs at most once. Subsequent
/// <c>ExecuteAsync</c> calls throw — routine DDL is not safe to re-run;
/// re-plan the statement instead.
/// </para>
/// </remarks>
internal sealed class RoutinePlan : StatementPlan
{
    private readonly Statement _statement;
    private readonly RoutineRegistrar _routineRegistrar;
    private readonly string? _sourceText;
    private int _executed;

    private RoutinePlan(
        TableCatalog catalog,
        RoutineRegistrar routineRegistrar,
        Statement statement,
        string? sourceText,
        string operatorName,
        string details) : base(catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
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
        Kind = operatorName.ToLowerInvariant();
    }

    public override string Kind { get; }

    /// <summary>Builds a plan for <c>CREATE FUNCTION</c>.</summary>
    public static RoutinePlan ForCreateFunction(
        TableCatalog catalog, CreateFunctionStatement statement, string? sourceText)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return new RoutinePlan(catalog, catalog.Routines, statement, sourceText, "CreateFunction", DescribeCreate(
            statement.SchemaName, statement.Name, statement.Parameters.Count,
            statement.IfNotExists, statement.OrReplace));
    }

    /// <summary>Builds a plan for <c>DROP FUNCTION</c>.</summary>
    public static RoutinePlan ForDropFunction(
        TableCatalog catalog, DropFunctionStatement statement, string? sourceText)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return new RoutinePlan(catalog, catalog.Routines, statement, sourceText, "DropFunction",
            DescribeDrop(statement.SchemaName, statement.Name, statement.IfExists));
    }

    /// <summary>Builds a plan for <c>CREATE PROCEDURE</c>.</summary>
    public static RoutinePlan ForCreateProcedure(
        TableCatalog catalog, CreateProcedureStatement statement, string? sourceText)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return new RoutinePlan(catalog, catalog.Routines, statement, sourceText, "CreateProcedure", DescribeCreate(
            statement.SchemaName, statement.Name, statement.Parameters.Count,
            statement.IfNotExists, statement.OrReplace));
    }

    /// <summary>Builds a plan for <c>DROP PROCEDURE</c>.</summary>
    public static RoutinePlan ForDropProcedure(
        TableCatalog catalog, DropProcedureStatement statement, string? sourceText)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return new RoutinePlan(catalog, catalog.Routines, statement, sourceText, "DropProcedure",
            DescribeDrop(statement.SchemaName, statement.Name, statement.IfExists));
    }

    /// <inheritdoc />
    public override ExplainPlanNode ExplainTree { get; }

    /// <inheritdoc />
#pragma warning disable CS1998 // Async method lacks 'await' — leaf yields no rows.
    protected override async IAsyncEnumerable<RowBatch> ExecuteImplAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        Execution.ExecutionContext context)
    {
        if (Interlocked.Exchange(ref _executed, 1) != 0)
        {
            throw new InvalidOperationException(
                $"RoutinePlan '{ExplainTree.OperatorName}' has already been executed. " +
                "Statement plans represent a single pending execution; re-plan the statement to apply it again.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        switch (_statement)
        {
            case CreateFunctionStatement create:
                _routineRegistrar.ApplyCreateFunction(create, _sourceText);
                break;
            case DropFunctionStatement drop:
                _routineRegistrar.ApplyDropFunction(drop, _sourceText);
                break;
            case CreateProcedureStatement create:
                _routineRegistrar.ApplyCreateProcedure(create, _sourceText);
                break;
            case DropProcedureStatement drop:
                _routineRegistrar.ApplyDropProcedure(drop, _sourceText);
                break;
            default:
                throw new InvalidOperationException(
                    $"RoutinePlan: unrecognised statement type {_statement.GetType().Name}.");
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

    private static string DescribeDrop(string? schemaName, string name, bool ifExists)
    {
        string qn = schemaName is null ? name : $"{schemaName}.{name}";
        return ifExists ? $"IF EXISTS {qn}" : qn;
    }
}
