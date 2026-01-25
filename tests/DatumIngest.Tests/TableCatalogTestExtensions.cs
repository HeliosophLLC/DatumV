using DatumIngest.Catalog;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests;

/// <summary>
/// Sync <c>Plan(...)</c> overloads for tests. The production
/// <see cref="TableCatalog"/> only exposes async planning
/// (<see cref="TableCatalog.PlanAsync(string)"/> and overloads); these
/// extensions bridge the async API for synchronous test code via
/// <c>.GetAwaiter().GetResult()</c>.
/// </summary>
/// <remarks>
/// Lives in the test assembly so the sync-over-async bridge cannot leak
/// into production. Per the C1g async cleanup rule: no new
/// <c>.GetAwaiter().GetResult()</c> in <c>src/</c>; test-only bridges
/// belong here.
/// </remarks>
internal static class TableCatalogTestExtensions
{
    public static IQueryPlan Plan(this TableCatalog catalog, string sql) =>
        catalog.PlanAsync(sql).GetAwaiter().GetResult();

    public static IQueryPlan Plan(this TableCatalog catalog, Statement statement) =>
        catalog.PlanAsync(statement).GetAwaiter().GetResult();

    public static IQueryPlan Plan(this TableCatalog catalog, Statement statement, string? sourceText) =>
        catalog.PlanAsync(statement, sourceText).GetAwaiter().GetResult();
}
