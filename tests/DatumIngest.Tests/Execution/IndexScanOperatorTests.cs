using System.Runtime.CompilerServices;
using System.Text;
using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Indexing;
using DatumIngest.Indexing.BTree;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for <see cref="IndexScanOperator"/> sorted iteration and
/// <see cref="QueryPlanner"/> substitution of ORDER BY with index scan.
/// </summary>
public sealed class IndexScanOperatorTests
{
    private static readonly FunctionRegistry DefaultFunctions = FunctionRegistry.CreateDefault();

    private static readonly byte[] DummyHash = new byte[32];

    // ───────────────────── IndexScanOperator unit tests ─────────────────────

    [Fact]
    public async Task IndexScan_Ascending_YieldsRowsInSortedOrder()
    {
        // 5 rows with values [30, 10, 40, 20, 50] — sorted ascending = [10, 20, 30, 40, 50].
        Row[] rows = CreateNumberedRows(30f, 10f, 40f, 20f, 50f);
        SeekableInMemoryProvider provider = new(rows);

        // Build sorted index on "value" column.
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromFloat32(10f), ChunkIndex: 0, RowOffsetInChunk: 1),
            new(DataValue.FromFloat32(20f), ChunkIndex: 0, RowOffsetInChunk: 3),
            new(DataValue.FromFloat32(30f), ChunkIndex: 0, RowOffsetInChunk: 0),
            new(DataValue.FromFloat32(40f), ChunkIndex: 0, RowOffsetInChunk: 2),
            new(DataValue.FromFloat32(50f), ChunkIndex: 0, RowOffsetInChunk: 4),
        ];

        SortedValueIndex sortedIndex = new(entries);
        IReadOnlyList<IndexChunk> chunks = [CreateChunk(rowOffset: 0, rowCount: 5)];

        TableDescriptor descriptor = CreateDescriptor("test");
        IndexScanOperator indexScan = new(descriptor, null, sortedIndex, chunks, descending: false);

        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        catalog.Register(descriptor);

        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog, new LocalBufferPool());

        List<Row> results = await CollectRowsAsync(indexScan.ExecuteAsync(context));

        Assert.Equal(5, results.Count);
        Assert.Equal(10f, results[0]["value"].AsFloat32());
        Assert.Equal(20f, results[1]["value"].AsFloat32());
        Assert.Equal(30f, results[2]["value"].AsFloat32());
        Assert.Equal(40f, results[3]["value"].AsFloat32());
        Assert.Equal(50f, results[4]["value"].AsFloat32());
    }

    [Fact]
    public async Task IndexScan_Descending_YieldsRowsInReverseSortedOrder()
    {
        Row[] rows = CreateNumberedRows(30f, 10f, 40f, 20f, 50f);
        SeekableInMemoryProvider provider = new(rows);

        ValueIndexEntry[] entries =
        [
            new(DataValue.FromFloat32(10f), ChunkIndex: 0, RowOffsetInChunk: 1),
            new(DataValue.FromFloat32(20f), ChunkIndex: 0, RowOffsetInChunk: 3),
            new(DataValue.FromFloat32(30f), ChunkIndex: 0, RowOffsetInChunk: 0),
            new(DataValue.FromFloat32(40f), ChunkIndex: 0, RowOffsetInChunk: 2),
            new(DataValue.FromFloat32(50f), ChunkIndex: 0, RowOffsetInChunk: 4),
        ];

        SortedValueIndex sortedIndex = new(entries);
        IReadOnlyList<IndexChunk> chunks = [CreateChunk(rowOffset: 0, rowCount: 5)];

        TableDescriptor descriptor = CreateDescriptor("test");
        IndexScanOperator indexScan = new(descriptor, null, sortedIndex, chunks, descending: true);

        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        catalog.Register(descriptor);

        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog, new LocalBufferPool());

        List<Row> results = await CollectRowsAsync(indexScan.ExecuteAsync(context));

        Assert.Equal(5, results.Count);
        Assert.Equal(50f, results[0]["value"].AsFloat32());
        Assert.Equal(40f, results[1]["value"].AsFloat32());
        Assert.Equal(30f, results[2]["value"].AsFloat32());
        Assert.Equal(20f, results[3]["value"].AsFloat32());
        Assert.Equal(10f, results[4]["value"].AsFloat32());
    }

    [Fact]
    public async Task IndexScan_MultipleChunks_BatchesSameChunkEntries()
    {
        // 6 rows across 2 chunks of 3 rows each.
        Row[] rows = CreateNumberedRows(50f, 30f, 10f, 60f, 40f, 20f);
        SeekableInMemoryProvider provider = new(rows);

        // Sorted index: 10(chunk0,row2), 20(chunk1,row2), 30(chunk0,row1), 40(chunk1,row1), 50(chunk0,row0), 60(chunk1,row0)
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromFloat32(10f), ChunkIndex: 0, RowOffsetInChunk: 2),
            new(DataValue.FromFloat32(20f), ChunkIndex: 1, RowOffsetInChunk: 2),
            new(DataValue.FromFloat32(30f), ChunkIndex: 0, RowOffsetInChunk: 1),
            new(DataValue.FromFloat32(40f), ChunkIndex: 1, RowOffsetInChunk: 1),
            new(DataValue.FromFloat32(50f), ChunkIndex: 0, RowOffsetInChunk: 0),
            new(DataValue.FromFloat32(60f), ChunkIndex: 1, RowOffsetInChunk: 0),
        ];

        SortedValueIndex sortedIndex = new(entries);
        IReadOnlyList<IndexChunk> chunks =
        [
            CreateChunk(rowOffset: 0, rowCount: 3),
            CreateChunk(rowOffset: 3, rowCount: 3),
        ];

        TableDescriptor descriptor = CreateDescriptor("test");
        IndexScanOperator indexScan = new(descriptor, null, sortedIndex, chunks, descending: false);

        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        catalog.Register(descriptor);

        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog, new LocalBufferPool());

        List<Row> results = await CollectRowsAsync(indexScan.ExecuteAsync(context));

        Assert.Equal(6, results.Count);
        Assert.Equal(10f, results[0]["value"].AsFloat32());
        Assert.Equal(20f, results[1]["value"].AsFloat32());
        Assert.Equal(30f, results[2]["value"].AsFloat32());
        Assert.Equal(40f, results[3]["value"].AsFloat32());
        Assert.Equal(50f, results[4]["value"].AsFloat32());
        Assert.Equal(60f, results[5]["value"].AsFloat32());
    }

    // ───────────────────── Planner integration tests ─────────────────────

    [Fact]
    public void Plan_OrderByWithSortedIndex_SubstitutesIndexScan()
    {
        Row[] rows = CreateNumberedRows(3f, 1f, 2f);
        SeekableInMemoryProvider provider = new(rows);

        TableDescriptor descriptor = CreateDescriptor("data");
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        catalog.Register(descriptor);

        // Register a sorted index on "value".
        SourceIndex sourceIndex = CreateSourceIndexWithSort("value", rows);
        catalog.RegisterIndex("data", sourceIndex);

        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("data")),
            OrderBy: new OrderByClause(
            [
                new OrderByItem(new ColumnReference("value"), SortDirection.Ascending)
            ]));

        IQueryOperator plan = planner.Plan(statement);

        // The plan should be an IndexScanOperator, not OrderByOperator over ScanOperator.
        Assert.IsType<IndexScanOperator>(plan);
    }

    [Fact]
    public void Plan_OrderByWithoutSortedIndex_FallsBackToOrderByOperator()
    {
        Row[] rows = CreateNumberedRows(3f, 1f, 2f);
        SeekableInMemoryProvider provider = new(rows);

        TableDescriptor descriptor = CreateDescriptor("data");
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        catalog.Register(descriptor);

        // Register an index without sorted indexes.
        SourceIndex sourceIndex = new(
            new SourceFingerprint(100, DummyHash),
            new IndexSchema(new Schema([new ColumnInfo("value", DataKind.Float32, false)]), 3),
            [CreateChunk(0, 3)]);
        catalog.RegisterIndex("data", sourceIndex);

        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("data")),
            OrderBy: new OrderByClause(
            [
                new OrderByItem(new ColumnReference("value"), SortDirection.Ascending)
            ]));

        IQueryOperator plan = planner.Plan(statement);

        // No sorted index → must fall back to OrderByOperator.
        Assert.IsType<OrderByOperator>(plan);
    }

    [Fact]
    public void Plan_OrderByMultipleColumns_FallsBackToOrderByOperator()
    {
        Row[] rows = CreateNumberedRows(3f, 1f, 2f);
        SeekableInMemoryProvider provider = new(rows);

        TableDescriptor descriptor = CreateDescriptor("data");
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        catalog.Register(descriptor);

        SourceIndex sourceIndex = CreateSourceIndexWithSort("value", rows);
        catalog.RegisterIndex("data", sourceIndex);

        QueryPlanner planner = new(catalog, DefaultFunctions);

        // ORDER BY value, index — multi-column, not eligible for index scan.
        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("data")),
            OrderBy: new OrderByClause(
            [
                new OrderByItem(new ColumnReference("value"), SortDirection.Ascending),
                new OrderByItem(new ColumnReference("index"), SortDirection.Ascending),
            ]));

        IQueryOperator plan = planner.Plan(statement);

        Assert.IsType<OrderByOperator>(plan);
    }

    [Fact]
    public async Task Plan_OrderByWithSortedIndex_ExecutesCorrectly()
    {
        Row[] rows = CreateNumberedRows(30f, 10f, 50f, 20f, 40f);
        SeekableInMemoryProvider provider = new(rows);

        TableDescriptor descriptor = CreateDescriptor("data");
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        catalog.Register(descriptor);

        SourceIndex sourceIndex = CreateSourceIndexWithSort("value", rows);
        catalog.RegisterIndex("data", sourceIndex);

        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("data")),
            OrderBy: new OrderByClause(
            [
                new OrderByItem(new ColumnReference("value"), SortDirection.Ascending)
            ]));

        IQueryOperator plan = planner.Plan(statement);

        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog, new LocalBufferPool());
        List<Row> results = await CollectRowsAsync(plan.ExecuteAsync(context));

        Assert.Equal(5, results.Count);
        Assert.Equal(10f, results[0]["value"].AsFloat32());
        Assert.Equal(20f, results[1]["value"].AsFloat32());
        Assert.Equal(30f, results[2]["value"].AsFloat32());
        Assert.Equal(40f, results[3]["value"].AsFloat32());
        Assert.Equal(50f, results[4]["value"].AsFloat32());
    }

    [Fact]
    public async Task Plan_OrderByDescWithSortedIndex_ExecutesCorrectly()
    {
        Row[] rows = CreateNumberedRows(30f, 10f, 50f, 20f, 40f);
        SeekableInMemoryProvider provider = new(rows);

        TableDescriptor descriptor = CreateDescriptor("data");
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        catalog.Register(descriptor);

        SourceIndex sourceIndex = CreateSourceIndexWithSort("value", rows);
        catalog.RegisterIndex("data", sourceIndex);

        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("data")),
            OrderBy: new OrderByClause(
            [
                new OrderByItem(new ColumnReference("value"), SortDirection.Descending)
            ]));

        IQueryOperator plan = planner.Plan(statement);

        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog, new LocalBufferPool());
        List<Row> results = await CollectRowsAsync(plan.ExecuteAsync(context));

        Assert.Equal(5, results.Count);
        Assert.Equal(50f, results[0]["value"].AsFloat32());
        Assert.Equal(40f, results[1]["value"].AsFloat32());
        Assert.Equal(30f, results[2]["value"].AsFloat32());
        Assert.Equal(20f, results[3]["value"].AsFloat32());
        Assert.Equal(10f, results[4]["value"].AsFloat32());
    }

    [Fact]
    public async Task Plan_OrderByWithLimitAndSortedIndex_ReturnsTopN()
    {
        Row[] rows = CreateNumberedRows(30f, 10f, 50f, 20f, 40f);
        SeekableInMemoryProvider provider = new(rows);

        TableDescriptor descriptor = CreateDescriptor("data");
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        catalog.Register(descriptor);

        SourceIndex sourceIndex = CreateSourceIndexWithSort("value", rows);
        catalog.RegisterIndex("data", sourceIndex);

        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("data")),
            OrderBy: new OrderByClause(
            [
                new OrderByItem(new ColumnReference("value"), SortDirection.Ascending)
            ]),
            Limit: 3);

        IQueryOperator plan = planner.Plan(statement);

        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog, new LocalBufferPool());
        List<Row> results = await CollectRowsAsync(plan.ExecuteAsync(context));

        Assert.Equal(3, results.Count);
        Assert.Equal(10f, results[0]["value"].AsFloat32());
        Assert.Equal(20f, results[1]["value"].AsFloat32());
        Assert.Equal(30f, results[2]["value"].AsFloat32());
    }

    // ───────────────────── WHERE index seek tests ─────────────────────

    [Fact]
    public async Task Scan_WhereEqualityWithSortedIndex_SeeksToMatchingRows()
    {
        // 10 rows with values 0..9. WHERE value = 5 should yield exactly 1 row.
        Row[] rows = CreateNumberedRows(
            0f, 1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f);
        SeekableInMemoryProvider provider = new(rows);

        TableDescriptor descriptor = CreateDescriptor("data");
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        catalog.Register(descriptor);

        SourceIndex sourceIndex = CreateSourceIndexWithSort("value", rows);
        catalog.RegisterIndex("data", sourceIndex);

        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("data")),
            Where: new BinaryExpression(
                new ColumnReference("value"),
                BinaryOperator.Equal,
                new LiteralExpression(5.0)));

        IQueryOperator plan = planner.Plan(statement);

        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog, new LocalBufferPool());
        List<Row> results = await CollectRowsAsync(plan.ExecuteAsync(context));

        Assert.Single(results);
        Assert.Equal(5f, results[0]["value"].AsFloat32());
    }

    [Fact]
    public async Task Scan_WhereEqualityWithSortedIndex_MultipleMatches()
    {
        // Duplicate values: [1, 2, 3, 2, 1]. WHERE value = 2 yields 2 rows.
        Row[] rows =
        [
            new(["index", "value"], [DataValue.FromFloat32(0f), DataValue.FromFloat32(1f)]),
            new(["index", "value"], [DataValue.FromFloat32(1f), DataValue.FromFloat32(2f)]),
            new(["index", "value"], [DataValue.FromFloat32(2f), DataValue.FromFloat32(3f)]),
            new(["index", "value"], [DataValue.FromFloat32(3f), DataValue.FromFloat32(2f)]),
            new(["index", "value"], [DataValue.FromFloat32(4f), DataValue.FromFloat32(1f)]),
        ];
        SeekableInMemoryProvider provider = new(rows);

        TableDescriptor descriptor = CreateDescriptor("data");
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        catalog.Register(descriptor);

        SourceIndex sourceIndex = CreateSourceIndexWithSortFromRows("value", rows);
        catalog.RegisterIndex("data", sourceIndex);

        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("data")),
            Where: new BinaryExpression(
                new ColumnReference("value"),
                BinaryOperator.Equal,
                new LiteralExpression(2.0)));

        IQueryOperator plan = planner.Plan(statement);

        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog, new LocalBufferPool());
        List<Row> results = await CollectRowsAsync(plan.ExecuteAsync(context));

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(2f, r["value"].AsFloat32()));
    }

    [Fact]
    public async Task Scan_WhereEqualityWithSortedIndex_NoMatches()
    {
        Row[] rows = CreateNumberedRows(1f, 2f, 3f);
        SeekableInMemoryProvider provider = new(rows);

        TableDescriptor descriptor = CreateDescriptor("data");
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        catalog.Register(descriptor);

        SourceIndex sourceIndex = CreateSourceIndexWithSort("value", rows);
        catalog.RegisterIndex("data", sourceIndex);

        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("data")),
            Where: new BinaryExpression(
                new ColumnReference("value"),
                BinaryOperator.Equal,
                new LiteralExpression(99.0)));

        IQueryOperator plan = planner.Plan(statement);

        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog, new LocalBufferPool());
        List<Row> results = await CollectRowsAsync(plan.ExecuteAsync(context));

        Assert.Empty(results);
    }

    [Fact]
    public async Task Scan_WhereCompoundAndWithSortedIndex_SeeksAndFilters()
    {
        // WHERE value = 2 AND index > 1 → index seek on value, filter on index.
        Row[] rows =
        [
            new(["index", "value"], [DataValue.FromFloat32(0f), DataValue.FromFloat32(2f)]),
            new(["index", "value"], [DataValue.FromFloat32(1f), DataValue.FromFloat32(2f)]),
            new(["index", "value"], [DataValue.FromFloat32(2f), DataValue.FromFloat32(2f)]),
            new(["index", "value"], [DataValue.FromFloat32(3f), DataValue.FromFloat32(3f)]),
        ];
        SeekableInMemoryProvider provider = new(rows);

        TableDescriptor descriptor = CreateDescriptor("data");
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        catalog.Register(descriptor);

        SourceIndex sourceIndex = CreateSourceIndexWithSortFromRows("value", rows);
        catalog.RegisterIndex("data", sourceIndex);

        QueryPlanner planner = new(catalog, DefaultFunctions);

        // WHERE value = 2 AND index > 1
        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("data")),
            Where: new BinaryExpression(
                new BinaryExpression(
                    new ColumnReference("value"),
                    BinaryOperator.Equal,
                    new LiteralExpression(2.0)),
                BinaryOperator.And,
                new BinaryExpression(
                    new ColumnReference("index"),
                    BinaryOperator.GreaterThan,
                    new LiteralExpression(1.0))));

        IQueryOperator plan = planner.Plan(statement);

        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog, new LocalBufferPool());
        List<Row> results = await CollectRowsAsync(plan.ExecuteAsync(context));

        // value=2 matches rows at index 0,1,2. index>1 further filters to index 2 only.
        Assert.Single(results);
        Assert.Equal(2f, results[0]["index"].AsFloat32());
    }

    [Fact]
    public async Task Scan_WhereOrWithSortedIndex_DoesNotUseIndexSeek()
    {
        // OR predicates cannot use index seek — must scan all.
        Row[] rows = CreateNumberedRows(1f, 2f, 3f, 4f, 5f);
        SeekableInMemoryProvider provider = new(rows);

        TableDescriptor descriptor = CreateDescriptor("data");
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        catalog.Register(descriptor);

        SourceIndex sourceIndex = CreateSourceIndexWithSort("value", rows);
        catalog.RegisterIndex("data", sourceIndex);

        QueryPlanner planner = new(catalog, DefaultFunctions);

        // WHERE value = 2 OR value = 4
        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("data")),
            Where: new BinaryExpression(
                new BinaryExpression(
                    new ColumnReference("value"),
                    BinaryOperator.Equal,
                    new LiteralExpression(2.0)),
                BinaryOperator.Or,
                new BinaryExpression(
                    new ColumnReference("value"),
                    BinaryOperator.Equal,
                    new LiteralExpression(4.0))));

        IQueryOperator plan = planner.Plan(statement);

        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog, new LocalBufferPool());
        List<Row> results = await CollectRowsAsync(plan.ExecuteAsync(context));

        // Should still produce correct results via normal scan + filter.
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task Scan_WhereEqualityWithSortedIndex_ReportsExactSeekRowsFetched()
    {
        // Verify the exact seek path is used by checking the ScanOperator metric.
        Row[] rows = CreateNumberedRows(
            0f, 1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f);
        SeekableInMemoryProvider provider = new(rows);

        TableDescriptor descriptor = CreateDescriptor("data");
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        catalog.Register(descriptor);

        SourceIndex sourceIndex = CreateSourceIndexWithSort("value", rows);
        catalog.RegisterIndex("data", sourceIndex);

        // Build operator tree manually to access ScanOperator properties.
        ScanOperator scan = new(descriptor, null);
        scan.SetSourceIndex(sourceIndex);
        scan.AddFilterHint(new BinaryExpression(
            new ColumnReference("value"),
            BinaryOperator.Equal,
            new LiteralExpression(5.0)));

        FilterOperator filter = new(scan, new BinaryExpression(
            new ColumnReference("value"),
            BinaryOperator.Equal,
            new LiteralExpression(5.0)));

        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog, new LocalBufferPool());
        List<Row> results = await CollectRowsAsync(filter.ExecuteAsync(context));

        Assert.Single(results);
        Assert.Equal(5f, results[0]["value"].AsFloat32());
        Assert.Equal(1, scan.ExactSeekRowsFetched);
    }

    // ───────────────────── Range predicate pruning tests ─────────────────────

    [Fact]
    public async Task Scan_WhereLessThanWithSortedIndex_PrunesChunks()
    {
        // 10 rows in 2 chunks of 5: chunk 0 has values 1-5, chunk 1 has values 6-10.
        // WHERE value < 6 → chunk 1 has no values < 6, so it should be pruned.
        Row[] rows = CreateNumberedRows(1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f);
        SeekableInMemoryProvider provider = new(rows);

        TableDescriptor descriptor = CreateDescriptor("data");
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        catalog.Register(descriptor);

        SourceIndex sourceIndex = CreateMultiChunkSourceIndex("value", rows, chunkSize: 5);
        catalog.RegisterIndex("data", sourceIndex);

        ScanOperator scan = new(descriptor, null);
        scan.SetSourceIndex(sourceIndex);
        scan.AddFilterHint(new BinaryExpression(
            new ColumnReference("value"),
            BinaryOperator.LessThan,
            new LiteralExpression(6.0)));

        FilterOperator filter = new(scan, new BinaryExpression(
            new ColumnReference("value"),
            BinaryOperator.LessThan,
            new LiteralExpression(6.0)));

        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog, new LocalBufferPool());
        List<Row> results = await CollectRowsAsync(filter.ExecuteAsync(context));

        Assert.Equal(5, results.Count);
        Assert.Equal(1, scan.PrunedIndexChunks);
    }

    [Fact]
    public async Task Scan_WhereGreaterThanWithSortedIndex_PrunesChunks()
    {
        Row[] rows = CreateNumberedRows(1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f);
        SeekableInMemoryProvider provider = new(rows);

        TableDescriptor descriptor = CreateDescriptor("data");
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        catalog.Register(descriptor);

        SourceIndex sourceIndex = CreateMultiChunkSourceIndex("value", rows, chunkSize: 5);
        catalog.RegisterIndex("data", sourceIndex);

        ScanOperator scan = new(descriptor, null);
        scan.SetSourceIndex(sourceIndex);
        scan.AddFilterHint(new BinaryExpression(
            new ColumnReference("value"),
            BinaryOperator.GreaterThan,
            new LiteralExpression(5.0)));

        FilterOperator filter = new(scan, new BinaryExpression(
            new ColumnReference("value"),
            BinaryOperator.GreaterThan,
            new LiteralExpression(5.0)));

        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog, new LocalBufferPool());
        List<Row> results = await CollectRowsAsync(filter.ExecuteAsync(context));

        Assert.Equal(5, results.Count);
        Assert.Equal(1, scan.PrunedIndexChunks);
    }

    [Fact]
    public async Task Scan_WhereLessThanOrEqualWithSortedIndex_PrunesChunks()
    {
        // WHERE value <= 5 → chunk 1 (values 6-10) should be pruned.
        Row[] rows = CreateNumberedRows(1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f);
        SeekableInMemoryProvider provider = new(rows);

        TableDescriptor descriptor = CreateDescriptor("data");
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        catalog.Register(descriptor);

        SourceIndex sourceIndex = CreateMultiChunkSourceIndex("value", rows, chunkSize: 5);
        catalog.RegisterIndex("data", sourceIndex);

        ScanOperator scan = new(descriptor, null);
        scan.SetSourceIndex(sourceIndex);
        scan.AddFilterHint(new BinaryExpression(
            new ColumnReference("value"),
            BinaryOperator.LessThanOrEqual,
            new LiteralExpression(5.0)));

        FilterOperator filter = new(scan, new BinaryExpression(
            new ColumnReference("value"),
            BinaryOperator.LessThanOrEqual,
            new LiteralExpression(5.0)));

        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog, new LocalBufferPool());
        List<Row> results = await CollectRowsAsync(filter.ExecuteAsync(context));

        Assert.Equal(5, results.Count);
        Assert.Equal(1, scan.PrunedIndexChunks);
    }

    [Fact]
    public async Task Scan_WhereBetweenWithSortedIndex_PrunesChunks()
    {
        // 15 rows in 3 chunks of 5: chunk 0 [1-5], chunk 1 [6-10], chunk 2 [11-15].
        // WHERE value BETWEEN 6 AND 10 → chunks 0 and 2 should be pruned.
        Row[] rows = CreateNumberedRows(
            1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f, 11f, 12f, 13f, 14f, 15f);
        SeekableInMemoryProvider provider = new(rows);

        TableDescriptor descriptor = CreateDescriptor("data");
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        catalog.Register(descriptor);

        SourceIndex sourceIndex = CreateMultiChunkSourceIndex("value", rows, chunkSize: 5);
        catalog.RegisterIndex("data", sourceIndex);

        ScanOperator scan = new(descriptor, null);
        scan.SetSourceIndex(sourceIndex);
        scan.AddFilterHint(new BetweenExpression(
            new ColumnReference("value"),
            new LiteralExpression(6.0),
            new LiteralExpression(10.0)));

        FilterOperator filter = new(scan, new BetweenExpression(
            new ColumnReference("value"),
            new LiteralExpression(6.0),
            new LiteralExpression(10.0)));

        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog, new LocalBufferPool());
        List<Row> results = await CollectRowsAsync(filter.ExecuteAsync(context));

        Assert.Equal(5, results.Count);
        Assert.Equal(2, scan.PrunedIndexChunks);
    }

    [Fact]
    public async Task Scan_WhereInWithSortedIndex_PrunesChunks()
    {
        // 10 rows in 2 chunks of 5: chunk 0 [1-5], chunk 1 [6-10].
        // WHERE value IN (1, 3) → only chunk 0 matched, chunk 1 pruned.
        Row[] rows = CreateNumberedRows(1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f);
        SeekableInMemoryProvider provider = new(rows);

        TableDescriptor descriptor = CreateDescriptor("data");
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        catalog.Register(descriptor);

        SourceIndex sourceIndex = CreateMultiChunkSourceIndex("value", rows, chunkSize: 5);
        catalog.RegisterIndex("data", sourceIndex);

        ScanOperator scan = new(descriptor, null);
        scan.SetSourceIndex(sourceIndex);
        scan.AddFilterHint(new InExpression(
            new ColumnReference("value"),
            [new LiteralExpression(1.0), new LiteralExpression(3.0)]));

        FilterOperator filter = new(scan, new InExpression(
            new ColumnReference("value"),
            [new LiteralExpression(1.0), new LiteralExpression(3.0)]));

        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog, new LocalBufferPool());
        List<Row> results = await CollectRowsAsync(filter.ExecuteAsync(context));

        Assert.Equal(2, results.Count);
        Assert.Equal(1, scan.PrunedIndexChunks);
    }

    // ───────────────────── Range predicate row seek tests ─────────────────────

    [Fact]
    public async Task Scan_WhereBetweenWithSortedIndex_SeeksToMatchingRows()
    {
        // Single chunk with 10 rows. BETWEEN 3 AND 7 should seek to 5 rows.
        Row[] rows = CreateNumberedRows(1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f);
        SeekableInMemoryProvider provider = new(rows);

        TableDescriptor descriptor = CreateDescriptor("data");
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        catalog.Register(descriptor);

        SourceIndex sourceIndex = CreateSourceIndexWithSort("value", rows);
        catalog.RegisterIndex("data", sourceIndex);

        ScanOperator scan = new(descriptor, null);
        scan.SetSourceIndex(sourceIndex);
        scan.AddFilterHint(new BetweenExpression(
            new ColumnReference("value"),
            new LiteralExpression(3.0),
            new LiteralExpression(7.0)));

        FilterOperator filter = new(scan, new BetweenExpression(
            new ColumnReference("value"),
            new LiteralExpression(3.0),
            new LiteralExpression(7.0)));

        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog, new LocalBufferPool());
        List<Row> results = await CollectRowsAsync(filter.ExecuteAsync(context));

        Assert.Equal(5, results.Count);
        Assert.Equal(5, scan.ExactSeekRowsFetched);
        Assert.All(results, r =>
        {
            float value = r["value"].AsFloat32();
            Assert.InRange(value, 3f, 7f);
        });
    }

    [Fact]
    public async Task Scan_WhereInWithSortedIndex_SeeksToMatchingRows()
    {
        Row[] rows = CreateNumberedRows(1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f);
        SeekableInMemoryProvider provider = new(rows);

        TableDescriptor descriptor = CreateDescriptor("data");
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        catalog.Register(descriptor);

        SourceIndex sourceIndex = CreateSourceIndexWithSort("value", rows);
        catalog.RegisterIndex("data", sourceIndex);

        ScanOperator scan = new(descriptor, null);
        scan.SetSourceIndex(sourceIndex);
        scan.AddFilterHint(new InExpression(
            new ColumnReference("value"),
            [new LiteralExpression(2.0), new LiteralExpression(5.0), new LiteralExpression(8.0)]));

        FilterOperator filter = new(scan, new InExpression(
            new ColumnReference("value"),
            [new LiteralExpression(2.0), new LiteralExpression(5.0), new LiteralExpression(8.0)]));

        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog, new LocalBufferPool());
        List<Row> results = await CollectRowsAsync(filter.ExecuteAsync(context));

        Assert.Equal(3, results.Count);
        Assert.Equal(3, scan.ExactSeekRowsFetched);
        float[] expected = [2f, 5f, 8f];
        Assert.Equal(expected, results.Select(r => r["value"].AsFloat32()).OrderBy(v => v).ToArray());
    }

    [Fact]
    public async Task Scan_WhereFlippedLiteralComparison_PrunesCorrectly()
    {
        // Test "5 > value" (literal on left) — should be handled like "value < 5".
        Row[] rows = CreateNumberedRows(1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f);
        SeekableInMemoryProvider provider = new(rows);

        TableDescriptor descriptor = CreateDescriptor("data");
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        catalog.Register(descriptor);

        SourceIndex sourceIndex = CreateMultiChunkSourceIndex("value", rows, chunkSize: 5);
        catalog.RegisterIndex("data", sourceIndex);

        // 5 > value means value < 5 (flipped).
        Expression predicate = new BinaryExpression(
            new LiteralExpression(5.0),
            BinaryOperator.GreaterThan,
            new ColumnReference("value"));

        ScanOperator scan = new(descriptor, null);
        scan.SetSourceIndex(sourceIndex);
        scan.AddFilterHint(predicate);

        FilterOperator filter = new(scan, predicate);

        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog, new LocalBufferPool());
        List<Row> results = await CollectRowsAsync(filter.ExecuteAsync(context));

        // value < 5 → rows with values 1,2,3,4 from chunk 0.
        Assert.Equal(4, results.Count);
        Assert.Equal(1, scan.PrunedIndexChunks);
    }

    // ───────────────────── B+Tree index tests ─────────────────────

    [Fact]
    public async Task Scan_WhereEqualityWithBPlusTreeIndex_PrunesChunks()
    {
        // 10 rows in 2 chunks of 5: chunk 0 [1-5], chunk 1 [6-10].
        // WHERE value = 3 → chunk 1 should be pruned (no key=3 there).
        Row[] rows = CreateNumberedRows(1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f);
        SeekableInMemoryProvider provider = new(rows);

        TableDescriptor descriptor = CreateDescriptor("data");
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        catalog.Register(descriptor);

        SourceIndex sourceIndex = CreateMultiChunkSourceIndexWithBPlusTree("value", rows, chunkSize: 5);
        catalog.RegisterIndex("data", sourceIndex);

        ScanOperator scan = new(descriptor, null);
        scan.SetSourceIndex(sourceIndex);
        scan.AddFilterHint(new BinaryExpression(
            new ColumnReference("value"),
            BinaryOperator.Equal,
            new LiteralExpression(3.0)));

        FilterOperator filter = new(scan, new BinaryExpression(
            new ColumnReference("value"),
            BinaryOperator.Equal,
            new LiteralExpression(3.0)));

        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog, new LocalBufferPool());
        List<Row> results = await CollectRowsAsync(filter.ExecuteAsync(context));

        Assert.Single(results);
        Assert.Equal(3f, results[0]["value"].AsFloat32());
        Assert.Equal(1, scan.PrunedIndexChunks);
    }

    [Fact]
    public async Task Scan_WhereEqualityWithBPlusTreeIndex_SeeksToMatchingRow()
    {
        // Single chunk with 10 rows. WHERE value = 5 should use exact seek.
        Row[] rows = CreateNumberedRows(1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f);
        SeekableInMemoryProvider provider = new(rows);

        TableDescriptor descriptor = CreateDescriptor("data");
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        catalog.Register(descriptor);

        SourceIndex sourceIndex = CreateSourceIndexWithBPlusTree("value", rows);
        catalog.RegisterIndex("data", sourceIndex);

        ScanOperator scan = new(descriptor, null);
        scan.SetSourceIndex(sourceIndex);
        scan.AddFilterHint(new BinaryExpression(
            new ColumnReference("value"),
            BinaryOperator.Equal,
            new LiteralExpression(5.0)));

        FilterOperator filter = new(scan, new BinaryExpression(
            new ColumnReference("value"),
            BinaryOperator.Equal,
            new LiteralExpression(5.0)));

        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog, new LocalBufferPool());
        List<Row> results = await CollectRowsAsync(filter.ExecuteAsync(context));

        Assert.Single(results);
        Assert.Equal(5f, results[0]["value"].AsFloat32());
        Assert.Equal(1, scan.ExactSeekRowsFetched);
    }

    [Fact]
    public void Plan_OrderByWithBPlusTreeIndex_SubstitutesIndexScan()
    {
        Row[] rows = CreateNumberedRows(3f, 1f, 2f);
        SeekableInMemoryProvider provider = new(rows);

        TableDescriptor descriptor = CreateDescriptor("data");
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        catalog.Register(descriptor);

        SourceIndex sourceIndex = CreateSourceIndexWithBPlusTree("value", rows);
        catalog.RegisterIndex("data", sourceIndex);

        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("data")),
            OrderBy: new OrderByClause(
            [
                new OrderByItem(new ColumnReference("value"), SortDirection.Ascending)
            ]));

        IQueryOperator plan = planner.Plan(statement);

        Assert.IsType<IndexScanOperator>(plan);
    }

    [Fact]
    public async Task Plan_OrderByWithBPlusTreeIndex_ExecutesCorrectly()
    {
        Row[] rows = CreateNumberedRows(30f, 10f, 50f, 20f, 40f);
        SeekableInMemoryProvider provider = new(rows);

        TableDescriptor descriptor = CreateDescriptor("data");
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        catalog.Register(descriptor);

        SourceIndex sourceIndex = CreateSourceIndexWithBPlusTree("value", rows);
        catalog.RegisterIndex("data", sourceIndex);

        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("data")),
            OrderBy: new OrderByClause(
            [
                new OrderByItem(new ColumnReference("value"), SortDirection.Ascending)
            ]));

        IQueryOperator plan = planner.Plan(statement);

        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog, new LocalBufferPool());
        List<Row> results = await CollectRowsAsync(plan.ExecuteAsync(context));

        Assert.Equal(5, results.Count);
        Assert.Equal(10f, results[0]["value"].AsFloat32());
        Assert.Equal(20f, results[1]["value"].AsFloat32());
        Assert.Equal(30f, results[2]["value"].AsFloat32());
        Assert.Equal(40f, results[3]["value"].AsFloat32());
        Assert.Equal(50f, results[4]["value"].AsFloat32());
    }

    // ───────────────────── B+Tree helpers ─────────────────────

    /// <summary>
    /// Creates a <see cref="SourceIndex"/> with a B+Tree index (no sorted indexes)
    /// on the given column, with all rows in a single chunk.
    /// </summary>
    private static SourceIndex CreateSourceIndexWithBPlusTree(string columnName, Row[] rows)
    {
        ValueIndexEntry[] entries = new ValueIndexEntry[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            entries[i] = new ValueIndexEntry(rows[i][columnName], ChunkIndex: 0, RowOffsetInChunk: i);
        }

        // Sort entries by key for the bulk loader.
        Array.Sort(entries, (a, b) => StatisticsPredicateEvaluator.CompareValues(a.Key, b.Key));

        BPlusTreeIndexSet bTreeSet = BuildBPlusTreeIndexSet(columnName, DataKind.Float32, entries);

        IndexChunk chunk = new(0, rows.Length, -1, -1,
            new Dictionary<string, ChunkColumnStatistics>());

        return new SourceIndex(
            new SourceFingerprint(100, DummyHash),
            new IndexSchema(
                new Schema([
                    new ColumnInfo("index", DataKind.Float32, false),
                    new ColumnInfo("value", DataKind.Float32, false),
                ]),
                rows.Length),
            [chunk],
            bloomFilters: null,
            sortedIndexes: null,
            zipDirectory: null,
            bPlusTreeIndexes: bTreeSet);
    }

    /// <summary>
    /// Creates a <see cref="SourceIndex"/> with a B+Tree index (no sorted indexes)
    /// distributed across multiple chunks.
    /// </summary>
    private static SourceIndex CreateMultiChunkSourceIndexWithBPlusTree(
        string columnName, Row[] allRows, int chunkSize)
    {
        int chunkCount = (allRows.Length + chunkSize - 1) / chunkSize;
        List<IndexChunk> chunks = new();
        List<ValueIndexEntry> entries = new();

        for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            int start = chunkIndex * chunkSize;
            int count = Math.Min(chunkSize, allRows.Length - start);
            chunks.Add(new IndexChunk(start, count, -1, -1,
                new Dictionary<string, ChunkColumnStatistics>()));

            for (int row = 0; row < count; row++)
            {
                entries.Add(new ValueIndexEntry(
                    allRows[start + row][columnName], chunkIndex, row));
            }
        }

        ValueIndexEntry[] sorted = entries.ToArray();
        Array.Sort(sorted, (a, b) => StatisticsPredicateEvaluator.CompareValues(a.Key, b.Key));

        BPlusTreeIndexSet bTreeSet = BuildBPlusTreeIndexSet(columnName, DataKind.Float32, sorted);

        ColumnInfo[] columns = allRows[0].ColumnNames
            .Select(name => new ColumnInfo(name, DataKind.Float32, false))
            .ToArray();

        return new SourceIndex(
            new SourceFingerprint(100, DummyHash),
            new IndexSchema(new Schema(columns), allRows.Length),
            chunks,
            bloomFilters: null,
            sortedIndexes: null,
            zipDirectory: null,
            bPlusTreeIndexes: bTreeSet);
    }

    private static BPlusTreeIndexSet BuildBPlusTreeIndexSet(
        string columnName, DataKind keyKind, ValueIndexEntry[] sortedEntries)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);

        BPlusTreeSectionHeader? header = BPlusTreeBulkLoader.Build(
            sortedEntries, columnName, keyKind, writer);

        if (header is null)
        {
            throw new InvalidOperationException("BPlusTreeBulkLoader.Build returned null for non-empty entries.");
        }

        stream.Position = 0;
        using BinaryReader binaryReader = new(stream, Encoding.UTF8, leaveOpen: true);
        BPlusTreeSectionHeader readHeader = BPlusTreeBulkLoader.ReadSectionHeader(binaryReader);

        byte[][] pages = new byte[readHeader.PageCount][];

        for (uint pageIndex = 0; pageIndex < readHeader.PageCount; pageIndex++)
        {
            pages[pageIndex] = binaryReader.ReadBytes(BPlusTreeConstants.PageSize);
        }

        BPlusTreeReader treeReader = new(readHeader, pages);
        Dictionary<string, BPlusTreeColumnIndex> indexes = new(StringComparer.OrdinalIgnoreCase)
        {
            [columnName] = new BPlusTreeColumnIndex(treeReader),
        };

        return new BPlusTreeIndexSet(indexes);
    }

    // ───────────────────── Helpers ─────────────────────

    private static TableDescriptor CreateDescriptor(string name)
    {
        return new TableDescriptor("test", name, $"{name}.test",
            new Dictionary<string, string>());
    }

    private static IndexChunk CreateChunk(long rowOffset, long rowCount)
    {
        return new IndexChunk(rowOffset, rowCount, -1, -1,
            new Dictionary<string, ChunkColumnStatistics>());
    }

    /// <summary>
    /// Creates rows with "index" (sequential) and "value" columns.
    /// </summary>
    private static Row[] CreateNumberedRows(params float[] values)
    {
        Row[] rows = new Row[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            rows[i] = new Row(
                ["index", "value"],
                [DataValue.FromFloat32(i), DataValue.FromFloat32(values[i])]);
        }

        return rows;
    }

    /// <summary>
    /// Creates a <see cref="SourceIndex"/> with a sorted value index on the given column,
    /// built from the row data.
    /// </summary>
    private static SourceIndex CreateSourceIndexWithSort(string columnName, Row[] rows)
    {
        ValueIndexEntry[] entries = new ValueIndexEntry[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            entries[i] = new ValueIndexEntry(rows[i][columnName], ChunkIndex: 0, RowOffsetInChunk: i);
        }

        SortedValueIndex sortedIndex = SortedValueIndex.BuildFromUnsorted(entries);
        Dictionary<string, SortedValueIndex> indexes =
            new(StringComparer.OrdinalIgnoreCase) { [columnName] = sortedIndex };
        SortedValueIndexSet sortedSet = new(indexes);

        IndexChunk chunk = new(0, rows.Length, -1, -1,
            new Dictionary<string, ChunkColumnStatistics>());

        return new SourceIndex(
            new SourceFingerprint(100, DummyHash),
            new IndexSchema(
                new Schema([
                    new ColumnInfo("index", DataKind.Float32, false),
                    new ColumnInfo("value", DataKind.Float32, false),
                ]),
                rows.Length),
            [chunk],
            sortedIndexes: sortedSet);
    }

    /// <summary>
    /// Creates a <see cref="SourceIndex"/> with a sorted value index, deriving column
    /// schema from the rows themselves. Handles rows with arbitrary column layouts.
    /// </summary>
    private static SourceIndex CreateSourceIndexWithSortFromRows(string columnName, Row[] rows)
    {
        ValueIndexEntry[] entries = new ValueIndexEntry[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            entries[i] = new ValueIndexEntry(rows[i][columnName], ChunkIndex: 0, RowOffsetInChunk: i);
        }

        SortedValueIndex sortedIndex = SortedValueIndex.BuildFromUnsorted(entries);
        Dictionary<string, SortedValueIndex> indexes =
            new(StringComparer.OrdinalIgnoreCase) { [columnName] = sortedIndex };
        SortedValueIndexSet sortedSet = new(indexes);

        IndexChunk chunk = new(0, rows.Length, -1, -1,
            new Dictionary<string, ChunkColumnStatistics>());

        ColumnInfo[] columns = rows[0].ColumnNames
            .Select(name => new ColumnInfo(name, DataKind.Float32, false))
            .ToArray();

        return new SourceIndex(
            new SourceFingerprint(100, DummyHash),
            new IndexSchema(new Schema(columns), rows.Length),
            [chunk],
            sortedIndexes: sortedSet);
    }

    /// <summary>
    /// Creates a <see cref="SourceIndex"/> with multiple chunks of the given size,
    /// each with a sorted value index on the specified column.
    /// </summary>
    private static SourceIndex CreateMultiChunkSourceIndex(
        string columnName, Row[] allRows, int chunkSize)
    {
        int chunkCount = (allRows.Length + chunkSize - 1) / chunkSize;
        List<IndexChunk> chunks = new();
        List<ValueIndexEntry> entries = new();

        for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            int start = chunkIndex * chunkSize;
            int count = Math.Min(chunkSize, allRows.Length - start);
            chunks.Add(new IndexChunk(start, count, -1, -1,
                new Dictionary<string, ChunkColumnStatistics>()));

            for (int row = 0; row < count; row++)
            {
                entries.Add(new ValueIndexEntry(
                    allRows[start + row][columnName], chunkIndex, row));
            }
        }

        SortedValueIndex sortedIndex = SortedValueIndex.BuildFromUnsorted(entries.ToArray());
        Dictionary<string, SortedValueIndex> indexes =
            new(StringComparer.OrdinalIgnoreCase) { [columnName] = sortedIndex };
        SortedValueIndexSet sortedSet = new(indexes);

        ColumnInfo[] columns = allRows[0].ColumnNames
            .Select(name => new ColumnInfo(name, DataKind.Float32, false))
            .ToArray();

        return new SourceIndex(
            new SourceFingerprint(100, DummyHash),
            new IndexSchema(new Schema(columns), allRows.Length),
            chunks,
            sortedIndexes: sortedSet);
    }

    private static async Task<List<Row>> CollectRowsAsync(IAsyncEnumerable<Row> source)
    {
        List<Row> rows = new();
        await foreach (Row row in source)
        {
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>
    /// In-memory provider that supports seeking for testing <see cref="IndexScanOperator"/>.
    /// </summary>
    private sealed class SeekableInMemoryProvider : ISeekableTableProvider
    {
        private readonly Row[] _rows;

        public SeekableInMemoryProvider(Row[] rows)
        {
            _rows = rows;
        }

        public Task<Schema> GetSchemaAsync(
            TableDescriptor descriptor, CancellationToken cancellationToken)
        {
            ColumnInfo[] columns = _rows[0].ColumnNames
                .Select(name => new ColumnInfo(name, DataKind.Float32, false))
                .ToArray();
            return Task.FromResult(new Schema(columns));
        }

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

        public Task<ProviderCapabilities> GetCapabilitiesAsync(
            TableDescriptor descriptor, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ProviderCapabilities(
                EstimatedRowCount: _rows.Length,
                EstimatedRowSizeBytes: null,
                SupportsSeek: true,
                ColumnCosts: new Dictionary<string, ColumnCost>()));
        }

        public async IAsyncEnumerable<Row> ReadRowRangeAsync(
            TableDescriptor descriptor,
            IReadOnlySet<string>? requiredColumns,
            long startRow,
            int count,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            long end = Math.Min(startRow + count, _rows.Length);

            for (long i = startRow; i < end; i++)
            {
                yield return _rows[i];
            }

            await Task.CompletedTask;
        }
    }
}
