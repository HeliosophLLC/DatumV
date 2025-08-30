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
/// End-to-end tests for LATERAL JOIN and CROSS/OUTER APPLY execution,
/// covering table-valued function sources and subquery sources with
/// correlated column references.
/// </summary>
public sealed class LateralJoinTests
{
    private static readonly FunctionRegistry DefaultFunctions = FunctionRegistry.CreateDefault();

    // ───────────────── CROSS JOIN LATERAL with TVF ─────────────────

    /// <summary>
    /// CROSS JOIN LATERAL UNNEST expands a vector column per row, producing
    /// one output row per element. Rows with no elements are excluded.
    /// </summary>
    [Fact]
    public async Task CrossJoinLateral_Unnest_ExpandsVectorPerRow()
    {
        Row[] data =
        [
            MakeRow(("name", DataValue.FromString("alice")), ("scores", DataValue.FromVector([1f, 2f, 3f]))),
            MakeRow(("name", DataValue.FromString("bob")), ("scores", DataValue.FromVector([10f, 20f]))),
        ];

        TableCatalog catalog = CreateCatalog(("data", data));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT data.name, s.value FROM data CROSS JOIN LATERAL UNNEST(data.scores) AS s",
            catalog);

        Assert.Equal(5, results.Count);
        Assert.Equal("alice", results[0]["name"].AsString());
        Assert.Equal(1f, results[0]["value"].AsFloat32());
        Assert.Equal("alice", results[1]["name"].AsString());
        Assert.Equal(2f, results[1]["value"].AsFloat32());
        Assert.Equal("alice", results[2]["name"].AsString());
        Assert.Equal(3f, results[2]["value"].AsFloat32());
        Assert.Equal("bob", results[3]["name"].AsString());
        Assert.Equal(10f, results[3]["value"].AsFloat32());
        Assert.Equal("bob", results[4]["name"].AsString());
        Assert.Equal(20f, results[4]["value"].AsFloat32());
    }

    /// <summary>
    /// CROSS APPLY is a T-SQL alias for CROSS JOIN LATERAL.
    /// </summary>
    [Fact]
    public async Task CrossApply_BehavesAsCrossJoinLateral()
    {
        Row[] data =
        [
            MakeRow(("name", DataValue.FromString("alice")), ("scores", DataValue.FromVector([1f, 2f]))),
        ];

        TableCatalog catalog = CreateCatalog(("data", data));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT data.name, s.value FROM data CROSS APPLY UNNEST(data.scores) AS s",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal("alice", results[0]["name"].AsString());
        Assert.Equal(1f, results[0]["value"].AsFloat32());
    }

    // ───────────────── LEFT JOIN LATERAL ─────────────────

    /// <summary>
    /// LEFT JOIN LATERAL preserves outer rows that produce no inner rows,
    /// padding the right side with NULLs.
    /// </summary>
    [Fact]
    public async Task LeftJoinLateral_PreservesUnmatchedOuterRows()
    {
        Row[] data =
        [
            MakeRow(("name", DataValue.FromString("alice")), ("scores", DataValue.FromVector([1f, 2f]))),
            MakeRow(("name", DataValue.FromString("bob")), ("scores", DataValue.FromVector([]))),
            MakeRow(("name", DataValue.FromString("carol")), ("scores", DataValue.FromVector([5f]))),
        ];

        TableCatalog catalog = CreateCatalog(("data", data));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT data.name, s.value FROM data LEFT JOIN LATERAL UNNEST(data.scores) AS s",
            catalog);

        // alice: 2 rows, bob: 1 null-padded row, carol: 1 row → 4 total.
        Assert.Equal(4, results.Count);
        Assert.Equal("alice", results[0]["name"].AsString());
        Assert.Equal(1f, results[0]["value"].AsFloat32());
        Assert.Equal("alice", results[1]["name"].AsString());
        Assert.Equal(2f, results[1]["value"].AsFloat32());

        // Bob has empty vector → LEFT preserves with NULL value.
        Assert.Equal("bob", results[2]["name"].AsString());
        Assert.True(results[2]["value"].IsNull);

        Assert.Equal("carol", results[3]["name"].AsString());
        Assert.Equal(5f, results[3]["value"].AsFloat32());
    }

    /// <summary>
    /// OUTER APPLY is a T-SQL alias for LEFT JOIN LATERAL.
    /// </summary>
    [Fact]
    public async Task OuterApply_BehavesAsLeftJoinLateral()
    {
        Row[] data =
        [
            MakeRow(("name", DataValue.FromString("alice")), ("scores", DataValue.FromVector([1f]))),
            MakeRow(("name", DataValue.FromString("bob")), ("scores", DataValue.FromVector([]))),
        ];

        TableCatalog catalog = CreateCatalog(("data", data));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT data.name, s.value FROM data OUTER APPLY UNNEST(data.scores) AS s",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal("alice", results[0]["name"].AsString());
        Assert.Equal(1f, results[0]["value"].AsFloat32());
        Assert.Equal("bob", results[1]["name"].AsString());
        Assert.True(results[1]["value"].IsNull);
    }

    // ───────────────── LATERAL with subquery source ─────────────────

    /// <summary>
    /// LEFT JOIN LATERAL with a subquery source that references outer columns
    /// via a correlated WHERE clause.
    /// </summary>
    [Fact]
    public async Task LeftJoinLateral_CorrelatedSubquery()
    {
        Row[] orders =
        [
            MakeRow(("id", DataValue.FromFloat32(1f)), ("customer", DataValue.FromString("alice"))),
            MakeRow(("id", DataValue.FromFloat32(2f)), ("customer", DataValue.FromString("bob"))),
            MakeRow(("id", DataValue.FromFloat32(3f)), ("customer", DataValue.FromString("carol"))),
        ];

        Row[] items =
        [
            MakeRow(("order_id", DataValue.FromFloat32(1f)), ("product", DataValue.FromString("widget"))),
            MakeRow(("order_id", DataValue.FromFloat32(1f)), ("product", DataValue.FromString("gadget"))),
            MakeRow(("order_id", DataValue.FromFloat32(3f)), ("product", DataValue.FromString("doohickey"))),
        ];

        TableCatalog catalog = CreateCatalog(("orders", orders), ("items", items));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT orders.customer, sub.product " +
            "FROM orders " +
            "LEFT JOIN LATERAL (SELECT items.product FROM items WHERE items.order_id = orders.id) AS sub ON 1 = 1",
            catalog);

        // alice: 2 items, bob: 0 items (null-padded), carol: 1 item → 4 total.
        Assert.Equal(4, results.Count);
        Assert.Equal("alice", results[0]["customer"].AsString());
        Assert.Equal("widget", results[0]["product"].AsString());
        Assert.Equal("alice", results[1]["customer"].AsString());
        Assert.Equal("gadget", results[1]["product"].AsString());
        Assert.Equal("bob", results[2]["customer"].AsString());
        Assert.True(results[2]["product"].IsNull);
        Assert.Equal("carol", results[3]["customer"].AsString());
        Assert.Equal("doohickey", results[3]["product"].AsString());
    }

    /// <summary>
    /// CROSS JOIN LATERAL with a correlated subquery excludes outer rows
    /// that produce no inner matches.
    /// </summary>
    [Fact]
    public async Task CrossJoinLateral_CorrelatedSubquery_ExcludesUnmatchedRows()
    {
        Row[] orders =
        [
            MakeRow(("id", DataValue.FromFloat32(1f)), ("customer", DataValue.FromString("alice"))),
            MakeRow(("id", DataValue.FromFloat32(2f)), ("customer", DataValue.FromString("bob"))),
        ];

        Row[] items =
        [
            MakeRow(("order_id", DataValue.FromFloat32(1f)), ("product", DataValue.FromString("widget"))),
        ];

        TableCatalog catalog = CreateCatalog(("orders", orders), ("items", items));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT orders.customer, sub.product " +
            "FROM orders " +
            "CROSS JOIN LATERAL (SELECT items.product FROM items WHERE items.order_id = orders.id) AS sub",
            catalog);

        // Only alice has items; bob is excluded (cross join semantics).
        Assert.Single(results);
        Assert.Equal("alice", results[0]["customer"].AsString());
        Assert.Equal("widget", results[0]["product"].AsString());
    }

    // ───────────────── Helper infrastructure ─────────────────

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

        ExecutionContext context = new(
            CancellationToken.None,
            DefaultFunctions,
            catalog, new LocalBufferPool());

        IQueryOperator plan = await planner.PlanWithSubqueriesAsync(query, context, CancellationToken.None);

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
