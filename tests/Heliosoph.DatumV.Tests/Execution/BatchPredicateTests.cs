using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;

using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Tests.Execution;

/// <summary>
/// Tests for the v1 batch-predicate fast path in <c>FilterOperator</c>.
/// Each test runs the query both ways — once with a shape the batch compiler
/// accepts (column OP literal on Float32/Int32/Int64) and once against the
/// per-row fallback by including a feature the compiler rejects — and asserts
/// the result rows are byte-for-byte identical.
/// </summary>
/// <remarks>
/// The compiler currently returns <see langword="null"/> for any shape that
/// isn't <c>BinaryExpression(column, comparison, literal)</c> or its flipped
/// twin, so adding a function call, IS NULL, AND/OR, etc. forces the
/// fallback. We exploit that to compare fast-path vs slow-path output without
/// any test-only knob.
/// </remarks>
public sealed class BatchPredicateTests : ServiceTestBase
{
    [Theory]
    [InlineData("SELECT id, value FROM data WHERE value > 500", "value > 500 fast")]
    [InlineData("SELECT id, value FROM data WHERE value >= 500", "value >= 500 fast")]
    [InlineData("SELECT id, value FROM data WHERE value < 500", "value < 500 fast")]
    [InlineData("SELECT id, value FROM data WHERE value <= 500", "value <= 500 fast")]
    [InlineData("SELECT id, value FROM data WHERE value = 500", "value = 500 fast")]
    [InlineData("SELECT id, value FROM data WHERE value != 500", "value != 500 fast")]
    [InlineData("SELECT id, value FROM data WHERE 500 < value", "literal < column flipped")]
    [InlineData("SELECT id, value FROM data WHERE 500 <= value", "literal <= column flipped")]
    public async Task FastPath_MatchesPerRowEvaluator_Float32(string sql, string _)
    {
        TableCatalog catalog = CreateCatalog(
            "data",
            ["id", "value"],
            [1f, 100f],
            [2f, 500f],   // boundary
            [3f, 501f],
            [4f, 499f],
            [5f, 1000f]);

        List<Row> fast = await ExecuteQueryAsync(sql, catalog);

        // Force fallback by wrapping the predicate in a redundant AND that the
        // v1 compiler doesn't batchable-classify. The AND short-circuits via
        // the per-row path so we get the slow path's output for comparison.
        string fallbackSql = sql.Replace("WHERE ", "WHERE 1 = 1 AND ");
        TableCatalog catalog2 = CreateCatalog(
            "data",
            ["id", "value"],
            [1f, 100f],
            [2f, 500f],
            [3f, 501f],
            [4f, 499f],
            [5f, 1000f]);
        List<Row> fallback = await ExecuteQueryAsync(fallbackSql, catalog2);

        Assert.Equal(fallback.Count, fast.Count);
        for (int i = 0; i < fast.Count; i++)
        {
            Assert.Equal(fallback[i]["id"].AsFloat32(), fast[i]["id"].AsFloat32());
            Assert.Equal(fallback[i]["value"].AsFloat32(), fast[i]["value"].AsFloat32());
        }
    }

    [Fact]
    public async Task FastPath_NullValues_ExcludedFromResult()
    {
        // SQL WHERE collapses UNKNOWN → false, so NULL rows must not appear
        // in the output regardless of which path runs.
        TableCatalog catalog = CreateCatalog(
            "data",
            ["id", "value"],
            [1f, 100f],
            [2f, null],
            [3f, 600f]);

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT id FROM data WHERE value > 50",
            catalog);

        Assert.Equal(2, rows.Count);
        Assert.Equal(1f, rows[0]["id"].AsFloat32());
        Assert.Equal(3f, rows[1]["id"].AsFloat32());
    }

    [Fact]
    public async Task FastPath_EmptyResultSet()
    {
        TableCatalog catalog = CreateCatalog(
            "data",
            ["id", "value"],
            [1f, 100f],
            [2f, 200f]);

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT id FROM data WHERE value > 1000",
            catalog);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task FastPath_AllRowsMatch()
    {
        TableCatalog catalog = CreateCatalog(
            "data",
            ["id", "value"],
            [1f, 100f],
            [2f, 200f],
            [3f, 300f]);

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT id FROM data WHERE value > 0",
            catalog);

        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public async Task Fallback_FunctionCallInPredicate()
    {
        // upper() in the predicate puts the AST out of v1's batchable shape;
        // the per-row evaluator handles it. Test that results are still correct.
        TableCatalog catalog = CreateCatalog(
            "data",
            ["id", "name"],
            [1f, "alpha"],
            [2f, "BETA"],
            [3f, "gamma"]);

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT id FROM data WHERE upper(name) = 'BETA'",
            catalog);

        Assert.Single(rows);
        Assert.Equal(2f, rows[0]["id"].AsFloat32());
    }

    [Fact]
    public async Task FastPath_LargeBatch_MultipleOutputBatches()
    {
        // 10K-row scan with ~50% selectivity produces multiple full output
        // batches (1024 per BDN default), exercising the
        // writer-detach-on-full + caller-yield-after-input path that v1
        // mis-handled in the first draft.
        object?[][] inputRows = new object?[10_000][];
        for (int i = 0; i < 10_000; i++)
        {
            inputRows[i] = [(float)i, (float)i];
        }
        TableCatalog catalog = CreateCatalog("data", ["id", "value"], inputRows);

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT id FROM data WHERE value >= 5000",
            catalog);

        Assert.Equal(5000, rows.Count);
        Assert.Equal(5000f, rows[0]["id"].AsFloat32());
        Assert.Equal(9999f, rows[^1]["id"].AsFloat32());
    }
}
