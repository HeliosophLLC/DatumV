using DatumIngest.Catalog;
using DatumIngest.Functions;
using DatumIngest.Model;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// End-to-end tests for <c>TABLESAMPLE BERNOULLI|SYSTEM(percentage) [REPEATABLE(seed)]</c>
/// execution, verifying approximate row counts and deterministic sampling.
/// </summary>
public sealed class TablesampleExecutionTests : ServiceTestBase
{
    private static readonly string[] RowColumns = ["id", "value"];
    private static readonly string[] ClassColumns = ["id", "label"];

    // ───────────────────── Bernoulli ─────────────────────

    /// <summary>
    /// TABLESAMPLE BERNOULLI(50) returns approximately half the rows.
    /// Tolerance of ±15% to account for random sampling variance on small datasets.
    /// </summary>
    [Fact]
    public async Task Bernoulli_ReturnsApproximatePercentage()
    {
        const int totalRows = 1000;
        object?[][] data = GenerateRows(totalRows);
        TableCatalog catalog = CreateCatalog("data", columns: RowColumns, rows: data);

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
        object?[][] data = GenerateRows(totalRows);
        TableCatalog catalog = CreateCatalog("data", columns: RowColumns, rows: data);

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
        object?[][] data = GenerateRows(totalRows);
        TableCatalog catalog = CreateCatalog("data", columns: RowColumns, rows: data);

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
        object?[][] data = GenerateRows(totalRows);
        TableCatalog catalog = CreateCatalog("data", columns: RowColumns, rows: data);

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
        object?[][] data = GenerateRows(totalRows);
        TableCatalog catalog = CreateCatalog("data", columns: RowColumns, rows: data);

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
        object?[][] data = GenerateRows(totalRows);
        TableCatalog catalog = CreateCatalog("data", columns: RowColumns, rows: data);

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
        object?[][] data = GenerateRows(totalRows);
        TableCatalog catalog = CreateCatalog("data", columns: RowColumns, rows: data);

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
        object?[][] data = GenerateRows(totalRows);
        TableCatalog catalog = CreateCatalog("data", columns: RowColumns, rows: data);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT * FROM data TABLESAMPLE BERNOULLI(1) REPEATABLE(42)",
            catalog);

        // With 1% of 1000, expect ~10 ± 15
        Assert.InRange(results.Count, 0, 50);
    }

    // ───────────────────── Stratified ─────────────────────

    /// <summary>
    /// TABLESAMPLE STRATIFIED(50) ON label preserves class proportions.
    /// </summary>
    [Fact]
    public async Task Stratified_E2E_PreservesDistribution()
    {
        object?[][] data = GenerateClassRows(("cat", 300), ("dog", 300), ("bird", 300));
        TableCatalog catalog = CreateCatalog("data", columns: ClassColumns, rows: data);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT * FROM data TABLESAMPLE STRATIFIED(50) ON label REPEATABLE(42)",
            catalog);

        // ~450 total (50% of 900), with roughly equal proportions.
        Assert.InRange(results.Count, 350, 550);

        int catCount = results.Count(r => r["label"].AsString() == "cat");
        int dogCount = results.Count(r => r["label"].AsString() == "dog");
        int birdCount = results.Count(r => r["label"].AsString() == "bird");

        // Each class should have roughly 50% of 300 = ~150 rows.
        Assert.InRange(catCount, 100, 200);
        Assert.InRange(dogCount, 100, 200);
        Assert.InRange(birdCount, 100, 200);
    }

    // ───────────────────── Balanced ─────────────────────

    /// <summary>
    /// TABLESAMPLE BALANCED(50) ON label returns exactly 50 per class.
    /// </summary>
    [Fact]
    public async Task Balanced_E2E_ExactCountPerClass()
    {
        object?[][] data = GenerateClassRows(("cat", 300), ("dog", 200), ("bird", 100));
        TableCatalog catalog = CreateCatalog("data", columns: ClassColumns, rows: data);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT * FROM data TABLESAMPLE BALANCED(50) ON label REPEATABLE(42)",
            catalog);

        Assert.Equal(150, results.Count);

        int catCount = results.Count(r => r["label"].AsString() == "cat");
        int dogCount = results.Count(r => r["label"].AsString() == "dog");
        int birdCount = results.Count(r => r["label"].AsString() == "bird");

        Assert.Equal(50, catCount);
        Assert.Equal(50, dogCount);
        Assert.Equal(50, birdCount);
    }

    /// <summary>
    /// TABLESAMPLE BALANCED returns all rows from a class that has fewer than the target.
    /// </summary>
    [Fact]
    public async Task Balanced_E2E_SmallClass()
    {
        object?[][] data = GenerateClassRows(("cat", 100), ("dog", 3));
        TableCatalog catalog = CreateCatalog("data", columns: ClassColumns, rows: data);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT * FROM data TABLESAMPLE BALANCED(50) ON label REPEATABLE(42)",
            catalog);

        int catCount = results.Count(r => r["label"].AsString() == "cat");
        int dogCount = results.Count(r => r["label"].AsString() == "dog");

        Assert.Equal(50, catCount);
        Assert.Equal(3, dogCount);
    }

    /// <summary>
    /// TABLESAMPLE BALANCED with REPEATABLE produces deterministic results.
    /// </summary>
    [Fact]
    public async Task Balanced_E2E_Deterministic()
    {
        object?[][] data = GenerateClassRows(("cat", 200), ("dog", 200));
        TableCatalog catalog = CreateCatalog("data", columns: ClassColumns, rows: data);

        List<Row> result1 = await ExecuteQueryAsync(
            "SELECT * FROM data TABLESAMPLE BALANCED(20) ON label REPEATABLE(42)",
            catalog);
        List<Row> result2 = await ExecuteQueryAsync(
            "SELECT * FROM data TABLESAMPLE BALANCED(20) ON label REPEATABLE(42)",
            catalog);

        Assert.Equal(result1.Count, result2.Count);
        for (int i = 0; i < result1.Count; i++)
        {
            Assert.Equal(result1[i]["id"].AsFloat32(), result2[i]["id"].AsFloat32());
        }
    }

    /// <summary>
    /// TABLESAMPLE STRATIFIED works with a WHERE clause (WHERE filters first).
    /// </summary>
    [Fact]
    public async Task Stratified_E2E_WithWhereClause()
    {
        object?[][] data = GenerateClassRows(("cat", 200), ("dog", 200));
        TableCatalog catalog = CreateCatalog("data", columns: ClassColumns, rows: data);

        // WHERE id >= 100 filters to ~300 rows, then sample 100%.
        List<Row> results = await ExecuteQueryAsync(
            "SELECT * FROM data TABLESAMPLE STRATIFIED(100) ON label WHERE id >= 100",
            catalog);

        // All rows with id >= 100 should be returned.
        Assert.All(results, r => Assert.True(r["id"].AsFloat32() >= 100));
    }

    // ───────────────────── Helpers ─────────────────────

    private static object?[][] GenerateClassRows(params (string label, int count)[] classes)
    {
        List<object?[]> rows = [];
        int id = 1;
        foreach ((string label, int count) in classes)
        {
            for (int i = 0; i < count; i++)
            {
                rows.Add([(float)(id++), label]);
            }
        }

        return rows.ToArray();
    }

    private static object?[][] GenerateRows(int count)
    {
        object?[][] rows = new object?[count][];
        for (int i = 0; i < count; i++)
        {
            rows[i] = [(float)i, i * 10f];
        }

        return rows;
    }
}
