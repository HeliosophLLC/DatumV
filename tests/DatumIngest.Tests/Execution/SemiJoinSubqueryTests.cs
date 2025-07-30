using System.Runtime.CompilerServices;
using DatumIngest.Catalog;
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
public sealed class SemiJoinSubqueryTests
{
    private static readonly FunctionRegistry DefaultFunctions = FunctionRegistry.CreateDefault();

    // ─────────────── Uncorrelated IN subquery ───────────────

    /// <summary>
    /// Uncorrelated IN subquery: constant-folded at plan time to a literal value list.
    /// </summary>
    [Fact]
    public async Task UncorrelatedIn_FiltersToMatchingRows()
    {
        Row[] employees =
        [
            MakeRow(("name", DataValue.FromString("alice")), ("department_id", DataValue.FromScalar(1f))),
            MakeRow(("name", DataValue.FromString("bob")), ("department_id", DataValue.FromScalar(2f))),
            MakeRow(("name", DataValue.FromString("carol")), ("department_id", DataValue.FromScalar(3f))),
        ];

        Row[] activeDepartments =
        [
            MakeRow(("id", DataValue.FromScalar(1f))),
            MakeRow(("id", DataValue.FromScalar(3f))),
        ];

        TableCatalog catalog = CreateCatalog(("employees", employees), ("active_departments", activeDepartments));
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
        Row[] employees =
        [
            MakeRow(("name", DataValue.FromString("alice")), ("department_id", DataValue.FromScalar(1f))),
            MakeRow(("name", DataValue.FromString("bob")), ("department_id", DataValue.FromScalar(2f))),
            MakeRow(("name", DataValue.FromString("carol")), ("department_id", DataValue.FromScalar(3f))),
        ];

        Row[] excludedDepartments =
        [
            MakeRow(("id", DataValue.FromScalar(2f))),
        ];

        TableCatalog catalog = CreateCatalog(("employees", employees), ("excluded_departments", excludedDepartments));
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
        Row[] data =
        [
            MakeRow(("x", DataValue.FromScalar(1f))),
            MakeRow(("x", DataValue.FromScalar(2f))),
        ];

        Row[] empty = [];

        TableCatalog catalog = CreateCatalog(("data", data), ("empty_table", empty));
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
        Row[] data =
        [
            MakeRow(("x", DataValue.FromScalar(1f))),
            MakeRow(("x", DataValue.FromScalar(2f))),
        ];

        Row[] empty = [];

        TableCatalog catalog = CreateCatalog(("data", data), ("empty_table", empty));
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
        Row[] data =
        [
            MakeRow(("x", DataValue.FromScalar(1f))),
            MakeRow(("x", DataValue.FromScalar(2f))),
        ];

        Row[] settings =
        [
            MakeRow(("flag", DataValue.FromScalar(1f))),
        ];

        TableCatalog catalog = CreateCatalog(("data", data), ("settings", settings));
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
        Row[] data =
        [
            MakeRow(("x", DataValue.FromScalar(1f))),
            MakeRow(("x", DataValue.FromScalar(2f))),
        ];

        Row[] empty = [];

        TableCatalog catalog = CreateCatalog(("data", data), ("empty_table", empty));
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
        Row[] data =
        [
            MakeRow(("x", DataValue.FromScalar(1f))),
            MakeRow(("x", DataValue.FromScalar(2f))),
        ];

        Row[] empty = [];

        TableCatalog catalog = CreateCatalog(("data", data), ("empty_table", empty));
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
        Row[] data =
        [
            MakeRow(("x", DataValue.FromScalar(1f))),
            MakeRow(("x", DataValue.FromScalar(2f))),
        ];

        Row[] settings =
        [
            MakeRow(("flag", DataValue.FromScalar(1f))),
        ];

        TableCatalog catalog = CreateCatalog(("data", data), ("settings", settings));
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
        Row[] employees =
        [
            MakeRow(("name", DataValue.FromString("alice")), ("department_id", DataValue.FromScalar(1f)), ("region", DataValue.FromString("west"))),
            MakeRow(("name", DataValue.FromString("bob")), ("department_id", DataValue.FromScalar(2f)), ("region", DataValue.FromString("east"))),
            MakeRow(("name", DataValue.FromString("carol")), ("department_id", DataValue.FromScalar(1f)), ("region", DataValue.FromString("east"))),
        ];

        Row[] activeDepartments =
        [
            MakeRow(("id", DataValue.FromScalar(1f)), ("region", DataValue.FromString("west"))),
            MakeRow(("id", DataValue.FromScalar(2f)), ("region", DataValue.FromString("west"))),
        ];

        TableCatalog catalog = CreateCatalog(("employees", employees), ("active_departments", activeDepartments));
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
        Row[] orders =
        [
            MakeRow(("id", DataValue.FromScalar(1f)), ("customer_id", DataValue.FromScalar(10f))),
            MakeRow(("id", DataValue.FromScalar(2f)), ("customer_id", DataValue.FromScalar(20f))),
            MakeRow(("id", DataValue.FromScalar(3f)), ("customer_id", DataValue.FromScalar(30f))),
        ];

        Row[] returns =
        [
            MakeRow(("order_id", DataValue.FromScalar(1f)), ("customer_id", DataValue.FromScalar(10f))),
            MakeRow(("order_id", DataValue.FromScalar(3f)), ("customer_id", DataValue.FromScalar(30f))),
        ];

        TableCatalog catalog = CreateCatalog(("orders", orders), ("returns", returns));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT orders.id FROM orders " +
            "WHERE orders.id NOT IN (SELECT order_id FROM returns " +
            "WHERE returns.customer_id = orders.customer_id)",
            catalog);

        // Order 2 has no matching return with customer_id=20.
        Assert.Single(results);
        Assert.Equal(2f, results[0]["id"].AsScalar());
    }

    // ─────────────── Correlated EXISTS subquery (semi-join) ───────────────

    /// <summary>
    /// Correlated EXISTS subquery: returns rows where at least one matching inner row exists.
    /// </summary>
    [Fact]
    public async Task CorrelatedExists_SemiJoinFiltersCorrectly()
    {
        Row[] customers =
        [
            MakeRow(("id", DataValue.FromScalar(1f)), ("name", DataValue.FromString("alice"))),
            MakeRow(("id", DataValue.FromScalar(2f)), ("name", DataValue.FromString("bob"))),
            MakeRow(("id", DataValue.FromScalar(3f)), ("name", DataValue.FromString("carol"))),
        ];

        Row[] orders =
        [
            MakeRow(("customer_id", DataValue.FromScalar(1f)), ("total", DataValue.FromScalar(100f))),
            MakeRow(("customer_id", DataValue.FromScalar(3f)), ("total", DataValue.FromScalar(200f))),
        ];

        TableCatalog catalog = CreateCatalog(("customers", customers), ("orders", orders));
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
        Row[] customers =
        [
            MakeRow(("id", DataValue.FromScalar(1f)), ("name", DataValue.FromString("alice"))),
            MakeRow(("id", DataValue.FromScalar(2f)), ("name", DataValue.FromString("bob"))),
            MakeRow(("id", DataValue.FromScalar(3f)), ("name", DataValue.FromString("carol"))),
        ];

        Row[] orders =
        [
            MakeRow(("customer_id", DataValue.FromScalar(1f)), ("total", DataValue.FromScalar(100f))),
            MakeRow(("customer_id", DataValue.FromScalar(3f)), ("total", DataValue.FromScalar(200f))),
        ];

        TableCatalog catalog = CreateCatalog(("customers", customers), ("orders", orders));
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
        Row[] data =
        [
            MakeRow(("x", DataValue.FromScalar(1f))),
            MakeRow(("x", DataValue.FromScalar(2f))),
            MakeRow(("x", DataValue.FromScalar(3f))),
        ];

        Row[] subqueryValues =
        [
            MakeRow(("val", DataValue.FromScalar(1f))),
            MakeRow(("val", DataValue.Null(DataKind.Scalar))),
        ];

        TableCatalog catalog = CreateCatalog(("data", data), ("subquery_values", subqueryValues));
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
        Row[] data =
        [
            MakeRow(("x", DataValue.FromScalar(1f))),
        ];

        Row[] multi =
        [
            MakeRow(("a", DataValue.FromScalar(1f)), ("b", DataValue.FromScalar(2f))),
        ];

        TableCatalog catalog = CreateCatalog(("data", data), ("multi", multi));

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
        Row[] employees =
        [
            MakeRow(("name", DataValue.FromString("alice")), ("department_id", DataValue.FromScalar(1f)), ("active", DataValue.FromScalar(1f))),
            MakeRow(("name", DataValue.FromString("bob")), ("department_id", DataValue.FromScalar(1f)), ("active", DataValue.FromScalar(0f))),
            MakeRow(("name", DataValue.FromString("carol")), ("department_id", DataValue.FromScalar(2f)), ("active", DataValue.FromScalar(1f))),
        ];

        Row[] departments =
        [
            MakeRow(("id", DataValue.FromScalar(1f))),
        ];

        TableCatalog catalog = CreateCatalog(("employees", employees), ("departments", departments));
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
        SelectStatement statement = SqlParser.Parse(
            "SELECT x FROM t WHERE x IN (SELECT id FROM s)");

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
        SelectStatement statement = SqlParser.Parse(
            "SELECT x FROM t WHERE x NOT IN (SELECT id FROM s)");

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
        SelectStatement statement = SqlParser.Parse(
            "SELECT x FROM t WHERE EXISTS (SELECT 1 FROM s)");

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
        SelectStatement statement = SqlParser.Parse(
            "SELECT x FROM t WHERE NOT EXISTS (SELECT 1 FROM s)");

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
        SelectStatement statement = SqlParser.Parse(
            "SELECT x FROM t WHERE x IN (1, 2, 3)");

        Assert.NotNull(statement.Where);
        InExpression inExpr = Assert.IsType<InExpression>(statement.Where);
        Assert.Equal(3, inExpr.Values.Count);
        Assert.False(inExpr.Negated);
    }

    // ─────────────── Helper infrastructure ───────────────

    private static Row MakeRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = columns.Select(c => c.Name).ToArray();
        DataValue[] values = columns.Select(c => c.Value).ToArray();
        return new Row(names, values);
    }

    private static TableCatalog CreateCatalog(params (string Name, Row[] Rows)[] tables)
    {
        TableCatalog catalog = new();

        foreach ((string name, Row[] rows) in tables)
        {
            InMemoryTableProvider provider = new(rows);
            catalog.RegisterProvider(name, () => provider);
            catalog.Register(new TableDescriptor(name, name, "", new Dictionary<string, string>()));
        }

        return catalog;
    }

    private static async Task<List<Row>> ExecuteQueryAsync(string sql, TableCatalog catalog)
    {
        SelectStatement statement = SqlParser.Parse(sql);
        QueryPlanner planner = new(catalog, DefaultFunctions);

        ExecutionContext context = new(
            CancellationToken.None,
            DefaultFunctions,
            catalog);

        IQueryOperator plan = await planner.PlanWithSubqueriesAsync(statement, context, CancellationToken.None);

        List<Row> rows = [];
        await foreach (Row row in plan.ExecuteAsync(context))
        {
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>
    /// Simple in-memory provider for testing.
    /// </summary>
    private sealed class InMemoryTableProvider : ITableProvider
    {
        private readonly Row[] _rows;

        /// <summary>
        /// Creates a provider that yields the given rows.
        /// </summary>
        public InMemoryTableProvider(Row[] rows)
        {
            _rows = rows;
        }

        /// <inheritdoc/>
        public Task<Schema> GetSchemaAsync(TableDescriptor descriptor, CancellationToken cancellationToken)
        {
            if (_rows.Length == 0)
            {
                return Task.FromResult(new Schema([new ColumnInfo("empty", DataKind.String, nullable: true)]));
            }

            List<ColumnInfo> columns = [];
            foreach (string name in _rows[0].ColumnNames)
            {
                columns.Add(new ColumnInfo(name, _rows[0][name].Kind, nullable: true));
            }

            return Task.FromResult(new Schema(columns));
        }

        /// <inheritdoc/>
        public Task<ProviderCapabilities> GetCapabilitiesAsync(
            TableDescriptor descriptor, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ProviderCapabilities(
                EstimatedRowCount: _rows.Length,
                EstimatedRowSizeBytes: null,
                SupportsSeek: false,
                ColumnCosts: new Dictionary<string, ColumnCost>()));
        }

        /// <inheritdoc/>
        public async IAsyncEnumerable<Row> OpenAsync(
            TableDescriptor descriptor,
            IReadOnlySet<string>? requiredColumns,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (Row row in _rows)
            {
                yield return row;
            }

            await Task.CompletedTask;
        }
    }
}
