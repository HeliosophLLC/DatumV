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
/// End-to-end tests for <c>TABLESAMPLE BERNOULLI|SYSTEM(percentage) [REPEATABLE(seed)]</c>
/// execution, verifying approximate row counts and deterministic sampling.
/// </summary>
public sealed class TablesampleExecutionTests
{
    private static readonly FunctionRegistry DefaultFunctions = FunctionRegistry.CreateDefault();

    // ───────────────────── Bernoulli ─────────────────────

    /// <summary>
    /// TABLESAMPLE BERNOULLI(50) returns approximately half the rows.
    /// Tolerance of ±15% to account for random sampling variance on small datasets.
    /// </summary>
    [Fact]
    public async Task Bernoulli_ReturnsApproximatePercentage()
    {
        const int totalRows = 1000;
        Row[] data = GenerateRows(totalRows);
        TableCatalog catalog = CreateCatalog(("data", data));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT * FROM data TABLESAMPLE BERNOULLI(50) REPEATABLE(42)",
            catalog);

        // With 1000 rows and 50%, expect ~500 ± 150
        Assert.InRange(results.Count, 350, 650);
    }

    /// <summary>
    /// TABLESAMPLE BERNOULLI(100) returns all rows.
    /// </summary>
    [Fact]
    public async Task Bernoulli_100Percent_ReturnsAllRows()
    {
        const int totalRows = 100;
        Row[] data = GenerateRows(totalRows);
        TableCatalog catalog = CreateCatalog(("data", data));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT * FROM data TABLESAMPLE BERNOULLI(100)",
            catalog);

        Assert.Equal(totalRows, results.Count);
    }

    /// <summary>
    /// TABLESAMPLE BERNOULLI(0) returns no rows.
    /// </summary>
    [Fact]
    public async Task Bernoulli_0Percent_ReturnsNoRows()
    {
        const int totalRows = 100;
        Row[] data = GenerateRows(totalRows);
        TableCatalog catalog = CreateCatalog(("data", data));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT * FROM data TABLESAMPLE BERNOULLI(0)",
            catalog);

        Assert.Empty(results);
    }

    // ───────────────────── REPEATABLE determinism ─────────────────────

    /// <summary>
    /// REPEATABLE(seed) produces identical results across executions.
    /// </summary>
    [Fact]
    public async Task Repeatable_ProducesDeterministicResults()
    {
        const int totalRows = 500;
        Row[] data = GenerateRows(totalRows);
        TableCatalog catalog = CreateCatalog(("data", data));

        List<Row> first = await ExecuteQueryAsync(
            "SELECT * FROM data TABLESAMPLE BERNOULLI(30) REPEATABLE(12345)",
            catalog);

        List<Row> second = await ExecuteQueryAsync(
            "SELECT * FROM data TABLESAMPLE BERNOULLI(30) REPEATABLE(12345)",
            catalog);

        Assert.Equal(first.Count, second.Count);
        for (int i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i]["id"].AsFloat32(), second[i]["id"].AsFloat32());
        }
    }

    /// <summary>
    /// Different seeds produce different results.
    /// </summary>
    [Fact]
    public async Task DifferentSeeds_ProduceDifferentResults()
    {
        const int totalRows = 500;
        Row[] data = GenerateRows(totalRows);
        TableCatalog catalog = CreateCatalog(("data", data));

        List<Row> seed1 = await ExecuteQueryAsync(
            "SELECT * FROM data TABLESAMPLE BERNOULLI(30) REPEATABLE(1)",
            catalog);

        List<Row> seed2 = await ExecuteQueryAsync(
            "SELECT * FROM data TABLESAMPLE BERNOULLI(30) REPEATABLE(2)",
            catalog);

        // Very unlikely to get identical row sets with different seeds
        bool anyDifference = seed1.Count != seed2.Count;
        if (!anyDifference && seed1.Count > 0)
        {
            anyDifference = Enumerable.Range(0, Math.Min(seed1.Count, seed2.Count))
                .Any(i => seed1[i]["id"].AsFloat32() != seed2[i]["id"].AsFloat32());
        }

        Assert.True(anyDifference, "Different seeds should generally produce different row sets");
    }

    // ───────────────────── System ─────────────────────

    /// <summary>
    /// TABLESAMPLE SYSTEM falls back to Bernoulli row-level sampling without a source index.
    /// </summary>
    [Fact]
    public async Task System_ReturnsApproximatePercentage()
    {
        const int totalRows = 1000;
        Row[] data = GenerateRows(totalRows);
        TableCatalog catalog = CreateCatalog(("data", data));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT * FROM data TABLESAMPLE SYSTEM(50) REPEATABLE(42)",
            catalog);

        Assert.InRange(results.Count, 350, 650);
    }

    // ───────────────────── With alias ─────────────────────

    /// <summary>
    /// TABLESAMPLE works correctly when combined with a table alias.
    /// </summary>
    [Fact]
    public async Task Tablesample_WithAlias_WorksCorrectly()
    {
        const int totalRows = 100;
        Row[] data = GenerateRows(totalRows);
        TableCatalog catalog = CreateCatalog(("data", data));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT d.id FROM data TABLESAMPLE BERNOULLI(100) AS d",
            catalog);

        Assert.Equal(totalRows, results.Count);
    }

    // ───────────────────── Small percentage ─────────────────────

    /// <summary>
    /// Small percentages produce few rows.
    /// </summary>
    [Fact]
    public async Task Bernoulli_SmallPercentage_ProducesFewRows()
    {
        const int totalRows = 1000;
        Row[] data = GenerateRows(totalRows);
        TableCatalog catalog = CreateCatalog(("data", data));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT * FROM data TABLESAMPLE BERNOULLI(1) REPEATABLE(42)",
            catalog);

        // With 1% of 1000, expect ~10 ± 15
        Assert.InRange(results.Count, 0, 50);
    }

    // ───────────────────── Helpers ─────────────────────

    private static Row[] GenerateRows(int count)
    {
        Row[] rows = new Row[count];
        for (int i = 0; i < count; i++)
        {
            rows[i] = MakeRow(
                ("id", DataValue.FromFloat32(i)),
                ("value", DataValue.FromFloat32(i * 10f)));
        }

        return rows;
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

        /// <summary>Creates an in-memory table provider.</summary>
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
