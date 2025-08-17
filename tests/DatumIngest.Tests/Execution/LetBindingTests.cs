using System.Runtime.CompilerServices;
using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for <c>LET</c> bindings in SELECT — named, memoized intermediate
/// expressions computed once per row. Covers parsing, end-to-end execution,
/// chaining, memoization, output visibility, clause interactions, and error cases.
/// </summary>
public sealed class LetBindingTests
{
    private static readonly FunctionRegistry DefaultFunctions = FunctionRegistry.CreateDefault();

    // ─────────────── Parsing ───────────────

    /// <summary>
    /// A single LET binding parses to the correct AST node with name and expression.
    /// </summary>
    [Fact]
    public void Parse_SingleLetBinding()
    {
        SelectStatement statement = ParseStatement(
            "SELECT LET x = col1 + col2, x AS result FROM t");

        Assert.NotNull(statement.LetBindings);
        Assert.Single(statement.LetBindings);

        LetBinding binding = statement.LetBindings[0];
        Assert.Equal("x", binding.Name);
        Assert.Null(binding.OutputAlias);
        Assert.IsType<BinaryExpression>(binding.Expression);
    }

    /// <summary>
    /// A LET binding with <c>AS</c> alias has its <see cref="LetBinding.OutputAlias"/> set.
    /// </summary>
    [Fact]
    public void Parse_LetBindingWithAlias()
    {
        SelectStatement statement = ParseStatement(
            "SELECT LET x = col1 + col2 AS \"total\", col3 FROM t");

        Assert.NotNull(statement.LetBindings);
        Assert.Single(statement.LetBindings);

        LetBinding binding = statement.LetBindings[0];
        Assert.Equal("x", binding.Name);
        Assert.Equal("total", binding.OutputAlias);
    }

    /// <summary>
    /// Multiple chained LET bindings parse in order with correct names.
    /// </summary>
    [Fact]
    public void Parse_MultipleChainingLetBindings()
    {
        SelectStatement statement = ParseStatement(
            "SELECT LET a = x + 1, LET b = a * 2, b AS result FROM t");

        Assert.NotNull(statement.LetBindings);
        Assert.Equal(2, statement.LetBindings.Count);
        Assert.Equal("a", statement.LetBindings[0].Name);
        Assert.Equal("b", statement.LetBindings[1].Name);

        Assert.Single(statement.Columns);
    }

    /// <summary>
    /// A SELECT with no LET bindings produces a null <see cref="SelectStatement.LetBindings"/>.
    /// </summary>
    [Fact]
    public void Parse_NoLetBindings_ReturnsNull()
    {
        SelectStatement statement = ParseStatement("SELECT a, b FROM t");

        Assert.Null(statement.LetBindings);
        Assert.Equal(2, statement.Columns.Count);
    }

    /// <summary>
    /// LET after a regular column is a parse error because the grammar
    /// enforces LET-first ordering.
    /// </summary>
    [Fact]
    public void Parse_LetAfterRegularColumn_Throws()
    {
        Assert.ThrowsAny<Exception>(
            () => SqlParser.Parse("SELECT col1, LET x = col2, x FROM t"));
    }

    // ─────────────── End-to-end planner integration ───────────────

    /// <summary>
    /// A LET binding used in a single SELECT column produces the correct value.
    /// </summary>
    [Fact]
    public async Task EndToEnd_BasicLetBinding_ProducesCorrectValue()
    {
        Row[] data =
        [
            MakeRow(("a", DataValue.FromFloat32(10)), ("b", DataValue.FromFloat32(3))),
            MakeRow(("a", DataValue.FromFloat32(20)), ("b", DataValue.FromFloat32(7)))
        ];
        TableCatalog catalog = CreateCatalog(("t", data));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT LET s = a + b, s AS result FROM t", catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal(13f, results[0]["result"].AsFloat32());
        Assert.Equal(27f, results[1]["result"].AsFloat32());
    }

    /// <summary>
    /// A LET binding without an <c>AS</c> alias does not appear in the output.
    /// </summary>
    [Fact]
    public async Task EndToEnd_LetBindingNotInOutput_WhenNoAlias()
    {
        Row[] data =
        [
            MakeRow(("a", DataValue.FromFloat32(5)), ("b", DataValue.FromFloat32(2)))
        ];
        TableCatalog catalog = CreateCatalog(("t", data));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT LET s = a + b, s * 2 AS doubled FROM t", catalog);

        Assert.Single(results);
        Assert.Equal(1, results[0].FieldCount);
        Assert.Equal(14f, results[0]["doubled"].AsFloat32());
    }

    /// <summary>
    /// A LET binding with <c>AS</c> alias appears in the output with the alias name.
    /// </summary>
    [Fact]
    public async Task EndToEnd_LetBindingWithAlias_AppearsInOutput()
    {
        Row[] data =
        [
            MakeRow(("a", DataValue.FromFloat32(10)), ("b", DataValue.FromFloat32(3)))
        ];
        TableCatalog catalog = CreateCatalog(("t", data));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT LET s = a + b AS \"sum\", s * 2 AS doubled FROM t", catalog);

        Assert.Single(results);
        Assert.Equal(2, results[0].FieldCount);
        Assert.Equal(13f, results[0]["sum"].AsFloat32());
        Assert.Equal(26f, results[0]["doubled"].AsFloat32());
    }

    /// <summary>
    /// LET bindings chain left-to-right: a later binding can reference an earlier one.
    /// </summary>
    [Fact]
    public async Task EndToEnd_LetChaining_LaterBindingReferencesEarlier()
    {
        Row[] data =
        [
            MakeRow(("x", DataValue.FromFloat32(4)))
        ];
        TableCatalog catalog = CreateCatalog(("t", data));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT LET a = x + 1, LET b = a * 3, b AS result FROM t", catalog);

        Assert.Single(results);
        Assert.Equal(15f, results[0]["result"].AsFloat32());
    }

    /// <summary>
    /// LET binding referenced multiple times produces identical values per row,
    /// proving memoization. Uses <c>uuid4()</c> which returns a different value
    /// each time it is called; memoization means the LET expression is evaluated
    /// once and both references see the same UUID.
    /// </summary>
    [Fact]
    public async Task EndToEnd_Memoization_UuidStableAcrossReferences()
    {
        Row[] data =
        [
            MakeRow(("x", DataValue.FromFloat32(1)))
        ];
        TableCatalog catalog = CreateCatalog(("t", data));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT LET u = uuid4(), uuid_str(u) AS first, uuid_str(u) AS second FROM t",
            catalog);

        Assert.Single(results);
        string first = results[0]["first"].AsString();
        string second = results[0]["second"].AsString();
        Assert.False(string.IsNullOrEmpty(first));
        Assert.Equal(first, second);
    }

    /// <summary>
    /// <c>SELECT *</c> does not include LET bindings — even aliased ones.
    /// </summary>
    [Fact]
    public async Task EndToEnd_StarDoesNotIncludeLetBindings()
    {
        Row[] data =
        [
            MakeRow(("a", DataValue.FromFloat32(1)), ("b", DataValue.FromFloat32(2)))
        ];
        TableCatalog catalog = CreateCatalog(("t", data));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT LET s = a + b AS \"sum\", * FROM t", catalog);

        Assert.Single(results);
        // Output should be: sum, a, b (the aliased LET appears because of AS,
        // and * expands the source columns).
        Assert.Equal(3, results[0].FieldCount);
        Assert.Equal(3f, results[0]["sum"].AsFloat32());
        Assert.Equal(1f, results[0]["a"].AsFloat32());
        Assert.Equal(2f, results[0]["b"].AsFloat32());
    }

    /// <summary>
    /// LET binding works correctly with GROUP BY and aggregate functions.
    /// </summary>
    [Fact]
    public async Task EndToEnd_LetWithGroupBy_AggregateExpression()
    {
        Row[] data =
        [
            MakeRow(("category", DataValue.FromString("A")), ("value", DataValue.FromFloat32(10))),
            MakeRow(("category", DataValue.FromString("A")), ("value", DataValue.FromFloat32(20))),
            MakeRow(("category", DataValue.FromString("B")), ("value", DataValue.FromFloat32(30)))
        ];
        TableCatalog catalog = CreateCatalog(("t", data));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT LET total = SUM(value), category, total AS group_total FROM t GROUP BY category",
            catalog);

        Assert.Equal(2, results.Count);
        Row rowA = results.First(r => r["category"].AsString() == "A");
        Row rowB = results.First(r => r["category"].AsString() == "B");
        Assert.Equal(30f, rowA["group_total"].AsFloat32());
        Assert.Equal(30f, rowB["group_total"].AsFloat32());
    }

    /// <summary>
    /// LET binding with an alias can be referenced in ORDER BY via the alias name.
    /// </summary>
    [Fact]
    public async Task EndToEnd_LetAliasUsedInOrderBy()
    {
        Row[] data =
        [
            MakeRow(("x", DataValue.FromFloat32(3))),
            MakeRow(("x", DataValue.FromFloat32(1))),
            MakeRow(("x", DataValue.FromFloat32(2)))
        ];
        TableCatalog catalog = CreateCatalog(("t", data));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT LET doubled = x * 2 AS \"doubled\", x FROM t ORDER BY doubled",
            catalog);

        Assert.Equal(3, results.Count);
        Assert.Equal(2f, results[0]["doubled"].AsFloat32());
        Assert.Equal(4f, results[1]["doubled"].AsFloat32());
        Assert.Equal(6f, results[2]["doubled"].AsFloat32());
    }

    /// <summary>
    /// Multiple LET bindings with mixed visibility: some aliased (output), some hidden.
    /// </summary>
    [Fact]
    public async Task EndToEnd_MixedVisibility_AliasedAndHidden()
    {
        Row[] data =
        [
            MakeRow(("x", DataValue.FromFloat32(10)))
        ];
        TableCatalog catalog = CreateCatalog(("t", data));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT LET a = x + 5 AS \"visible\", LET b = a * 2, b AS result FROM t",
            catalog);

        Assert.Single(results);
        Assert.Equal(2, results[0].FieldCount);
        Assert.Equal(15f, results[0]["visible"].AsFloat32());
        Assert.Equal(30f, results[0]["result"].AsFloat32());
    }

    /// <summary>
    /// LET binding works with a function call in the expression.
    /// </summary>
    [Fact]
    public async Task EndToEnd_LetWithFunctionCall()
    {
        Row[] data =
        [
            MakeRow(("x", DataValue.FromFloat32(-7)))
        ];
        TableCatalog catalog = CreateCatalog(("t", data));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT LET magnitude = ABS(x), magnitude AS result FROM t",
            catalog);

        Assert.Single(results);
        Assert.Equal(7f, results[0]["result"].AsFloat32());
    }

    /// <summary>
    /// Multiple rows are processed correctly with LET bindings, each row
    /// getting its own independently computed LET values.
    /// </summary>
    [Fact]
    public async Task EndToEnd_MultipleRows_IndependentLetValues()
    {
        Row[] data =
        [
            MakeRow(("x", DataValue.FromFloat32(2))),
            MakeRow(("x", DataValue.FromFloat32(5))),
            MakeRow(("x", DataValue.FromFloat32(10)))
        ];
        TableCatalog catalog = CreateCatalog(("t", data));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT LET sq = x * x, sq AS squared FROM t", catalog);

        Assert.Equal(3, results.Count);
        Assert.Equal(4f, results[0]["squared"].AsFloat32());
        Assert.Equal(25f, results[1]["squared"].AsFloat32());
        Assert.Equal(100f, results[2]["squared"].AsFloat32());
    }

    // ─────────────── Helpers ───────────────

    private static SelectStatement ParseStatement(string sql)
    {
        return ((SelectQueryExpression)SqlParser.Parse(sql)).Statement;
    }

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
        QueryExpression query = SqlParser.Parse(sql);
        QueryPlanner planner = new(catalog, DefaultFunctions);
        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog);
        IQueryOperator plan = planner.Plan(query);

        List<Row> rows = [];
        await foreach (Row row in plan.ExecuteAsync(context))
        {
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>
    /// In-memory table provider implementing the full <see cref="ITableProvider"/> contract.
    /// </summary>
    private sealed class InMemoryTableProvider : ITableProvider
    {
        private readonly Row[] _rows;

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
