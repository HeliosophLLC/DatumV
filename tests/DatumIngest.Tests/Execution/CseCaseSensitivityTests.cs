namespace DatumIngest.Tests.Execution;

using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

/// <summary>
/// Pins down CSE's behaviour on textually-different but
/// semantically-equivalent expression pairs:
/// <list type="bullet">
///   <item><description>Whitespace differences inside a function call.</description></item>
///   <item><description>Case-different function names.</description></item>
///   <item><description>Case-different column names.</description></item>
/// </list>
/// CSE keys its dedup map on <see cref="QueryExplainer.FormatExpression"/>
/// output, so anything the parser preserves in the AST flows into the
/// fingerprint. These tests document where that's transparent and where it
/// isn't, so future work knows what to fix.
/// </summary>
public sealed class CseCaseSensitivityTests : ServiceTestBase
{
    private static IQueryOperator PlanQuery(string sql, TableCatalog catalog)
    {
        QueryExpression query = SqlParser.Parse(sql);
        QueryPlanner planner = new(catalog, FunctionRegistry.CreateDefault());
        return planner.Plan(query);
    }

    private TableCatalog Catalog() =>
        CreateCatalog("t",
            columns: ["a", "b"],
            new object?[] { "alpha", "beta" },
            new object?[] { "gamma", "delta" });

    /// <summary>
    /// Whitespace inside a function call is dropped at tokenisation, so the
    /// parser produces identical AST nodes for <c>concat(a,b)</c> and
    /// <c>concat(a, b)</c> and <c>concat( a , b )</c>. Identical AST →
    /// identical fingerprint → CSE dedups.
    /// </summary>
    [Fact]
    public void WhitespaceVariations_AreDeduped()
    {
        TableCatalog catalog = Catalog();
        IQueryOperator plan = PlanQuery(
            "SELECT concat(a, b), concat( a,b ), concat(a , b) FROM t",
            catalog);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);
        // Only one CSE rung — all three sites collapsed onto __cse_0.
        RowEnricherOperator enricher = Assert.IsType<RowEnricherOperator>(project.Source);
        Assert.Single(enricher.Enrichments);

        // All three projection columns reference the single hidden column.
        Assert.Equal(3, project.Columns.Count);
        foreach (SelectColumn col in project.Columns)
        {
            ColumnReference cref = Assert.IsType<ColumnReference>(col.Expression);
            Assert.Equal("__cse_0", cref.ColumnName);
        }
    }

    /// <summary>
    /// Pins down the current behaviour for case-different function names
    /// (<c>UPPER(a)</c> vs <c>upper(a)</c>). The strict assertion below makes
    /// the test fail the day this changes — flip the assertion at that point
    /// and update the docstring.
    /// </summary>
    [Fact]
    public void FunctionNameCaseDifference_DoesNotDedup()
    {
        TableCatalog catalog = Catalog();
        IQueryOperator plan = PlanQuery(
            "SELECT UPPER(a), upper(a) FROM t",
            catalog);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);

        // Today: no RowEnricher, both projection columns still hold their
        // original FunctionCallExpression with their original-cased
        // FunctionName. SQL-wise this is a missed optimisation — the two
        // calls evaluate the same function — but it's not a correctness bug.
        Assert.IsNotType<RowEnricherOperator>(project.Source);

        FunctionCallExpression call0 = Assert.IsType<FunctionCallExpression>(project.Columns[0].Expression);
        FunctionCallExpression call1 = Assert.IsType<FunctionCallExpression>(project.Columns[1].Expression);
        Assert.Equal("UPPER", call0.FunctionName);
        Assert.Equal("upper", call1.FunctionName);
    }

    /// <summary>
    /// Pins down the current behaviour for case-different column names
    /// (<c>concat(A, b)</c> vs <c>concat(a, b)</c>). Captures whatever the
    /// parser does with column-name casing — strict assertions tell us if
    /// the parser canonicalises (CSE dedups) or preserves (CSE doesn't).
    /// </summary>
    [Fact]
    public void ColumnNameCaseDifference_PinsCurrentBehaviour()
    {
        TableCatalog catalog = Catalog();
        IQueryOperator plan = PlanQuery(
            "SELECT concat(A, b), concat(a, b) FROM t",
            catalog);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);
        bool wasDeduped = project.Source is RowEnricherOperator;

        // Strict assertion: encodes today's reality. If this fails, the
        // parser/fingerprinter changed behaviour — investigate which.
        Assert.False(wasDeduped,
            "concat(A, b) and concat(a, b) deduped — column-name casing is " +
            "now canonicalised. Update the assertion and the docstring.");

        // Both projection columns still hold their FunctionCallExpression.
        FunctionCallExpression call0 = Assert.IsType<FunctionCallExpression>(project.Columns[0].Expression);
        FunctionCallExpression call1 = Assert.IsType<FunctionCallExpression>(project.Columns[1].Expression);
        ColumnReference arg0 = Assert.IsType<ColumnReference>(call0.Arguments[0]);
        ColumnReference arg1 = Assert.IsType<ColumnReference>(call1.Arguments[0]);
        Assert.Equal("A", arg0.ColumnName);
        Assert.Equal("a", arg1.ColumnName);
    }
}
