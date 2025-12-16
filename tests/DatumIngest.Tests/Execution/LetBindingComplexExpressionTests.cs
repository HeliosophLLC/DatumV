namespace DatumIngest.Tests.Execution;

using DatumIngest.Catalog;
using DatumIngest.Model;

/// <summary>
/// Verification sweep for LET bindings whose bodies contain non-trivial
/// expression kinds: aggregate functions, window functions, scalar subqueries.
/// Each kind has its own dedicated rewrite pass in the planner — these tests
/// confirm the LET binding mechanism cooperates with those passes end-to-end.
/// Sister to <see cref="LetBindingTests"/> (basic LET semantics) and
/// <see cref="LetBindingModelChainTests"/> (model-call interop).
/// </summary>
public sealed class LetBindingComplexExpressionTests : ServiceTestBase
{
    // ──────────────── Aggregates inside LET ────────────────

    /// <summary>
    /// A LET binding whose body is an aggregate, then referenced multiple
    /// times in the SELECT list. Each reference must resolve to the same
    /// GroupBy output column, not produce two independent SUM evaluations.
    /// </summary>
    [Fact]
    public async Task Aggregate_InLet_ReferencedMultipleTimes()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["category", "value"],
            ["A", 10f],
            ["A", 20f],
            ["B", 30f],
            ["B", 40f]);

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT LET total = SUM(value), " +
            "  category, total AS sum_total, total / 2 AS half_total " +
            "FROM t GROUP BY category",
            catalog);

        Assert.Equal(2, rows.Count);
        Row a = rows.First(r => r["category"].AsString() == "A");
        Row b = rows.First(r => r["category"].AsString() == "B");
        Assert.Equal(30f, a["sum_total"].AsFloat32());
        Assert.Equal(15f, a["half_total"].AsFloat32());
        Assert.Equal(70f, b["sum_total"].AsFloat32());
        Assert.Equal(35f, b["half_total"].AsFloat32());
    }

    /// <summary>
    /// Chained aggregate LET: <c>LET total = SUM(x), LET ratio = total / SUM(y)</c>.
    /// The second LET references both the first LET's output and another aggregate.
    /// Both aggregates must register on the same GroupBy.
    /// </summary>
    [Fact]
    public async Task Aggregate_InLet_ChainedWithSecondAggregate()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["category", "x", "y"],
            ["A", 10f, 5f],
            ["A", 20f, 5f],
            ["B", 30f, 10f]);

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT LET sum_x = SUM(x), LET ratio = sum_x / SUM(y), " +
            "  category, ratio AS x_to_y_ratio " +
            "FROM t GROUP BY category",
            catalog);

        Assert.Equal(2, rows.Count);
        // A: sum_x=30, sum_y=10, ratio=3.0
        // B: sum_x=30, sum_y=10, ratio=3.0
        Row a = rows.First(r => r["category"].AsString() == "A");
        Row b = rows.First(r => r["category"].AsString() == "B");
        Assert.Equal(3f, a["x_to_y_ratio"].AsFloat32());
        Assert.Equal(3f, b["x_to_y_ratio"].AsFloat32());
    }

    /// <summary>
    /// LET with COUNT(*) — the special "no arguments" aggregate. Distinct from
    /// COUNT(col) in the AST and in the aggregate registry; verifies the
    /// rewrite pass handles both shapes inside LET bodies.
    /// </summary>
    [Fact]
    public async Task Aggregate_CountStar_InLet()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["category"],
            new object?[] { "A" },
            new object?[] { "A" },
            new object?[] { "B" });

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT LET cnt = COUNT(*), category, cnt AS group_size FROM t GROUP BY category",
            catalog);

        Assert.Equal(2, rows.Count);
        Assert.Equal(2L, rows.First(r => r["category"].AsString() == "A")["group_size"].AsInt64());
        Assert.Equal(1L, rows.First(r => r["category"].AsString() == "B")["group_size"].AsInt64());
    }

    // ──────────────── Window functions inside LET ────────────────

    /// <summary>
    /// Window function inside a LET binding body. The planner's window-rewrite
    /// pass walks LET binding bodies and lifts the call into a
    /// <see cref="DatumIngest.Execution.Operators.WindowOperator"/>; the LET
    /// binding rewrites to a column reference that subsequent SELECT-list
    /// expressions can use.
    /// </summary>
    [Fact]
    public async Task Window_RowNumber_InLet()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["category", "value"],
            ["A", 30f],
            ["A", 10f],
            ["A", 20f],
            ["B", 50f]);

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT LET rn = ROW_NUMBER() OVER (PARTITION BY category ORDER BY value), " +
            "  category, value, rn AS within_group_rank " +
            "FROM t",
            catalog);

        Assert.Equal(4, rows.Count);
        Row a10 = rows.First(r => r["value"].AsFloat32() == 10f);
        Row a20 = rows.First(r => r["value"].AsFloat32() == 20f);
        Row a30 = rows.First(r => r["value"].AsFloat32() == 30f);
        Row b50 = rows.First(r => r["value"].AsFloat32() == 50f);
        Assert.Equal(1f, a10["within_group_rank"].AsFloat32());
        Assert.Equal(2f, a20["within_group_rank"].AsFloat32());
        Assert.Equal(3f, a30["within_group_rank"].AsFloat32());
        Assert.Equal(1f, b50["within_group_rank"].AsFloat32());
    }

    /// <summary>
    /// LET window-function output used in arithmetic in a subsequent SELECT
    /// column. Verifies the LET name resolves correctly when consumed by a
    /// non-trivial expression (not just a bare column reference).
    /// </summary>
    [Fact]
    public async Task Window_InLet_UsedInArithmetic()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["category", "value"],
            ["A", 10f],
            ["A", 20f],
            ["B", 30f]);

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT LET rn = ROW_NUMBER() OVER (PARTITION BY category ORDER BY value), " +
            "  category, rn * 100 AS scaled_rank " +
            "FROM t",
            catalog);

        Assert.Equal(3, rows.Count);
        // ROW_NUMBER() returns Float32 in this engine; arithmetic preserves Float32.
        Row a10 = rows.First(r => r["category"].AsString() == "A" && r["scaled_rank"].AsFloat32() == 100f);
        Row a20 = rows.First(r => r["category"].AsString() == "A" && r["scaled_rank"].AsFloat32() == 200f);
        Row b30 = rows.First(r => r["category"].AsString() == "B");
        Assert.NotEqual(default, a10);
        Assert.NotEqual(default, a20);
        Assert.Equal(100f, b30["scaled_rank"].AsFloat32());
    }

    // ──────────────── Scalar subqueries inside LET ────────────────

    /// <summary>
    /// LET binding whose body is a non-correlated scalar subquery. The
    /// subquery rewriter should lift it into a join (or one-shot eval) and
    /// the LET name resolves to the resulting column.
    /// </summary>
    [Fact]
    public async Task ScalarSubquery_InLet_NonCorrelated()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["price"],
            new object?[] { 10f },
            new object?[] { 20f },
            new object?[] { 30f });

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT LET avg_price = (SELECT AVG(price) FROM t), " +
            "  price, price - avg_price AS deviation " +
            "FROM t",
            catalog);

        Assert.Equal(3, rows.Count);
        // avg = 20.0, so deviations are -10, 0, 10.
        Assert.Equal(-10f, rows.First(r => r["price"].AsFloat32() == 10f)["deviation"].AsFloat32());
        Assert.Equal(0f, rows.First(r => r["price"].AsFloat32() == 20f)["deviation"].AsFloat32());
        Assert.Equal(10f, rows.First(r => r["price"].AsFloat32() == 30f)["deviation"].AsFloat32());
    }
}
