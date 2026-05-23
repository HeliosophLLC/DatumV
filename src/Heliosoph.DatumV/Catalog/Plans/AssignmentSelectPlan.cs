using System.Runtime.CompilerServices;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Catalog.Plans;

/// <summary>
/// <see cref="StatementPlan"/> for assignment-form SELECT — every column
/// is <c>@var = expression</c>. Runs the underlying projection as a normal
/// query; for each yielded row, stabilises the per-column value and
/// writes it through <see cref="VariableScope.Set"/>. Multiple matching
/// rows update the variables in iteration order — the last row's values
/// win, matching T-SQL semantics. Zero rows leave the variables unchanged.
/// Silent at the streaming wire — produces no rows.
/// </summary>
internal sealed class AssignmentSelectPlan : StatementPlan
{
    private readonly SelectPlan _sourcePlan;
    private readonly IReadOnlyList<string?> _assignTargets;

    private AssignmentSelectPlan(
        TableCatalog catalog,
        SelectPlan sourcePlan,
        IReadOnlyList<string?> assignTargets)
        : base(catalog)
    {
        _sourcePlan = sourcePlan;
        _assignTargets = assignTargets;

        ExplainPlanNode tree = new()
        {
            OperatorName = "AssignmentSelect",
            Details = $"{assignTargets.Count} target(s)",
            EstimatedRows = 0,
        };
        tree.Children.Add(sourcePlan.ExplainTree);
        ExplainTree = tree;
    }

    /// <inheritdoc />
    public override ExplainPlanNode ExplainTree { get; }

    public override string Kind => "assignmentselect";
    public override bool IsProductive => false;

    /// <summary>
    /// Detects whether <paramref name="statement"/> is an assignment-form
    /// SELECT — every top-level <see cref="SelectColumn"/> carries an
    /// <see cref="SelectColumn.AssignedVariableName"/>. Mixed projections
    /// + assignments throw eagerly so the user sees a clear error rather
    /// than half-bound variables and half-rendered rows. UNION / INTERSECT
    /// / EXCEPT compositions of an assignment-form SELECT are also
    /// rejected — set ops over assignment columns are nonsensical.
    /// Returns <see langword="true"/> and the planned source +
    /// <paramref name="plan"/> when the statement is assignment-form;
    /// <see langword="false"/> with both <see langword="null"/> when not.
    /// </summary>
    public static bool TryPlan(
        TableCatalog catalog,
        QueryStatement statement,
        out AssignmentSelectPlan? plan)
    {
        if (statement.Query is not SelectQueryExpression select)
        {
            plan = null;
            return false;
        }

        IReadOnlyList<SelectColumn> columns = select.Statement.Columns;
        if (columns.Count == 0)
        {
            plan = null;
            return false;
        }

        int assignmentCount = 0;
        foreach (SelectColumn col in columns)
        {
            if (col.AssignedVariableName is not null) assignmentCount++;
        }

        if (assignmentCount == 0)
        {
            plan = null;
            return false;
        }

        if (assignmentCount != columns.Count)
        {
            throw new QueryPlanException(
                "SELECT mixes variable assignments with projection columns. " +
                "All columns must use the `@var = expression` form, or none of them should. " +
                "Add an alias (`AS name`) to a column to opt it out of assignment and treat it " +
                "as a comparison instead.");
        }

        string?[] targets = new string?[columns.Count];
        for (int i = 0; i < columns.Count; i++)
        {
            targets[i] = columns[i].AssignedVariableName;
        }

        SelectPlan sourcePlan = catalog.PlanQuery(statement.Query);
        plan = new AssignmentSelectPlan(catalog, sourcePlan, targets);
        return true;
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<RowBatch> ExecuteImplAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        Execution.ExecutionContext context)
    {
        await foreach (RowBatch batch in _sourcePlan
            .ExecuteAsync(cancellationToken, context)
            .ConfigureAwait(false))
        {
            for (int rowIdx = 0; rowIdx < batch.Count; rowIdx++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Row row = batch[rowIdx];
                for (int colIdx = 0; colIdx < _assignTargets.Count; colIdx++)
                {
                    string? variableName = _assignTargets[colIdx];
                    if (variableName is null) continue;
                    // Lift directly from the producing batch's arena into a
                    // managed-payload ValueRef so the binding survives the
                    // batch's recycle without going through VariableStore.
                    EvaluationFrame batchFrame = new(row, batch.Arena, context.VariableStore, context);
                    ValueRef bound = ExpressionEvaluator.ToValueRef(row[colIdx], batchFrame);
                    context.VariableScope.Set(variableName, bound);
                }
            }
        }
        yield break;
    }
}
