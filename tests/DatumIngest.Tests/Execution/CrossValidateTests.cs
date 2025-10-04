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
/// End-to-end tests for CROSS VALIDATE fold assignment.
/// </summary>
public sealed class CrossValidateTests
{
    private static readonly FunctionRegistry DefaultFunctions = FunctionRegistry.CreateDefault();

    // ───────────────────── Basic fold assignment ─────────────────────

    [Fact]
    public async Task BasicFoldAssignment_ProducesKDistinctFolds()
    {
        Row[] data = GenerateRows(100);
        TableCatalog catalog = CreateCatalog(("data", data));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT *, fold FROM data CROSS VALIDATE(k = 5) ON id AS fold",
            catalog);

        Assert.Equal(100, results.Count);

        // All fold values should be in [0, 4].
        HashSet<int> folds = results.Select(r => (int)r["fold"].AsInt32()).ToHashSet();
        Assert.Equal(5, folds.Count);
        Assert.All(folds, f => Assert.InRange(f, 0, 4));
    }

    [Fact]
    public async Task FoldDistribution_ApproximatelyUniform()
    {
        Row[] data = GenerateRows(10000);
        TableCatalog catalog = CreateCatalog(("data", data));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT *, fold FROM data CROSS VALIDATE(k = 5, seed = 42) ON id AS fold",
            catalog);

        // Each fold should have ~2000 ± 500 rows.
        var foldCounts = results.GroupBy(r => (int)r["fold"].AsInt32())
            .ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(5, foldCounts.Count);
        foreach ((int fold, int count) in foldCounts)
        {
            Assert.InRange(count, 1500, 2500);
        }
    }

    // ───────────────────── Determinism ─────────────────────

    [Fact]
    public async Task DeterministicWithSeed_SameKeysSameFolds()
    {
        Row[] data = GenerateRows(100);
        TableCatalog catalog = CreateCatalog(("data", data));

        List<Row> result1 = await ExecuteQueryAsync(
            "SELECT id, fold FROM data CROSS VALIDATE(k = 5, seed = 42) ON id AS fold",
            catalog);
        List<Row> result2 = await ExecuteQueryAsync(
            "SELECT id, fold FROM data CROSS VALIDATE(k = 5, seed = 42) ON id AS fold",
            catalog);

        Assert.Equal(result1.Count, result2.Count);
        for (int i = 0; i < result1.Count; i++)
        {
            Assert.Equal(result1[i]["fold"].AsInt32(), result2[i]["fold"].AsInt32());
        }
    }

    [Fact]
    public async Task DifferentSeeds_DifferentAssignments()
    {
        Row[] data = GenerateRows(200);
        TableCatalog catalog = CreateCatalog(("data", data));

        List<Row> result1 = await ExecuteQueryAsync(
            "SELECT id, fold FROM data CROSS VALIDATE(k = 5, seed = 1) ON id AS fold",
            catalog);
        List<Row> result2 = await ExecuteQueryAsync(
            "SELECT id, fold FROM data CROSS VALIDATE(k = 5, seed = 99) ON id AS fold",
            catalog);

        // Different seeds should produce different fold assignments.
        bool anyDifference = false;
        for (int i = 0; i < result1.Count; i++)
        {
            if (result1[i]["fold"].AsInt32() != result2[i]["fold"].AsInt32())
            {
                anyDifference = true;
                break;
            }
        }

        Assert.True(anyDifference);
    }

    // ───────────────────── Fold range and type ─────────────────────

    [Fact]
    public async Task FoldRange_AlwaysZeroToKMinusOne()
    {
        Row[] data = GenerateRows(500);
        TableCatalog catalog = CreateCatalog(("data", data));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT *, fold FROM data CROSS VALIDATE(k = 3) ON id AS fold",
            catalog);

        Assert.All(results, r =>
        {
            int foldValue = r["fold"].AsInt32();
            Assert.InRange(foldValue, 0, 2);
        });
    }

    [Fact]
    public async Task FoldColumn_IsInt32()
    {
        Row[] data = GenerateRows(10);
        TableCatalog catalog = CreateCatalog(("data", data));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT *, fold FROM data CROSS VALIDATE(k = 5) ON id AS fold",
            catalog);

        Assert.NotEmpty(results);
        Assert.Equal(DataKind.Int32, results[0]["fold"].Kind);
    }

    // ───────────────────── Composite keys ─────────────────────

    [Fact]
    public async Task CompositeKey_DifferentCombinationsGetDifferentFolds()
    {
        Row[] data =
        [
            MakeRow(("user_id", DataValue.FromFloat32(1)), ("session_id", DataValue.FromFloat32(100))),
            MakeRow(("user_id", DataValue.FromFloat32(1)), ("session_id", DataValue.FromFloat32(200))),
            MakeRow(("user_id", DataValue.FromFloat32(2)), ("session_id", DataValue.FromFloat32(100))),
        ];
        TableCatalog catalog = CreateCatalog(("data", data));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT *, fold FROM data CROSS VALIDATE(k = 100, seed = 42) ON (user_id, session_id) AS fold",
            catalog);

        // With k=100 and 3 distinct composite keys, at least 2 should get different folds.
        HashSet<int> folds = results.Select(r => r["fold"].AsInt32()).ToHashSet();
        Assert.True(folds.Count >= 2);
    }

    // ───────────────────── Combined with other clauses ─────────────────────

    [Fact]
    public async Task WithWhere_FoldsAssignedAfterFilter()
    {
        Row[] data = GenerateRows(100);
        TableCatalog catalog = CreateCatalog(("data", data));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT *, fold FROM data WHERE id >= 50 CROSS VALIDATE(k = 5) ON id AS fold",
            catalog);

        // WHERE filters first — only rows with id >= 50 get folds.
        Assert.All(results, r => Assert.True(r["id"].AsFloat32() >= 50));
        Assert.True(results.Count < 100);
        Assert.All(results, r => Assert.InRange(r["fold"].AsInt32(), 0f, 4f));
    }

    [Fact]
    public async Task WithGroupByFold_GroupsByFoldCorrectly()
    {
        Row[] data = GenerateRows(500);
        TableCatalog catalog = CreateCatalog(("data", data));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT fold, COUNT(*) AS cnt FROM data CROSS VALIDATE(k = 5, seed = 42) ON id AS fold GROUP BY fold ORDER BY fold",
            catalog);

        // 5 folds → 5 grouped rows.
        Assert.Equal(5, results.Count);

        // Each fold should have a positive count, and they sum to 500.
        int total = 0;
        for (int i = 0; i < results.Count; i++)
        {
            Assert.Equal(i, results[i]["fold"].AsInt32());
            int cnt = (int)results[i]["cnt"].ToFloat();
            Assert.True(cnt > 0);
            total += cnt;
        }

        Assert.Equal(500, total);
    }

    [Fact]
    public async Task WithGroupByFoldAndLabel_GroupsByBoth()
    {
        Row[] data = GenerateRows(300);
        TableCatalog catalog = CreateCatalog(("data", data));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT fold, label, COUNT(*) AS cnt FROM data CROSS VALIDATE(k = 3, seed = 42) ON id AS fold GROUP BY fold, label ORDER BY fold, label",
            catalog);

        // 3 folds × 3 labels = 9 groups.
        Assert.Equal(9, results.Count);
    }

    [Fact]
    public async Task WithBalancedAndGroupByFold_Works()
    {
        // Use column names that match the user's Instacart scenario (product_id, department_id)
        Row[] data = Enumerable.Range(0, 300).Select(i => MakeRow(
            ("product_id", DataValue.FromFloat32(i)),
            ("department_id", DataValue.FromFloat32(i % 5)),
            ("name", DataValue.FromString($"product_{i}"))
        )).ToArray();
        TableCatalog catalog = CreateCatalog(("products", data));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT fold, department_id, COUNT(*) AS cnt FROM products TABLESAMPLE BALANCED(30) ON department_id REPEATABLE(42) CROSS VALIDATE(k = 3, seed = 7) ON product_id AS fold GROUP BY fold, department_id ORDER BY fold, department_id",
            catalog);

        // 3 folds × 5 departments = 15 groups
        Assert.Equal(15, results.Count);

        // All fold values should be 0, 1, or 2.
        Assert.All(results, r => Assert.InRange(r["fold"].AsInt32(), 0, 2));
    }

    [Fact]
    public async Task WithOrderByFold_SortsByFoldCorrectly()
    {
        Row[] data = GenerateRows(100);
        TableCatalog catalog = CreateCatalog(("data", data));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT *, fold FROM data CROSS VALIDATE(k = 5, seed = 42) ON id AS fold ORDER BY fold",
            catalog);

        // Verify rows are sorted by fold value.
        for (int i = 1; i < results.Count; i++)
        {
            Assert.True(results[i]["fold"].AsInt32() >= results[i - 1]["fold"].AsInt32());
        }
    }

    // ───────────────────── Default seed ─────────────────────

    [Fact]
    public async Task DefaultSeed_IsZero()
    {
        Row[] data = GenerateRows(100);
        TableCatalog catalog = CreateCatalog(("data", data));

        // No seed specified — should behave as seed = 0.
        List<Row> noSeed = await ExecuteQueryAsync(
            "SELECT id, fold FROM data CROSS VALIDATE(k = 5) ON id AS fold",
            catalog);
        List<Row> seedZero = await ExecuteQueryAsync(
            "SELECT id, fold FROM data CROSS VALIDATE(k = 5, seed = 0) ON id AS fold",
            catalog);

        Assert.Equal(noSeed.Count, seedZero.Count);
        for (int i = 0; i < noSeed.Count; i++)
        {
            Assert.Equal(noSeed[i]["fold"].AsInt32(), seedZero[i]["fold"].AsInt32());
        }
    }

    // ───────────────────── Helpers ─────────────────────

    private static Row[] GenerateRows(int count)
    {
        Row[] rows = new Row[count];
        for (int i = 0; i < count; i++)
        {
            rows[i] = MakeRow(
                ("id", DataValue.FromFloat32(i)),
                ("value", DataValue.FromFloat32(i * 10f)),
                ("label", DataValue.FromString(i % 3 == 0 ? "cat" : i % 3 == 1 ? "dog" : "bird")));
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

        return await plan.CollectRowsAsync(context);
    }

    private sealed class InMemoryTableProvider : ITableProvider
    {
        private readonly Row[] _rows;

        public InMemoryTableProvider(Row[] rows) { _rows = rows; }

        public Task<Schema> GetSchemaAsync(TableDescriptor descriptor, CancellationToken cancellationToken)
        {
            if (_rows.Length == 0)
                return Task.FromResult(new Schema([new ColumnInfo("empty", DataKind.String, nullable: true)]));

            List<ColumnInfo> columns = [];
            foreach (string name in _rows[0].ColumnNames)
                columns.Add(new ColumnInfo(name, _rows[0][name].Kind, nullable: true));

            return Task.FromResult(new Schema(columns));
        }

        public Task<ProviderCapabilities> GetCapabilitiesAsync(
            TableDescriptor descriptor, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ProviderCapabilities(
                EstimatedRowCount: _rows.Length,
                EstimatedRowSizeBytes: null,
                SupportsSeek: false,
                ColumnCosts: new Dictionary<string, ColumnCost>()));
        }

        public async IAsyncEnumerable<RowBatch> OpenAsync(
            TableDescriptor descriptor,
            IReadOnlySet<string>? requiredColumns,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            RowBatch batch = RowBatch.Rent(64);
            foreach (Row row in _rows)
            {
                batch.Add(row);
                if (batch.IsFull) { yield return batch; batch = RowBatch.Rent(64); }
            }

            if (batch.Count > 0) yield return batch;
            await Task.CompletedTask;
        }
    }
}
