using DatumIngest.Catalog;
using DatumIngest.Catalog.Plans;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests;

/// <summary>
/// Sync <c>Plan(...)</c> overloads for tests. The production
/// <see cref="TableCatalog"/> exposes async planning only; these
/// extensions bridge the async API for synchronous test code via
/// <c>.GetAwaiter().GetResult()</c>.
/// </summary>
/// <remarks>
/// <para>
/// Plans through <see cref="TableCatalog.PlanAsync(string)"/>, then
/// eagerly applies side-effect-only plans (DDL, no-RETURNING DML, CTAS)
/// before returning. Row-yielding plans (SELECT, CALL, RETURNING DML)
/// are returned unevaluated so the test can iterate for rows — matches
/// the historical dual semantics tests rely on.
/// </para>
/// <para>
/// Lives in the test assembly so the sync-over-async bridge cannot leak
/// into production. Per the C1g async cleanup rule: no new
/// <c>.GetAwaiter().GetResult()</c> in <c>src/</c>; test-only bridges
/// belong here.
/// </para>
/// </remarks>
internal static class TableCatalogTestExtensions
{
    public static StatementPlan Plan(this TableCatalog catalog, string sql) =>
        PlanAndApplyIfSideEffect(catalog, catalog.PlanAsync(sql));

    public static StatementPlan Plan(this TableCatalog catalog, Statement statement) =>
        PlanAndApplyIfSideEffect(catalog, catalog.PlanAsync(statement));

    public static StatementPlan Plan(this TableCatalog catalog, Statement statement, string? sourceText) =>
        PlanAndApplyIfSideEffect(catalog, catalog.PlanAsync(statement, sourceText));

    private static StatementPlan PlanAndApplyIfSideEffect(TableCatalog catalog, Task<StatementPlan> planTask)
    {
        StatementPlan plan = planTask.GetAwaiter().GetResult();
        if (IsRowYielding(plan))
        {
            return plan;
        }
        // Side-effect-only plan (DDL / no-RETURNING DML / etc.): drain
        // it now to apply the effect, then hand back a NoOp marker the
        // caller can iterate for zero rows without re-triggering the
        // side effect (matches the historical ExecuteStatementAsync
        // contract tests rely on).
        catalog.ExecuteAsync(plan).DrainAsync().GetAwaiter().GetResult();
        return DdlPlan.NoOp(catalog, plan.ExplainTree.OperatorName);
    }

    private static bool IsRowYielding(StatementPlan plan) => plan is SelectPlan or DmlReturningPlan;
}
