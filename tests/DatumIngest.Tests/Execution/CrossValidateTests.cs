using DatumIngest.Catalog;
using DatumIngest.Model;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// End-to-end tests for CROSS VALIDATE fold assignment.
/// </summary>
public sealed class CrossValidateTests : ServiceTestBase
{
    private static readonly string[] DataColumns = ["id", "value", "label"];

    // ───────────────────── Basic fold assignment ─────────────────────

    [Fact]
    public async Task BasicFoldAssignment_ProducesKDistinctFolds()
    {
        object?[][] data = GenerateRows(100);
        TableCatalog catalog = CreateCatalog("data", columns: DataColumns, rows: data);

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
        object?[][] data = GenerateRows(10000);
        TableCatalog catalog = CreateCatalog("data", columns: DataColumns, rows: data);

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
        object?[][] data = GenerateRows(100);
        TableCatalog catalog = CreateCatalog("data", columns: DataColumns, rows: data);

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
        object?[][] data = GenerateRows(200);
        TableCatalog catalog = CreateCatalog("data", columns: DataColumns, rows: data);

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
        object?[][] data = GenerateRows(500);
        TableCatalog catalog = CreateCatalog("data", columns: DataColumns, rows: data);

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
        object?[][] data = GenerateRows(10);
        TableCatalog catalog = CreateCatalog("data", columns: DataColumns, rows: data);

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
        TableCatalog catalog = CreateCatalog("data",
            columns: ["user_id", "session_id"],
            [1f, 100f],
            [1f, 200f],
            [2f, 100f]);

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
        object?[][] data = GenerateRows(100);
        TableCatalog catalog = CreateCatalog("data", columns: DataColumns, rows: data);

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
        object?[][] data = GenerateRows(500);
        TableCatalog catalog = CreateCatalog("data", columns: DataColumns, rows: data);

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
        object?[][] data = GenerateRows(300);
        TableCatalog catalog = CreateCatalog("data", columns: DataColumns, rows: data);

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
        object?[][] data = Enumerable.Range(0, 300)
            .Select(i => new object?[] { (float)i, (float)(i % 5), $"product_{i}" })
            .ToArray();
        TableCatalog catalog = CreateCatalog("products",
            columns: ["product_id", "department_id", "name"],
            rows: data);

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
        object?[][] data = GenerateRows(100);
        TableCatalog catalog = CreateCatalog("data", columns: DataColumns, rows: data);

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
        object?[][] data = GenerateRows(100);
        TableCatalog catalog = CreateCatalog("data", columns: DataColumns, rows: data);

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

    private static object?[][] GenerateRows(int count)
    {
        object?[][] rows = new object?[count][];
        for (int i = 0; i < count; i++)
        {
            rows[i] = [
                (float)i,
                i * 10f,
                i % 3 == 0 ? "cat" : i % 3 == 1 ? "dog" : "bird",
            ];
        }

        return rows;
    }
}
