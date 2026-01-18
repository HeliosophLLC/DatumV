using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// End-to-end coverage for the per-aggregate <c>WITHIN GROUP</c>
/// semantics that the planner applies. Drives SQL through the parser
/// + planner + executor so the parse-time AST decision and the
/// plan-time arg arrangement are tested together.
/// </summary>
public sealed class WithinGroupSemanticsTests : ServiceTestBase
{
    private async Task<List<Row>> RunAsync(TableCatalog catalog, string sql)
    {
        IQueryPlan plan = catalog.Plan(sql);
        DatumIngest.Execution.ExecutionContext ctx =
            CreateExecutionContext(catalog: catalog);
        List<Row> result = new();
        await foreach (RowBatch batch in plan.ExecuteAsync(ctx.CancellationToken))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                Row row = batch[i];
                DataValue[] copies = new DataValue[row.ColumnLookup.Count];
                for (int c = 0; c < copies.Length; c++) copies[c] = row[c];
                result.Add(new Row(row.ColumnLookup, copies));
            }
        }
        return result;
    }

    // ───────── Sort-modifier (STRING_AGG / ARRAY_AGG) ─────────

    /// <summary>
    /// <c>STRING_AGG(expr, sep) WITHIN GROUP (ORDER BY x)</c> ought to use
    /// the WITHIN GROUP column purely as sort order. The aggregate's
    /// arguments stay <c>(expr, sep)</c>; rows are sorted by <c>x</c>
    /// before accumulation.
    /// </summary>
    [Fact]
    public async Task StringAgg_WithinGroup_SortsOnlyAndPreservesArgs()
    {
        TableCatalog catalog = CreateCatalog("words",
            columns: ["id", "w"],
            [3, "c"],
            [1, "a"],
            [2, "b"]);

        List<Row> result = await RunAsync(catalog,
            "SELECT STRING_AGG(w, ',') WITHIN GROUP (ORDER BY id) AS joined FROM words");

        Assert.Single(result);
        Assert.Equal("a,b,c", result[0]["joined"].AsString(GetService<Pool>().RentArena()));
    }

    /// <summary>
    /// <c>ARRAY_AGG(expr) WITHIN GROUP (ORDER BY x)</c> mirrors STRING_AGG —
    /// SortModifier semantics, the array is built in WITHIN GROUP order.
    /// </summary>
    [Fact]
    public async Task ArrayAgg_WithinGroup_SortsOnly()
    {
        TableCatalog catalog = CreateCatalog("nums",
            columns: ["id", "n"],
            [3, 30],
            [1, 10],
            [2, 20]);

        List<Row> result = await RunAsync(catalog,
            "SELECT ARRAY_AGG(n) WITHIN GROUP (ORDER BY id) AS xs FROM nums");

        Assert.Single(result);
        DataValue arr = result[0]["xs"];
        Assert.True(arr.IsArray);
        Assert.Equal(DataKind.Int32, arr.Kind);
    }

    // ───────── Ordered-set (PERCENTILE_*, MODE) ─────────

    /// <summary>
    /// <c>PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY salary)</c> — ordered-set
    /// form. The planner prepends <c>salary</c> to args so the accumulator
    /// sees <c>(salary, 0.5)</c>, matching the inline two-arg API contract.
    /// </summary>
    [Fact]
    public async Task PercentileCont_WithinGroup_PrependsDataColumn()
    {
        TableCatalog catalog = CreateCatalog("emp",
            columns: ["id", "salary"],
            [1, 50.0],
            [2, 60.0],
            [3, 70.0],
            [4, 80.0],
            [5, 90.0]);

        List<Row> result = await RunAsync(catalog,
            "SELECT PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY salary) AS median FROM emp");

        Assert.Single(result);
        Assert.Equal(70.0, result[0]["median"].AsFloat64());
    }

    /// <summary>
    /// <c>MODE() WITHIN GROUP (ORDER BY x)</c> — ordered-set with no inline
    /// args. The planner prepends the WITHIN GROUP column to give MODE its
    /// single data argument.
    /// </summary>
    [Fact]
    public async Task Mode_WithinGroup_AcceptsEmptyInnerArgs()
    {
        TableCatalog catalog = CreateCatalog("hits",
            columns: ["hour"],
            [9],
            [9],
            [9],
            [10],
            [10],
            [11]);

        List<Row> result = await RunAsync(catalog,
            "SELECT MODE() WITHIN GROUP (ORDER BY hour) AS peak FROM hits");

        Assert.Single(result);
        Assert.Equal(9, result[0]["peak"].AsInt32());
    }

    // ───────── NotSupported ─────────

    /// <summary>
    /// Aggregates that don't model <c>WITHIN GROUP</c> reject the clause at
    /// plan time with a clear error mentioning the function name. SUM is
    /// the canonical regular aggregate — using WITHIN GROUP with it is
    /// non-standard SQL and shouldn't silently succeed.
    /// </summary>
    [Fact]
    public async Task Sum_WithinGroup_RaisesPlanTimeError()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["x"],
            [1],
            [2],
            [3]);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsync(catalog, "SELECT SUM(x) WITHIN GROUP (ORDER BY x) FROM t"));
        Assert.Contains("WITHIN GROUP", ex.Message);
        Assert.Contains("SUM", ex.Message);
    }
}
