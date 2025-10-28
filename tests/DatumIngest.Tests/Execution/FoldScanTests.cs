using System.Runtime.CompilerServices;
using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for SCAN fold/prefix-scan expressions. Covers parsing, operator
/// behavior, and end-to-end SQL execution including sessionization, EMA,
/// streaks, tuple accumulators, and PREV() pseudo-function.
/// </summary>
public sealed class FoldScanTests : ServiceTestBase
{
    private static readonly FunctionRegistry DefaultFunctions = FunctionRegistry.CreateDefault();

    // ─────────────── Parsing ───────────────

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
        Row[] data =
        [
            MakeRow(("grp", DataValue.FromInt32(1)), ("id", DataValue.FromFloat32(1f)), ("fare", DataValue.FromFloat32(100f))),
            MakeRow(("grp", DataValue.FromInt32(1)), ("id", DataValue.FromFloat32(2f)), ("fare", DataValue.FromFloat32(40f))),
        ];
        TableCatalog catalog = CreateCatalog(("t", data));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT grp, id, fare, SCAN ema = 0.15 * fare + 0.85 * ema INIT fare " +
            "OVER (PARTITION BY grp ORDER BY id) AS fare_ema FROM t",
            catalog);

        Assert.Equal(2, results.Count);
        // Column must be named "fare_ema", not "expression"
        Assert.Equal(4, results[0].FieldCount);
        Assert.Equal("fare_ema", results[0].ColumnNames[3]);
        // Values: row1 ema=100 (INIT), row2 ema=0.15*40 + 0.85*100 = 6+85 = 91
        Assert.Equal(100f, results[0]["fare_ema"].AsFloat32());
        Assert.Equal(91f, results[1]["fare_ema"].AsFloat32());
    }

    // ─────────────── End-to-end execution ───────────────

    [Fact]
    public async Task E2E_RunningSum()
    {
        Row[] data =
        [
            MakeRow(("id", DataValue.FromFloat32(1)), ("value", DataValue.FromFloat32(10))),
            MakeRow(("id", DataValue.FromFloat32(2)), ("value", DataValue.FromFloat32(20))),
            MakeRow(("id", DataValue.FromFloat32(3)), ("value", DataValue.FromFloat32(30))),
        ];

        TableCatalog catalog = CreateCatalog(("t", data));

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
        TableCatalog catalog = CreateCatalog(("t", []));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT SCAN s = s + 1 INIT 0 OVER (ORDER BY id) AS rn FROM t",
            catalog);

        Assert.Empty(results);
    }

    [Fact]
    public async Task E2E_SingleRow()
    {
        Row[] data =
        [
            MakeRow(("id", DataValue.FromFloat32(1)), ("value", DataValue.FromFloat32(42))),
        ];

        TableCatalog catalog = CreateCatalog(("t", data));

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
        Row[] data =
        [
            MakeRow(("grp", DataValue.FromString("A")), ("id", DataValue.FromFloat32(1)), ("value", DataValue.FromFloat32(10))),
            MakeRow(("grp", DataValue.FromString("A")), ("id", DataValue.FromFloat32(2)), ("value", DataValue.FromFloat32(20))),
            MakeRow(("grp", DataValue.FromString("B")), ("id", DataValue.FromFloat32(1)), ("value", DataValue.FromFloat32(100))),
            MakeRow(("grp", DataValue.FromString("B")), ("id", DataValue.FromFloat32(2)), ("value", DataValue.FromFloat32(200))),
        ];

        TableCatalog catalog = CreateCatalog(("t", data));

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
        Row[] data =
        [
            MakeRow(("date", DataValue.FromFloat32(1)), ("price", DataValue.FromFloat32(100))),
            MakeRow(("date", DataValue.FromFloat32(2)), ("price", DataValue.FromFloat32(110))),
            MakeRow(("date", DataValue.FromFloat32(3)), ("price", DataValue.FromFloat32(105))),
        ];

        TableCatalog catalog = CreateCatalog(("t", data));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT SCAN ema = 0.1 * price + 0.9 * ema INIT price OVER (ORDER BY date) AS ema_10 FROM t",
            catalog);

        Assert.Equal(3, results.Count);
        // Row 1: ema = 100 (INIT), body: 0.1*100 + 0.9*100 = 100
        Assert.Equal(100f, results[0]["ema_10"].AsFloat32(), 0.01f);
        // Row 2: ema = 100, body: 0.1*110 + 0.9*100 = 101
        Assert.Equal(101f, results[1]["ema_10"].AsFloat32(), 0.01f);
        // Row 3: ema = 101, body: 0.1*105 + 0.9*101 = 101.4
        Assert.Equal(101.4f, results[2]["ema_10"].AsFloat32(), 0.1f);
    }

    [Fact]
    public async Task E2E_Sessionization_WithPrev()
    {
        Row[] data =
        [
            MakeRow(("user_id", DataValue.FromString("u1")), ("ts", DataValue.FromFloat32(100))),
            MakeRow(("user_id", DataValue.FromString("u1")), ("ts", DataValue.FromFloat32(110))),
            MakeRow(("user_id", DataValue.FromString("u1")), ("ts", DataValue.FromFloat32(200))),
        ];

        TableCatalog catalog = CreateCatalog(("t", data));

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
        Row[] data =
        [
            MakeRow(("id", DataValue.FromFloat32(1)), ("won", DataValue.FromFloat32(1))),
            MakeRow(("id", DataValue.FromFloat32(2)), ("won", DataValue.FromFloat32(1))),
            MakeRow(("id", DataValue.FromFloat32(3)), ("won", DataValue.FromFloat32(0))),
            MakeRow(("id", DataValue.FromFloat32(4)), ("won", DataValue.FromFloat32(1))),
        ];

        TableCatalog catalog = CreateCatalog(("t", data));

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
        Row[] data =
        [
            MakeRow(("id", DataValue.FromFloat32(1)), ("value", DataValue.FromFloat32(10))),
            MakeRow(("id", DataValue.FromFloat32(2)), ("value", DataValue.FromFloat32(20))),
        ];

        TableCatalog catalog = CreateCatalog(("t", data));

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
        Row[] data =
        [
            MakeRow(("id", DataValue.FromFloat32(1)), ("gap", DataValue.FromFloat32(0))),
            MakeRow(("id", DataValue.FromFloat32(2)), ("gap", DataValue.FromFloat32(10))),
            MakeRow(("id", DataValue.FromFloat32(3)), ("gap", DataValue.FromFloat32(100))),
            MakeRow(("id", DataValue.FromFloat32(4)), ("gap", DataValue.FromFloat32(5))),
        ];

        TableCatalog catalog = CreateCatalog(("t", data));

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
        Row[] data =
        [
            MakeRow(("id", DataValue.FromFloat32(1)), ("value", DataValue.FromFloat32(10))),
            MakeRow(("id", DataValue.FromFloat32(2)), ("value", DataValue.FromFloat32(20))),
        ];

        TableCatalog catalog = CreateCatalog(("t", data));

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
        Row[] data =
        [
            MakeRow(("id", DataValue.FromFloat32(1)), ("value", DataValue.FromFloat32(10))),
            MakeRow(("id", DataValue.FromFloat32(2)), ("value", DataValue.FromFloat32(20))),
            MakeRow(("id", DataValue.FromFloat32(3)), ("value", DataValue.FromFloat32(30))),
        ];

        TableCatalog catalog = CreateCatalog(("t", data));

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

    // ─────────────── Helpers ───────────────

    private static SelectStatement ParseStatement(string sql)
    {
        return ((SelectQueryExpression)SqlParser.Parse(sql)).Statement;
    }

    private static Row MakeRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = columns.Select(c => c.Name).ToArray();
        DataValue[] values = columns.Select(c => c.Value).ToArray();
        return new Row(names, values);
    }

    private static async Task<List<Row>> ExecuteQueryAsync(string sql, TableCatalog catalog)
    {
        QueryExpression query = SqlParser.Parse(sql);
        QueryPlanner planner = new(catalog, DefaultFunctions);
        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog, new LocalBufferPool());
        IQueryOperator plan = planner.Plan(query);

        return await plan.CollectRowsAsync(context);
    }
}
