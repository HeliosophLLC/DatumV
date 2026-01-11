using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for SCAN fold/prefix-scan expressions. Covers parsing, operator
/// behavior, and end-to-end SQL execution including sessionization, EMA,
/// streaks, tuple accumulators, and PREV() pseudo-function.
/// </summary>
public sealed class FoldScanTests : ServiceTestBase
{
    [Fact]
    public void Parse_ScalarScan()
    {
        SelectStatement statement = ParseStatement(
            "SELECT SCAN s = s + value INIT 0 OVER (ORDER BY id) AS running_sum FROM t");

        Assert.Single(statement.Columns);
        ScanExpression scan = Assert.IsType<ScanExpression>(statement.Columns[0].Expression);

        Assert.Single(scan.AccumulatorNames);
        Assert.Equal("s", scan.AccumulatorNames[0]);
        Assert.Single(scan.BodyExpressions);
        Assert.Single(scan.InitExpressions);
        Assert.Single(scan.OutputAliases);
        Assert.Equal("running_sum", scan.OutputAliases[0]);
        Assert.NotNull(scan.Window.OrderBy);
    }

    [Fact]
    public void Parse_TupleScan()
    {
        SelectStatement statement = ParseStatement(
            "SELECT SCAN (a, b) = (a + 1, b + value) INIT (0, 0) OVER (ORDER BY id) AS (count, total) FROM t");

        ScanExpression scan = Assert.IsType<ScanExpression>(statement.Columns[0].Expression);

        Assert.Equal(2, scan.AccumulatorNames.Count);
        Assert.Equal("a", scan.AccumulatorNames[0]);
        Assert.Equal("b", scan.AccumulatorNames[1]);
        Assert.Equal(2, scan.BodyExpressions.Count);
        Assert.Equal(2, scan.InitExpressions.Count);
        Assert.Equal(2, scan.OutputAliases.Count);
        Assert.Equal("count", scan.OutputAliases[0]);
        Assert.Equal("total", scan.OutputAliases[1]);
    }

    [Fact]
    public void Parse_ScanWithPartitionBy()
    {
        SelectStatement statement = ParseStatement(
            "SELECT SCAN s = s + 1 INIT 0 OVER (PARTITION BY grp ORDER BY id) AS rn FROM t");

        ScanExpression scan = Assert.IsType<ScanExpression>(statement.Columns[0].Expression);
        Assert.NotNull(scan.Window.PartitionBy);
        Assert.Single(scan.Window.PartitionBy);
    }

    [Fact]
    public void Parse_ScanWithPrev()
    {
        SelectStatement statement = ParseStatement(
            "SELECT SCAN s = CASE WHEN PREV(ts) IS NULL THEN 0 ELSE s + 1 END " +
            "INIT 0 OVER (ORDER BY ts) AS session FROM t");

        ScanExpression scan = Assert.IsType<ScanExpression>(statement.Columns[0].Expression);
        Assert.Single(scan.BodyExpressions);
        // PREV(ts) should be parsed as a FunctionCallExpression in the AST
        Assert.IsType<CaseExpression>(scan.BodyExpressions[0]);
    }

    [Fact]
    public void Parse_ScanAsLetRhs()
    {
        SelectStatement statement = ParseStatement(
            "SELECT LET x = SCAN s = s + val INIT 0 OVER (ORDER BY id) AS running, " +
            "x AS running_sum FROM t");

        Assert.NotNull(statement.LetBindings);
        Assert.Single(statement.LetBindings);
        Assert.IsType<ScanExpression>(statement.LetBindings[0].Expression);
    }

    [Fact]
    public void Parse_TupleScanWithCaseExpressions()
    {
        SelectStatement statement = ParseStatement(
            "SELECT PULocationID, pickup_datetime, " +
            "SCAN (episode, step) = (" +
            "  CASE WHEN date_diff('hour', PREV(pickup_datetime), pickup_datetime) > 6 " +
            "    THEN episode + 1 ELSE episode END, " +
            "  CASE WHEN date_diff('hour', PREV(pickup_datetime), pickup_datetime) > 6 " +
            "    THEN 0 ELSE step + 1 END" +
            ") " +
            "INIT (0, 0) " +
            "OVER (PARTITION BY PULocationID ORDER BY pickup_datetime) " +
            "AS (episode_id, step_index) " +
            "FROM t");

        // Two regular columns + two expanded tuple SCAN columns
        Assert.True(statement.Columns.Count >= 2);
        // Find the ScanExpression in the columns
        ScanExpression? scan = null;
        foreach (var col in statement.Columns)
        {
            if (col.Expression is ScanExpression s) { scan = s; break; }
        }
        Assert.NotNull(scan);
        Assert.Equal(2, scan.AccumulatorNames.Count);
        Assert.Equal("episode", scan.AccumulatorNames[0]);
        Assert.Equal("step", scan.AccumulatorNames[1]);
        Assert.Equal(2, scan.BodyExpressions.Count);
        Assert.IsType<CaseExpression>(scan.BodyExpressions[0]);
        Assert.IsType<CaseExpression>(scan.BodyExpressions[1]);
    }

    [Fact]
    public async Task E2E_ScalarScan_OutputColumnName()
    {
        // Regression test: SCAN output alias must be the column name when other columns
        // precede the SCAN in the SELECT list.
        TableCatalog catalog = CreateCatalog("t",
            columns: ["grp", "id", "fare"],
            [1, 1f, 100f],
            [1, 2f, 40f]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT grp, id, fare, SCAN ema = 0.15 * fare + 0.85 * ema INIT fare " +
            "OVER (PARTITION BY grp ORDER BY id) AS fare_ema FROM t",
            catalog);

        Assert.Equal(2, results.Count);
        // Column must be named "fare_ema", not "expression"
        Assert.Equal(4, results[0].FieldCount);
        Assert.Equal("fare_ema", results[0].ColumnNames[3]);
        // Values: row1 ema=100 (INIT), row2 ema=0.15*40 + 0.85*100 = 6+85 = 91
        Assert.Equal(100f, results[0]["fare_ema"].AsFloat64());
        Assert.Equal(91f, results[1]["fare_ema"].AsFloat64());
    }

    // ─────────────── End-to-end execution ───────────────

    [Fact]
    public async Task E2E_RunningSum()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["id", "value"],
            [1f, 10f],
            [2f, 20f],
            [3f, 30f]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT SCAN s = s + value INIT 0 OVER (ORDER BY id) AS running_sum FROM t",
            catalog);

        Assert.Equal(3, results.Count);
        // Body is evaluated on every row including first. s=0 initially.
        // Row 1: 0 + 10 = 10
        // Row 2: 10 + 20 = 30
        // Row 3: 30 + 30 = 60
        Assert.Equal(10f, results[0]["running_sum"].AsFloat32());
        Assert.Equal(30f, results[1]["running_sum"].AsFloat32());
        Assert.Equal(60f, results[2]["running_sum"].AsFloat32());
    }

    [Fact]
    public async Task E2E_EmptyInput()
    {
        TableCatalog catalog = CreateCatalog("t", columns: ["id"]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT SCAN s = s + 1 INIT 0 OVER (ORDER BY id) AS rn FROM t",
            catalog);

        Assert.Empty(results);
    }

    [Fact]
    public async Task E2E_SingleRow()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["id", "value"],
            [1f, 42f]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT SCAN s = s + value INIT 0 OVER (ORDER BY id) AS total FROM t",
            catalog);

        Assert.Single(results);
        // s=0 initially, body: 0 + 42 = 42
        Assert.Equal(42f, results[0]["total"].AsFloat32());
    }

    [Fact]
    public async Task E2E_MultiplePartitions()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["grp", "id", "value"],
            ["A", 1f, 10f],
            ["A", 2f, 20f],
            ["B", 1f, 100f],
            ["B", 2f, 200f]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT grp, SCAN s = s + value INIT 0 OVER (PARTITION BY grp ORDER BY id) AS total FROM t",
            catalog);

        Assert.Equal(4, results.Count);

        // Results are emitted in original order. Group by grp, accumulator resets per partition.
        Dictionary<string, List<float>> grouped = new();
        foreach (Row row in results)
        {
            string grp = row["grp"].AsString();
            if (!grouped.ContainsKey(grp)) grouped[grp] = [];
            grouped[grp].Add(row["total"].AsFloat32());
        }

        Assert.Equal([10f, 30f], grouped["A"]);
        Assert.Equal([100f, 300f], grouped["B"]);
    }

    [Fact]
    public async Task E2E_ExponentialMovingAverage()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["date", "price"],
            [1f, 100f],
            [2f, 110f],
            [3f, 105f]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT SCAN ema = 0.1 * price + 0.9 * ema INIT price OVER (ORDER BY date) AS ema_10 FROM t",
            catalog);

        Assert.Equal(3, results.Count);
        // Row 1: ema = 100 (INIT), body: 0.1*100 + 0.9*100 = 100
        Assert.Equal(100f, results[0]["ema_10"].AsFloat64(), 0.01f);
        // Row 2: ema = 100, body: 0.1*110 + 0.9*100 = 101
        Assert.Equal(101f, results[1]["ema_10"].AsFloat64(), 0.01f);
        // Row 3: ema = 101, body: 0.1*105 + 0.9*101 = 101.4
        Assert.Equal(101.4f, results[2]["ema_10"].AsFloat64(), 0.1f);
    }

    [Fact]
    public async Task E2E_Sessionization_WithPrev()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["user_id", "ts"],
            ["u1", 100f],
            ["u1", 110f],
            ["u1", 200f]);

        // Session increments when gap > 50
        List<Row> results = await ExecuteQueryAsync(
            "SELECT SCAN s = CASE WHEN PREV(ts) IS NULL THEN s " +
            "WHEN ts - PREV(ts) > 50 THEN s + 1 ELSE s END " +
            "INIT 0 OVER (PARTITION BY user_id ORDER BY ts) AS session_id FROM t",
            catalog);

        Assert.Equal(3, results.Count);
        // Row 1: PREV(ts)=NULL → s=0, body: CASE NULL IS NULL → s=0 → output 0
        Assert.Equal(0d, results[0]["session_id"].ToDouble());
        // Row 2: PREV(ts)=100, gap=10 ≤ 50 → output 0
        Assert.Equal(0d, results[1]["session_id"].ToDouble());
        // Row 3: PREV(ts)=110, gap=90 > 50 → output 1
        Assert.Equal(1d, results[2]["session_id"].ToDouble());
    }

    [Fact]
    public async Task E2E_StreakDetection()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["id", "won"],
            [1f, 1f],
            [2f, 1f],
            [3f, 0f],
            [4f, 1f]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT SCAN streak = CASE WHEN won = 1 THEN streak + 1 ELSE 0 END " +
            "INIT 0 OVER (ORDER BY id) AS current_streak FROM t",
            catalog);

        Assert.Equal(4, results.Count);
        Assert.Equal(1d, results[0]["current_streak"].ToDouble()); // 0+1
        Assert.Equal(2d, results[1]["current_streak"].ToDouble()); // 1+1
        Assert.Equal(0d, results[2]["current_streak"].ToDouble()); // reset
        Assert.Equal(1d, results[3]["current_streak"].ToDouble()); // 0+1
    }

    [Fact]
    public async Task E2E_ScanWithLetBinding()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["id", "value"],
            [1f, 10f],
            [2f, 20f]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT LET total = SCAN s = s + value INIT 0 OVER (ORDER BY id) AS _total, " +
            "total AS running_sum FROM t",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal(10f, results[0]["running_sum"].AsFloat32());
        Assert.Equal(30f, results[1]["running_sum"].AsFloat32());
    }

    [Fact]
    public async Task E2E_TupleScan_EpisodeAndStep()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["id", "gap"],
            [1f, 0f],
            [2f, 10f],
            [3f, 100f],
            [4f, 5f]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT SCAN (episode, step) = " +
            "(CASE WHEN gap > 60 THEN episode + 1 ELSE episode END, " +
            " CASE WHEN gap > 60 THEN 0 ELSE step + 1 END) " +
            "INIT (0, 0) OVER (ORDER BY id) " +
            "AS (episode_id, step_index) FROM t",
            catalog);

        Assert.Equal(4, results.Count);
        // Row 1: episode=0, step=0 → body: gap=0 ≤ 60 → (0, 0+1) = (0, 1)
        Assert.Equal(0d, results[0]["episode_id"].ToDouble());
        Assert.Equal(1d, results[0]["step_index"].ToDouble());
        // Row 2: episode=0, step=1 → gap=10 ≤ 60 → (0, 2)
        Assert.Equal(0d, results[1]["episode_id"].ToDouble());
        Assert.Equal(2d, results[1]["step_index"].ToDouble());
        // Row 3: episode=0, step=2 → gap=100 > 60 → (1, 0)
        Assert.Equal(1d, results[2]["episode_id"].ToDouble());
        Assert.Equal(0d, results[2]["step_index"].ToDouble());
        // Row 4: episode=1, step=0 → gap=5 ≤ 60 → (1, 1)
        Assert.Equal(1d, results[3]["episode_id"].ToDouble());
        Assert.Equal(1d, results[3]["step_index"].ToDouble());
    }

    [Fact]
    public async Task E2E_ScanPreservesOriginalColumns()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["id", "value"],
            [1f, 10f],
            [2f, 20f]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT id, value, SCAN s = s + value INIT 0 OVER (ORDER BY id) AS total FROM t",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal(1f, results[0]["id"].AsFloat32());
        Assert.Equal(10f, results[0]["value"].AsFloat32());
        Assert.Equal(10f, results[0]["total"].AsFloat32());
    }

    [Fact]
    public async Task E2E_ScanAndWindowSameOver()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["id", "value"],
            [1f, 10f],
            [2f, 20f],
            [3f, 30f]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT ROW_NUMBER() OVER (ORDER BY id) AS rn, " +
            "SCAN s = s + value INIT 0 OVER (ORDER BY id) AS total FROM t",
            catalog);

        Assert.Equal(3, results.Count);
        Assert.Equal(1f, results[0]["rn"].AsFloat32());
        Assert.Equal(10f, results[0]["total"].AsFloat32());
        Assert.Equal(2f, results[1]["rn"].AsFloat32());
        Assert.Equal(30f, results[1]["total"].AsFloat32());
    }

    // ─────────────── Memory budget enforcement (Tier 2) ───────────────

    /// <summary>
    /// FOLD/SCAN currently materialises the entire input in memory (Tier 1+2 of
    /// the migration; spill-to-disk is Tier 3). Under a tight budget the operator
    /// must throw cleanly with a user-facing message rather than silently OOMing
    /// or quietly exceeding the budget. Asserts the throw fires at the predictable
    /// point and identifies the cause.
    /// </summary>
    [Fact]
    public async Task FoldScan_TightBudget_ThrowsBeforeOOM()
    {
        // Enough rows that a 64-byte budget is unambiguously exceeded.
        object?[][] rows = Enumerable.Range(0, 1000)
            .Select(index => new object?[] { (float)index, (float)(index * 10) })
            .ToArray();

        TableCatalog catalog = CreateCatalog("t", columns: ["id", "value"], rows);

        ExecutionContext context = CreateExecutionContext(catalog: catalog, memoryBudgetBytes: 64);

        QueryExpression query = SqlParser.Parse(
            "SELECT SCAN s = s + value INIT 0 OVER (ORDER BY id) AS running_sum FROM t");
        QueryPlanner planner = new(catalog, FunctionRegistry.CreateDefault());
        IQueryOperator plan = await planner.PlanWithSubqueriesAsync(query, context, CancellationToken.None);

        ExecutionException ex = await Assert.ThrowsAnyAsync<ExecutionException>(
            () => plan.CollectRowsAsync(context));

        Assert.Contains("FOLD/SCAN", ex.Message);
        Assert.Contains("memory budget", ex.Message);
    }

    /// <summary>
    /// Under a generous budget the same query must complete normally — proves the
    /// budget check doesn't fire spuriously on small datasets.
    /// </summary>
    [Fact]
    public async Task FoldScan_GenerousBudget_CompletesNormally()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["id", "value"],
            [1f, 10f],
            [2f, 20f],
            [3f, 30f]);

        ExecutionContext context = CreateExecutionContext(catalog: catalog, memoryBudgetBytes: 10 * 1024 * 1024);

        QueryExpression query = SqlParser.Parse(
            "SELECT SCAN s = s + value INIT 0 OVER (ORDER BY id) AS running_sum FROM t");
        QueryPlanner planner = new(catalog, FunctionRegistry.CreateDefault());
        IQueryOperator plan = await planner.PlanWithSubqueriesAsync(query, context, CancellationToken.None);

        List<Row> results = await plan.CollectRowsAsync(context);

        Assert.Equal(3, results.Count);
        Assert.Equal(10f, results[0]["running_sum"].AsFloat32());
        Assert.Equal(30f, results[1]["running_sum"].AsFloat32());
        Assert.Equal(60f, results[2]["running_sum"].AsFloat32());
    }

    // ─────────────── Helpers ───────────────

    private static SelectStatement ParseStatement(string sql)
    {
        return ((SelectQueryExpression)SqlParser.Parse(sql)).Statement;
    }

}
