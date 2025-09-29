using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Functions.Aggregates;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for <see cref="GroupByOperator"/> in streaming mode
/// (<c>streamingSorted: true</c>), which emits groups one at a time from
/// pre-sorted input, enabling LIMIT short-circuit.
/// </summary>
public sealed class StreamingGroupByTests
{
    private static ExecutionContext CreateContext()
    {
        return new ExecutionContext(
            CancellationToken.None,
            FunctionRegistry.CreateDefault(),
            new TableCatalog(),
            new LocalBufferPool());
    }

    private static Row MakeRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = columns.Select(c => c.Name).ToArray();
        DataValue[] values = columns.Select(c => c.Value).ToArray();
        return new Row(names, values);
    }

    private static async Task<List<Row>> CollectAsync(IQueryOperator op, ExecutionContext? context = null)
    {
        context ??= CreateContext();
        return await op.CollectRowsAsync(context);
    }

    // ─────────────── Single-key streaming ───────────────

    /// <summary>
    /// Streaming GROUP BY with a single key produces the same results as hash
    /// aggregation when input is sorted.
    /// </summary>
    [Fact]
    public async Task SingleKey_Streaming_ProducesCorrectGroups()
    {
        // Input sorted by "category".
        MockOperator source = new(
            MakeRow(("category", DataValue.FromString("A")), ("value", DataValue.FromFloat32(1f))),
            MakeRow(("category", DataValue.FromString("A")), ("value", DataValue.FromFloat32(3f))),
            MakeRow(("category", DataValue.FromString("A")), ("value", DataValue.FromFloat32(5f))),
            MakeRow(("category", DataValue.FromString("B")), ("value", DataValue.FromFloat32(2f))),
            MakeRow(("category", DataValue.FromString("B")), ("value", DataValue.FromFloat32(4f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [new ColumnReference("category")],
            aggregateColumns:
            [
                new AggregateColumn(new CountFunction(), [], "COUNT(*)", IsCountStar: true),
                new AggregateColumn(
                    new SumFunction(),
                    [new ColumnReference("value")],
                    "SUM(value)"),
            ],
            streamingSorted: true);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Equal(2, results.Count);

        // Streaming preserves input order.
        Assert.Equal("A", results[0]["category"].AsString());
        Assert.Equal(3f, results[0]["COUNT(*)"].AsFloat32());
        Assert.Equal(9f, results[0]["SUM(value)"].AsFloat32());

        Assert.Equal("B", results[1]["category"].AsString());
        Assert.Equal(2f, results[1]["COUNT(*)"].AsFloat32());
        Assert.Equal(6f, results[1]["SUM(value)"].AsFloat32());
    }

    /// <summary>
    /// Streaming GROUP BY matches hash GROUP BY results for the same sorted input.
    /// </summary>
    [Fact]
    public async Task SingleKey_StreamingMatchesHashResult()
    {
        Row[] rows =
        [
            MakeRow(("key", DataValue.FromFloat32(1f)), ("val", DataValue.FromFloat32(10f))),
            MakeRow(("key", DataValue.FromFloat32(1f)), ("val", DataValue.FromFloat32(20f))),
            MakeRow(("key", DataValue.FromFloat32(2f)), ("val", DataValue.FromFloat32(30f))),
            MakeRow(("key", DataValue.FromFloat32(3f)), ("val", DataValue.FromFloat32(40f))),
            MakeRow(("key", DataValue.FromFloat32(3f)), ("val", DataValue.FromFloat32(50f))),
            MakeRow(("key", DataValue.FromFloat32(3f)), ("val", DataValue.FromFloat32(60f))),
        ];

        IReadOnlyList<Expression> groupByKeys = [new ColumnReference("key")];
        AggregateColumn[] aggregates =
        [
            new AggregateColumn(new CountFunction(), [], "COUNT(*)", IsCountStar: true),
            new AggregateColumn(new SumFunction(), [new ColumnReference("val")], "SUM(val)"),
        ];

        GroupByOperator hashGroupBy = new(
            new MockOperator(rows.Select(r => r.Clone()).ToArray()),
            groupByKeys,
            aggregates,
            streamingSorted: false);

        GroupByOperator streamingGroupBy = new(
            new MockOperator(rows.Select(r => r.Clone()).ToArray()),
            groupByKeys,
            aggregates,
            streamingSorted: true);

        List<Row> hashResults = await CollectAsync(hashGroupBy);
        List<Row> streamingResults = await CollectAsync(streamingGroupBy);

        Assert.Equal(hashResults.Count, streamingResults.Count);

        // Compare by key value (hash order may differ).
        foreach (Row streamingRow in streamingResults)
        {
            DataValue key = streamingRow["key"];
            Row hashRow = hashResults.First(row => row["key"].Equals(key));

            Assert.Equal(hashRow["COUNT(*)"].AsFloat32(), streamingRow["COUNT(*)"].AsFloat32());
            Assert.Equal(hashRow["SUM(val)"].AsFloat32(), streamingRow["SUM(val)"].AsFloat32());
        }
    }

    // ─────────────── Composite-key streaming ───────────────

    /// <summary>
    /// Streaming GROUP BY with composite keys emits groups when any key changes.
    /// </summary>
    [Fact]
    public async Task CompositeKey_Streaming_ProducesCorrectGroups()
    {
        // Input sorted by (dept, status).
        MockOperator source = new(
            MakeRow(("dept", DataValue.FromString("X")), ("status", DataValue.FromString("active")), ("amount", DataValue.FromFloat32(100f))),
            MakeRow(("dept", DataValue.FromString("X")), ("status", DataValue.FromString("active")), ("amount", DataValue.FromFloat32(300f))),
            MakeRow(("dept", DataValue.FromString("X")), ("status", DataValue.FromString("inactive")), ("amount", DataValue.FromFloat32(200f))),
            MakeRow(("dept", DataValue.FromString("Y")), ("status", DataValue.FromString("active")), ("amount", DataValue.FromFloat32(50f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [new ColumnReference("dept"), new ColumnReference("status")],
            aggregateColumns:
            [
                new AggregateColumn(new CountFunction(), [], "COUNT(*)", IsCountStar: true),
                new AggregateColumn(new SumFunction(), [new ColumnReference("amount")], "SUM(amount)"),
            ],
            streamingSorted: true);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Equal(3, results.Count);

        // Groups appear in sorted order.
        Assert.Equal("X", results[0]["dept"].AsString());
        Assert.Equal("active", results[0]["status"].AsString());
        Assert.Equal(2f, results[0]["COUNT(*)"].AsFloat32());
        Assert.Equal(400f, results[0]["SUM(amount)"].AsFloat32());

        Assert.Equal("X", results[1]["dept"].AsString());
        Assert.Equal("inactive", results[1]["status"].AsString());
        Assert.Equal(1f, results[1]["COUNT(*)"].AsFloat32());
        Assert.Equal(200f, results[1]["SUM(amount)"].AsFloat32());

        Assert.Equal("Y", results[2]["dept"].AsString());
        Assert.Equal("active", results[2]["status"].AsString());
        Assert.Equal(1f, results[2]["COUNT(*)"].AsFloat32());
        Assert.Equal(50f, results[2]["SUM(amount)"].AsFloat32());
    }

    // ─────────────── LIMIT short-circuit ───────────────

    /// <summary>
    /// Streaming GROUP BY with LIMIT reads only enough rows to fill the requested
    /// number of groups, verifying early termination.
    /// </summary>
    [Fact]
    public async Task Streaming_WithLimit_StopsEarly()
    {
        int rowsRead = 0;

        // 100 groups × 10 rows each = 1000 rows total. All sorted by "group_id".
        Row[] allRows = new Row[1000];
        for (int group = 0; group < 100; group++)
        {
            for (int row = 0; row < 10; row++)
            {
                allRows[group * 10 + row] = MakeRow(
                    ("group_id", DataValue.FromFloat32(group)),
                    ("value", DataValue.FromFloat32(row)));
            }
        }

        CountingOperator source = new(allRows, () => rowsRead++);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [new ColumnReference("group_id")],
            aggregateColumns:
            [
                new AggregateColumn(new CountFunction(), [], "COUNT(*)", IsCountStar: true),
            ],
            streamingSorted: true);

        LimitOperator limit = new(groupBy, 5, 0);

        // Use a small output BatchSize so the streaming GROUP BY yields an output
        // batch after 8 groups, allowing LimitOperator to terminate before all
        // source rows are consumed. With default BatchSize (1024) all 100 groups
        // fit in one batch and the entire input would be read before any output
        // is yielded.
        ExecutionContext context = new(
            CancellationToken.None,
            FunctionRegistry.CreateDefault(),
            new TableCatalog(),
            new LocalBufferPool())
        {
            BatchSize = 8,
        };

        List<Row> results = await CollectAsync(limit, context);

        Assert.Equal(5, results.Count);

        // Verify groups 0-4 are returned.
        for (int index = 0; index < 5; index++)
        {
            Assert.Equal((float)index, results[index]["group_id"].AsFloat32());
            Assert.Equal(10f, results[index]["COUNT(*)"].AsFloat32());
        }

        // The source should read enough rows to fill the first output batch of 8
        // groups (80 rows), rounded up to CountingOperator's source batch size of
        // 64. That means at most 2-3 source batches (128-192 rows), far less than
        // the full 1000.
        Assert.True(rowsRead <= 200,
            $"Expected at most ~200 rows read with LIMIT 5 (batch-aware), but read {rowsRead}");
    }

    // ─────────────── Edge cases ───────────────

    /// <summary>
    /// Streaming GROUP BY with a single row produces one group.
    /// </summary>
    [Fact]
    public async Task Streaming_SingleRow_ProducesOneGroup()
    {
        MockOperator source = new(
            MakeRow(("key", DataValue.FromString("only")), ("val", DataValue.FromFloat32(42f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [new ColumnReference("key")],
            aggregateColumns:
            [
                new AggregateColumn(new SumFunction(), [new ColumnReference("val")], "SUM(val)"),
            ],
            streamingSorted: true);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal("only", results[0]["key"].AsString());
        Assert.Equal(42f, results[0]["SUM(val)"].AsFloat32());
    }

    /// <summary>
    /// Streaming GROUP BY with empty input produces no rows.
    /// </summary>
    [Fact]
    public async Task Streaming_EmptyInput_ProducesNoRows()
    {
        MockOperator source = new();

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [new ColumnReference("key")],
            aggregateColumns:
            [
                new AggregateColumn(new CountFunction(), [], "COUNT(*)", IsCountStar: true),
            ],
            streamingSorted: true);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Empty(results);
    }

    /// <summary>
    /// Streaming GROUP BY where every row has a different key produces one group per row.
    /// </summary>
    [Fact]
    public async Task Streaming_AllUniqueKeys_ProducesOneGroupPerRow()
    {
        MockOperator source = new(
            MakeRow(("id", DataValue.FromFloat32(1f))),
            MakeRow(("id", DataValue.FromFloat32(2f))),
            MakeRow(("id", DataValue.FromFloat32(3f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [new ColumnReference("id")],
            aggregateColumns:
            [
                new AggregateColumn(new CountFunction(), [], "COUNT(*)", IsCountStar: true),
            ],
            streamingSorted: true);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Equal(3, results.Count);
        Assert.All(results, row => Assert.Equal(1f, row["COUNT(*)"].AsFloat32()));
    }

    /// <summary>
    /// Streaming GROUP BY handles NULL key values (all NULLs form one group).
    /// </summary>
    [Fact]
    public async Task Streaming_NullKeys_GroupedTogether()
    {
        MockOperator source = new(
            MakeRow(("key", DataValue.Null(DataKind.String)), ("val", DataValue.FromFloat32(1f))),
            MakeRow(("key", DataValue.Null(DataKind.String)), ("val", DataValue.FromFloat32(2f))),
            MakeRow(("key", DataValue.FromString("A")), ("val", DataValue.FromFloat32(3f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [new ColumnReference("key")],
            aggregateColumns:
            [
                new AggregateColumn(new CountFunction(), [], "COUNT(*)", IsCountStar: true),
            ],
            streamingSorted: true);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Equal(2, results.Count);
        Assert.True(results[0]["key"].IsNull);
        Assert.Equal(2f, results[0]["COUNT(*)"].AsFloat32());
        Assert.Equal("A", results[1]["key"].AsString());
        Assert.Equal(1f, results[1]["COUNT(*)"].AsFloat32());
    }

    // ─────────────── MIN/MAX in streaming mode ───────────────

    /// <summary>
    /// Streaming GROUP BY computes MIN and MAX correctly.
    /// </summary>
    [Fact]
    public async Task Streaming_MinMax_Aggregates()
    {
        MockOperator source = new(
            MakeRow(("group", DataValue.FromString("G1")), ("val", DataValue.FromFloat32(5f))),
            MakeRow(("group", DataValue.FromString("G1")), ("val", DataValue.FromFloat32(15f))),
            MakeRow(("group", DataValue.FromString("G1")), ("val", DataValue.FromFloat32(10f))),
            MakeRow(("group", DataValue.FromString("G2")), ("val", DataValue.FromFloat32(100f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [new ColumnReference("group")],
            aggregateColumns:
            [
                new AggregateColumn(new MinFunction(), [new ColumnReference("val")], "MIN(val)"),
                new AggregateColumn(new MaxFunction(), [new ColumnReference("val")], "MAX(val)"),
            ],
            streamingSorted: true);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Equal(2, results.Count);
        Assert.Equal(5f, results[0]["MIN(val)"].AsFloat32());
        Assert.Equal(15f, results[0]["MAX(val)"].AsFloat32());
        Assert.Equal(100f, results[1]["MIN(val)"].AsFloat32());
        Assert.Equal(100f, results[1]["MAX(val)"].AsFloat32());
    }

    // ─────────────── DescribeForExplain ───────────────

    /// <summary>
    /// Streaming GROUP BY uses distinct name and annotations in the explain plan.
    /// </summary>
    [Fact]
    public void DescribeForExplain_StreamingMode_ShowsStreamingName()
    {
        MockOperator source = new();

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [new ColumnReference("key")],
            aggregateColumns:
            [
                new AggregateColumn(new CountFunction(), [], "COUNT(*)", IsCountStar: true),
            ],
            streamingSorted: true);

        OperatorPlanDescription description = groupBy.DescribeForExplain();

        Assert.Equal("Streaming Group By", description.OperatorName);
        Assert.Empty(description.Warnings);
        Assert.Contains(description.Annotations, annotation => annotation.Contains("streaming"));
    }

    /// <summary>
    /// Hash GROUP BY retains the warning about materializing all rows.
    /// </summary>
    [Fact]
    public void DescribeForExplain_HashMode_ShowsWarning()
    {
        MockOperator source = new();

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [new ColumnReference("key")],
            aggregateColumns:
            [
                new AggregateColumn(new CountFunction(), [], "COUNT(*)", IsCountStar: true),
            ],
            streamingSorted: false);

        OperatorPlanDescription description = groupBy.DescribeForExplain();

        Assert.Equal("Group By", description.OperatorName);
        Assert.Contains(description.Warnings, warning => warning.Contains("materializes"));
        Assert.Empty(description.Annotations);
    }
}
