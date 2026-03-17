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
    private static QueryOperator PlanQuery(string sql, TableCatalog catalog)
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
        QueryOperator plan = PlanQuery(
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
    /// Case-different function names (<c>UPPER(a)</c> vs <c>upper(a)</c>)
    /// fingerprint to the same bucket via <see cref="QueryExplainer.Fingerprint"/>'s
    /// identifier-canonicalisation. CSE dedups them into a single
    /// RowEnricher rung.
    /// </summary>
    [Fact]
    public void FunctionNameCaseDifference_IsDeduped()
    {
        TableCatalog catalog = Catalog();
        QueryOperator plan = PlanQuery(
            "SELECT UPPER(a), upper(a) FROM t",
            catalog);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);
        RowEnricherOperator enricher = Assert.IsType<RowEnricherOperator>(project.Source);
        Assert.Single(enricher.Enrichments);

        // Both SELECT columns now reference the single hoisted column.
        Assert.Equal(2, project.Columns.Count);
        ColumnReference col0 = Assert.IsType<ColumnReference>(project.Columns[0].Expression);
        ColumnReference col1 = Assert.IsType<ColumnReference>(project.Columns[1].Expression);
        Assert.Equal(col0.ColumnName, col1.ColumnName);
        Assert.Equal("__cse_0", col0.ColumnName);
    }

    /// <summary>
    /// Case-different column names (<c>concat(A, b)</c> vs <c>concat(a, b)</c>)
    /// also dedup. SQL identifiers are case-insensitive; the fingerprint
    /// canonicalises both before comparing.
    /// </summary>
    [Fact]
    public void ColumnNameCaseDifference_IsDeduped()
    {
        TableCatalog catalog = Catalog();
        QueryOperator plan = PlanQuery(
            "SELECT concat(A, b), concat(a, b) FROM t",
            catalog);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);
        RowEnricherOperator enricher = Assert.IsType<RowEnricherOperator>(project.Source);
        Assert.Single(enricher.Enrichments);

        Assert.Equal(2, project.Columns.Count);
        ColumnReference col0 = Assert.IsType<ColumnReference>(project.Columns[0].Expression);
        ColumnReference col1 = Assert.IsType<ColumnReference>(project.Columns[1].Expression);
        Assert.Equal(col0.ColumnName, col1.ColumnName);
    }

    /// <summary>
    /// Case-different string literals (<c>concat(a, 'FOO')</c> vs
    /// <c>concat(a, 'foo')</c>) must NOT dedup — the two calls genuinely
    /// produce different output. Verifies the fingerprinter only canonicalises
    /// identifiers, leaving literal payloads case-sensitive.
    /// </summary>
    [Fact]
    public void StringLiteralCaseDifference_IsNotDeduped()
    {
        TableCatalog catalog = Catalog();
        QueryOperator plan = PlanQuery(
            "SELECT concat(a, 'FOO'), concat(a, 'foo') FROM t",
            catalog);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);
        // No CSE rung — the two calls produce different values, must stay separate.
        Assert.IsNotType<RowEnricherOperator>(project.Source);
        Assert.Equal(2, project.Columns.Count);
        Assert.IsType<FunctionCallExpression>(project.Columns[0].Expression);
        Assert.IsType<FunctionCallExpression>(project.Columns[1].Expression);
    }
}
