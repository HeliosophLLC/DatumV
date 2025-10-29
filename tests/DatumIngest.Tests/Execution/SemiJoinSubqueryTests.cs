using System.Runtime.CompilerServices;
using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// End-to-end tests for IN (SELECT ...), NOT IN (SELECT ...), EXISTS (SELECT ...),
/// and NOT EXISTS (SELECT ...) subquery support, covering uncorrelated constant-folding,
/// correlated decorrelation into semi-joins, and SQL-standard NULL semantics.
/// </summary>
public sealed class SemiJoinSubqueryTests : ServiceTestBase
{
    private static readonly FunctionRegistry DefaultFunctions = FunctionRegistry.CreateDefault();

    // ─────────────── Uncorrelated IN subquery ───────────────

    /// <summary>
    /// Uncorrelated IN subquery: constant-folded at plan time to a literal value list.
    /// </summary>
    [Fact]
    public async Task UncorrelatedIn_FiltersToMatchingRows()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("employees",
            columns: ["name", "department_id"],
            ["alice", 1f],
            ["bob", 2f],
            ["carol", 3f]));
        catalog.Add(CreateProvider("active_departments",
            columns: ["id"],
            [1f],
            [3f]));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT name FROM employees WHERE department_id IN (SELECT id FROM active_departments)",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal("alice", results[0]["name"].AsString());
        Assert.Equal("carol", results[1]["name"].AsString());
    }

    /// <summary>
    /// Uncorrelated NOT IN subquery: filters out rows whose value appears in the subquery result.
    /// </summary>
    [Fact]
    public async Task UncorrelatedNotIn_FiltersOutMatchingRows()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("employees",
            columns: ["name", "department_id"],
            ["alice", 1f],
            ["bob", 2f],
            ["carol", 3f]));
        catalog.Add(CreateProvider("excluded_departments",
            columns: ["id"],
            [2f]));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT name FROM employees WHERE department_id NOT IN (SELECT id FROM excluded_departments)",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal("alice", results[0]["name"].AsString());
        Assert.Equal("carol", results[1]["name"].AsString());
    }

    /// <summary>
    /// IN subquery with empty subquery result: no rows match.
    /// </summary>
    [Fact]
    public async Task UncorrelatedIn_EmptySubquery_ReturnsNoRows()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("data",
            columns: ["x"],
            [1f],
            [2f]));
        catalog.Add(CreateProvider("empty_table",
            columns: ["x"]));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT x FROM data WHERE x IN (SELECT x FROM empty_table)",
            catalog);

        Assert.Empty(results);
    }

    /// <summary>
    /// NOT IN with empty subquery: all rows pass (nothing to exclude).
    /// </summary>
    [Fact]
    public async Task UncorrelatedNotIn_EmptySubquery_ReturnsAllRows()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("data",
            columns: ["x"],
            [1f],
            [2f]));
        catalog.Add(CreateProvider("empty_table",
            columns: ["x"]));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT x FROM data WHERE x NOT IN (SELECT x FROM empty_table)",
            catalog);

        Assert.Equal(2, results.Count);
    }

    // ─────────────── Uncorrelated EXISTS subquery ───────────────

    /// <summary>
    /// Uncorrelated EXISTS with non-empty subquery: predicate is true, all rows pass.
    /// </summary>
    [Fact]
    public async Task UncorrelatedExists_NonEmpty_ReturnsAllRows()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("data",
            columns: ["x"],
            [1f],
            [2f]));
        catalog.Add(CreateProvider("settings",
            columns: ["flag"],
            [1f]));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT x FROM data WHERE EXISTS (SELECT flag FROM settings)",
            catalog);

        Assert.Equal(2, results.Count);
    }

    /// <summary>
    /// Uncorrelated EXISTS with empty subquery: predicate is false, no rows pass.
    /// </summary>
    [Fact]
    public async Task UncorrelatedExists_Empty_ReturnsNoRows()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("data",
            columns: ["x"],
            [1f],
            [2f]));
        catalog.Add(CreateProvider("empty_table",
            columns: ["x"]));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT x FROM data WHERE EXISTS (SELECT x FROM empty_table)",
            catalog);

        Assert.Empty(results);
    }

    /// <summary>
    /// Uncorrelated NOT EXISTS with empty subquery: predicate is true, all rows pass.
    /// </summary>
    [Fact]
    public async Task UncorrelatedNotExists_Empty_ReturnsAllRows()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("data",
            columns: ["x"],
            [1f],
            [2f]));
        catalog.Add(CreateProvider("empty_table",
            columns: ["x"]));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT x FROM data WHERE NOT EXISTS (SELECT x FROM empty_table)",
            catalog);

        Assert.Equal(2, results.Count);
    }

    /// <summary>
    /// Uncorrelated NOT EXISTS with non-empty subquery: predicate is false, no rows pass.
    /// </summary>
    [Fact]
    public async Task UncorrelatedNotExists_NonEmpty_ReturnsNoRows()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("data",
            columns: ["x"],
            [1f],
            [2f]));
        catalog.Add(CreateProvider("settings",
            columns: ["flag"],
            [1f]));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT x FROM data WHERE NOT EXISTS (SELECT flag FROM settings)",
            catalog);

        Assert.Empty(results);
    }

    // ─────────────── Correlated IN subquery (semi-join) ───────────────

    /// <summary>
    /// Correlated IN subquery decorrelated into a semi-join: filters employees
    /// whose department exists in the active departments table with a matching region.
    /// </summary>
    [Fact]
    public async Task CorrelatedIn_SemiJoinFiltersCorrectly()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("employees",
            columns: ["name", "department_id", "region"],
            ["alice", 1f, "west"],
            ["bob", 2f, "east"],
            ["carol", 1f, "east"]));
        catalog.Add(CreateProvider("active_departments",
            columns: ["id", "region"],
            [1f, "west"],
            [2f, "west"]));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT name FROM employees " +
            "WHERE department_id IN (SELECT id FROM active_departments " +
            "WHERE active_departments.region = employees.region)",
            catalog);

        // alice: dept 1, region west → active_departments has (1, west) → match
        // bob: dept 2, region east → active_departments has (2, west) → no match (region mismatch)
        // carol: dept 1, region east → active_departments has (1, west) → no match (region mismatch)
        Assert.Single(results);
        Assert.Equal("alice", results[0]["name"].AsString());
    }

    /// <summary>
    /// Correlated NOT IN subquery: excludes rows where a match exists.
    /// </summary>
    [Fact]
    public async Task CorrelatedNotIn_AntiSemiJoinFiltersCorrectly()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("orders",
            columns: ["id", "customer_id"],
            [1f, 10f],
            [2f, 20f],
            [3f, 30f]));
        catalog.Add(CreateProvider("returns",
            columns: ["order_id", "customer_id"],
            [1f, 10f],
            [3f, 30f]));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT orders.id FROM orders " +
            "WHERE orders.id NOT IN (SELECT order_id FROM returns " +
            "WHERE returns.customer_id = orders.customer_id)",
            catalog);

        // Order 2 has no matching return with customer_id=20.
        Assert.Single(results);
        Assert.Equal(2f, results[0]["id"].AsFloat32());
    }

    // ─────────────── Correlated EXISTS subquery (semi-join) ───────────────

    /// <summary>
    /// Correlated EXISTS subquery: returns rows where at least one matching inner row exists.
    /// </summary>
    [Fact]
    public async Task CorrelatedExists_SemiJoinFiltersCorrectly()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("customers",
            columns: ["id", "name"],
            [1f, "alice"],
            [2f, "bob"],
            [3f, "carol"]));
        catalog.Add(CreateProvider("orders",
            columns: ["customer_id", "total"],
            [1f, 100f],
            [3f, 200f]));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT customers.name FROM customers " +
            "WHERE EXISTS (SELECT 1 FROM orders WHERE orders.customer_id = customers.id)",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal("alice", results[0]["name"].AsString());
        Assert.Equal("carol", results[1]["name"].AsString());
    }

    /// <summary>
    /// Correlated NOT EXISTS subquery: returns rows where no matching inner row exists.
    /// </summary>
    [Fact]
    public async Task CorrelatedNotExists_AntiSemiJoinFiltersCorrectly()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("customers",
            columns: ["id", "name"],
            [1f, "alice"],
            [2f, "bob"],
            [3f, "carol"]));
        catalog.Add(CreateProvider("orders",
            columns: ["customer_id", "total"],
            [1f, 100f],
            [3f, 200f]));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT customers.name FROM customers " +
            "WHERE NOT EXISTS (SELECT 1 FROM orders WHERE orders.customer_id = customers.id)",
            catalog);

        Assert.Single(results);
        Assert.Equal("bob", results[0]["name"].AsString());
    }

    // ─────────────── NULL semantics ───────────────

    /// <summary>
    /// NOT IN with NULL in the subquery result: per SQL standard, the entire
    /// result should be empty because NULL NOT IN is UNKNOWN.
    /// </summary>
    [Fact]
    public async Task NotIn_NullInSubquery_ReturnsNoRows()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("data",
            columns: ["x"],
            [1f],
            [2f],
            [3f]));
        catalog.Add(CreateProvider("subquery_values",
            columns: ["val"],
            [1f],
            [null]));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT x FROM data WHERE x NOT IN (SELECT val FROM subquery_values)",
            catalog);

        // SQL standard: NOT IN with any NULL in the subquery → all rows excluded.
        // The uncorrelated path constant-folds to InExpression with a NULL literal.
        // The evaluator's NOT IN with NULL should return UNKNOWN for all rows.
        // Note: This relies on EvaluateIn handling NULL correctly.
        Assert.Empty(results);
    }

    /// <summary>
    /// IN subquery selecting more than one column should throw a clear error.
    /// </summary>
    [Fact]
    public async Task InSubquery_MultipleColumns_Throws()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("data",
            columns: ["x"],
            [1f]));
        catalog.Add(CreateProvider("multi",
            columns: ["a", "b"],
            [1f, 2f]));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ExecuteQueryAsync(
                "SELECT x FROM data WHERE x IN (SELECT a, b FROM multi)",
                catalog));

        Assert.Contains("exactly one column", exception.Message);
    }

    // ─────────────── Combined with other clauses ───────────────

    /// <summary>
    /// IN subquery combined with other WHERE predicates: both the subquery
    /// and the regular predicate must be satisfied.
    /// </summary>
    [Fact]
    public async Task InSubquery_CombinedWithOtherPredicates()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("employees",
            columns: ["name", "department_id", "active"],
            ["alice", 1f, 1f],
            ["bob", 1f, 0f],
            ["carol", 2f, 1f]));
        catalog.Add(CreateProvider("departments",
            columns: ["id"],
            [1f]));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT name FROM employees " +
            "WHERE department_id IN (SELECT id FROM departments) AND active = 1",
            catalog);

        Assert.Single(results);
        Assert.Equal("alice", results[0]["name"].AsString());
    }

    // ─────────────── Parser tests ───────────────

    /// <summary>
    /// Verifies that IN (SELECT ...) parses to an <see cref="InSubqueryExpression"/>.
    /// </summary>
    [Fact]
    public void Parser_InSubquery_ParsesCorrectly()
    {
        SelectStatement statement = ((SelectQueryExpression)SqlParser.Parse(
            "SELECT x FROM t WHERE x IN (SELECT id FROM s)")).Statement;

        Assert.NotNull(statement.Where);
        InSubqueryExpression inSubquery = Assert.IsType<InSubqueryExpression>(statement.Where);
        Assert.False(inSubquery.Negated);
        Assert.IsType<ColumnReference>(inSubquery.Expression);
        ColumnReference innerColumn = Assert.IsType<ColumnReference>(
            ((SelectColumn)inSubquery.Query.Columns[0]).Expression);
        Assert.Equal("id", innerColumn.ColumnName);
    }

    /// <summary>
    /// Verifies that NOT IN (SELECT ...) parses to a negated <see cref="InSubqueryExpression"/>.
    /// </summary>
    [Fact]
    public void Parser_NotInSubquery_ParsesCorrectly()
    {
        SelectStatement statement = ((SelectQueryExpression)SqlParser.Parse(
            "SELECT x FROM t WHERE x NOT IN (SELECT id FROM s)")).Statement;

        Assert.NotNull(statement.Where);
        InSubqueryExpression inSubquery = Assert.IsType<InSubqueryExpression>(statement.Where);
        Assert.True(inSubquery.Negated);
    }

    /// <summary>
    /// Verifies that EXISTS (SELECT ...) parses to an <see cref="ExistsExpression"/>.
    /// </summary>
    [Fact]
    public void Parser_Exists_ParsesCorrectly()
    {
        SelectStatement statement = ((SelectQueryExpression)SqlParser.Parse(
            "SELECT x FROM t WHERE EXISTS (SELECT 1 FROM s)")).Statement;

        Assert.NotNull(statement.Where);
        ExistsExpression exists = Assert.IsType<ExistsExpression>(statement.Where);
        Assert.False(exists.Negated);
    }

    /// <summary>
    /// Verifies that NOT EXISTS (SELECT ...) parses to a negated <see cref="ExistsExpression"/>.
    /// </summary>
    [Fact]
    public void Parser_NotExists_ParsesCorrectly()
    {
        SelectStatement statement = ((SelectQueryExpression)SqlParser.Parse(
            "SELECT x FROM t WHERE NOT EXISTS (SELECT 1 FROM s)")).Statement;

        Assert.NotNull(statement.Where);
        // NOT EXISTS is parsed as UnaryExpression(NOT, ExistsExpression) because
        // the NOT layer consumes the NOT token before PrimaryExpression runs.
        UnaryExpression unary = Assert.IsType<UnaryExpression>(statement.Where);
        Assert.Equal(UnaryOperator.Not, unary.Operator);
        ExistsExpression exists = Assert.IsType<ExistsExpression>(unary.Operand);
        Assert.False(exists.Negated);
    }

    /// <summary>
    /// Verifies that IN with a literal value list still parses to <see cref="InExpression"/>.
    /// </summary>
    [Fact]
    public void Parser_InLiteralList_StillWorks()
    {
        SelectStatement statement = ((SelectQueryExpression)SqlParser.Parse(
            "SELECT x FROM t WHERE x IN (1, 2, 3)")).Statement;

        Assert.NotNull(statement.Where);
        InExpression inExpr = Assert.IsType<InExpression>(statement.Where);
        Assert.Equal(3, inExpr.Values.Count);
        Assert.False(inExpr.Negated);
    }

    // ─────────────── Helper infrastructure ───────────────

    private static async Task<List<Row>> ExecuteQueryAsync(string sql, TableCatalog catalog)
    {
        QueryExpression query = SqlParser.Parse(sql);
        QueryPlanner planner = new(catalog, DefaultFunctions);

        ExecutionContext context = new(
            CancellationToken.None,
            DefaultFunctions,
            catalog, new LocalBufferPool());

        IQueryOperator plan = await planner.PlanWithSubqueriesAsync(query, context, CancellationToken.None);

        return await plan.CollectRowsAsync(context);
    }
}
