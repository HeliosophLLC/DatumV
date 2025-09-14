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
/// Tests for <c>GROUP BY ALL</c> — projection-derived grouping that infers
/// group keys from non-aggregate columns in the SELECT list.
/// </summary>
public sealed class GroupByAllTests
{
    private static readonly FunctionRegistry DefaultFunctions = FunctionRegistry.CreateDefault();

    // ─────────────── Basic resolution ───────────────

    /// <summary>
    /// GROUP BY ALL infers grouping keys from the two non-aggregate columns
    /// and produces the same result as an explicit GROUP BY.
    /// </summary>
    [Fact]
    public async Task GroupByAll_InfersKeysFromNonAggregateColumns()
    {
        Row[] data =
        [
            MakeRow(("department", "A"), ("region", "North"), ("amount", 10f)),
            MakeRow(("department", "A"), ("region", "North"), ("amount", 20f)),
            MakeRow(("department", "A"), ("region", "South"), ("amount", 30f)),
            MakeRow(("department", "B"), ("region", "North"), ("amount", 40f)),
        ];
        TableCatalog catalog = CreateCatalog(("sales", data));

        List<Row> allResults = await ExecuteQueryAsync(
            "SELECT department, region, SUM(amount) AS total FROM sales GROUP BY ALL",
            catalog);

        List<Row> explicitResults = await ExecuteQueryAsync(
            "SELECT department, region, SUM(amount) AS total FROM sales GROUP BY department, region",
            catalog);

        Assert.Equal(explicitResults.Count, allResults.Count);

        // Both should produce 3 groups: (A,North), (A,South), (B,North)
        Assert.Equal(3, allResults.Count);
    }

    /// <summary>
    /// GROUP BY ALL with a single non-aggregate column and COUNT(*).
    /// </summary>
    [Fact]
    public async Task GroupByAll_SingleGroupKey()
    {
        Row[] data =
        [
            MakeRow(("category", "X"), ("value", 1f)),
            MakeRow(("category", "X"), ("value", 2f)),
            MakeRow(("category", "Y"), ("value", 3f)),
        ];
        TableCatalog catalog = CreateCatalog(("t", data));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT category, COUNT(*) AS n FROM t GROUP BY ALL",
            catalog);

        Assert.Equal(2, results.Count);
        Row rowX = results.First(r => r["category"].AsString() == "X");
        Row rowY = results.First(r => r["category"].AsString() == "Y");
        Assert.Equal(2f, rowX["n"].AsFloat32());
        Assert.Equal(1f, rowY["n"].AsFloat32());
    }

    // ─────────────── Computed expressions ───────────────

    /// <summary>
    /// GROUP BY ALL correctly treats a non-aggregate expression that wraps a column
    /// as a grouping key, matching the behavior of explicit GROUP BY.
    /// </summary>
    [Fact]
    public async Task GroupByAll_WithMixedColumnsAndAggregates()
    {
        Row[] data =
        [
            MakeRow(("a", "X"), ("b", "P"), ("v", 1f)),
            MakeRow(("a", "X"), ("b", "Q"), ("v", 2f)),
            MakeRow(("a", "Y"), ("b", "P"), ("v", 3f)),
            MakeRow(("a", "Y"), ("b", "P"), ("v", 4f)),
        ];
        TableCatalog catalog = CreateCatalog(("t", data));

        // Three non-aggregate columns (a, b) and two aggregates.
        List<Row> results = await ExecuteQueryAsync(
            "SELECT a, b, COUNT(*) AS n, SUM(v) AS total FROM t GROUP BY ALL",
            catalog);

        // Three distinct (a, b) pairs: (X,P), (X,Q), (Y,P)
        Assert.Equal(3, results.Count);
        Row yp = results.First(r => r["a"].AsString() == "Y" && r["b"].AsString() == "P");
        Assert.Equal(2f, yp["n"].AsFloat32());
        Assert.Equal(7f, yp["total"].AsFloat32());
    }

    // ─────────────── Multiple aggregates ───────────────

    /// <summary>
    /// GROUP BY ALL works when multiple aggregate functions appear in the SELECT list.
    /// </summary>
    [Fact]
    public async Task GroupByAll_MultipleAggregates()
    {
        Row[] data =
        [
            MakeRow(("region", "East"), ("sales", 10f)),
            MakeRow(("region", "East"), ("sales", 20f)),
            MakeRow(("region", "West"), ("sales", 30f)),
        ];
        TableCatalog catalog = CreateCatalog(("t", data));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT region, COUNT(*) AS n, SUM(sales) AS total, AVG(sales) AS avg_sales FROM t GROUP BY ALL",
            catalog);

        Assert.Equal(2, results.Count);
        Row east = results.First(r => r["region"].AsString() == "East");
        Assert.Equal(2f, east["n"].AsFloat32());
        Assert.Equal(30f, east["total"].AsFloat32());
        Assert.Equal(15.0, east["avg_sales"].AsFloat64());
    }

    // ─────────────── With HAVING ───────────────

    /// <summary>
    /// GROUP BY ALL works with a HAVING clause to filter groups.
    /// </summary>
    [Fact]
    public async Task GroupByAll_WithHaving()
    {
        Row[] data =
        [
            MakeRow(("category", "A"), ("value", 5f)),
            MakeRow(("category", "A"), ("value", 10f)),
            MakeRow(("category", "B"), ("value", 1f)),
        ];
        TableCatalog catalog = CreateCatalog(("t", data));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT category, SUM(value) AS total FROM t GROUP BY ALL HAVING SUM(value) > 5",
            catalog);

        Assert.Single(results);
        Assert.Equal("A", results[0]["category"].AsString());
        Assert.Equal(15f, results[0]["total"].AsFloat32());
    }

    // ─────────────── With ORDER BY and LIMIT ───────────────

    /// <summary>
    /// GROUP BY ALL integrates correctly with ORDER BY and LIMIT.
    /// </summary>
    [Fact]
    public async Task GroupByAll_WithOrderByAndLimit()
    {
        Row[] data =
        [
            MakeRow(("category", "A"), ("value", 30f)),
            MakeRow(("category", "B"), ("value", 10f)),
            MakeRow(("category", "C"), ("value", 20f)),
        ];
        TableCatalog catalog = CreateCatalog(("t", data));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT category, SUM(value) AS total FROM t GROUP BY ALL ORDER BY total DESC LIMIT 2",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal("A", results[0]["category"].AsString());
        Assert.Equal("C", results[1]["category"].AsString());
    }

    // ─────────────── Helpers ───────────────

    private static Row MakeRow(params (string Name, object Value)[] columns)
    {
        string[] names = columns.Select(c => c.Name).ToArray();
        DataValue[] values = columns.Select(c => c.Value switch
        {
            float f => DataValue.FromFloat32(f),
            string s => DataValue.FromString(s),
            _ => throw new ArgumentException($"Unsupported test value type: {c.Value.GetType()}")
        }).ToArray();
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
        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog, new LocalBufferPool());
        IQueryOperator plan = planner.Plan(query);

        List<Row> rows = [];
        await foreach (RowBatch batch in plan.ExecuteAsync(context))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                rows.Add(batch[i]);
            }
        }

        return rows;
    }

    /// <summary>
    /// In-memory table provider for test data.
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
        public async IAsyncEnumerable<RowBatch> OpenAsync(
            TableDescriptor descriptor,
            IReadOnlySet<string>? requiredColumns,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            RowBatch batch = RowBatch.Rent(64);

            foreach (Row row in _rows)
            {
                if (batch.Count >= batch.Capacity)
                {
                    yield return batch;
                    batch = RowBatch.Rent(64);
                }

                batch.Add(row);
            }

            if (batch.Count > 0)
            {
                yield return batch;
            }

            await Task.CompletedTask;
        }
    }
}
