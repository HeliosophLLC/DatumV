using DatumIngest.Catalog;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests;

/// <summary>
/// Sync <c>Plan(...)</c> overloads for tests. The production
/// <see cref="TableCatalog"/> exposes async planning only; these extensions
/// bridge the async API for synchronous test code via
/// <c>.GetAwaiter().GetResult()</c>. Routes to
/// <see cref="TableCatalog.ExecuteStatementAsync(string)"/> because most
/// tests want eager DDL/DML application — they don't iterate the returned
/// plan for side effects, they just want the statement to have happened by
/// the time the sync call returns.
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
        catalog.ExecuteStatementAsync(sql).GetAwaiter().GetResult();

    public static IQueryPlan Plan(this TableCatalog catalog, Statement statement) =>
        catalog.ExecuteStatementAsync(statement).GetAwaiter().GetResult();

    public static IQueryPlan Plan(this TableCatalog catalog, Statement statement, string? sourceText) =>
        catalog.ExecuteStatementAsync(statement, sourceText).GetAwaiter().GetResult();
}
