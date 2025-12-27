using System.Text;
using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Indexing;
using DatumIngest.Indexing.BTree;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for <see cref="IndexScanOperator"/> sorted iteration and
/// <see cref="QueryPlanner"/> substitution of ORDER BY with index scan,
/// plus <see cref="ScanOperator"/> WHERE-predicate seek and chunk pruning
/// via column indexes. All indexes are built via <see cref="BPlusTreeBulkLoader"/>.
/// </summary>
public sealed class IndexScanOperatorTests : ServiceTestBase
{
    private static readonly FunctionRegistry DefaultFunctions = FunctionRegistry.CreateDefault();
    private static readonly byte[] DummyHash = new byte[32];

    // ───────────────────── IndexScanOperator unit tests ─────────────────────

    [Fact]
    public async Task IndexScan_Ascending_YieldsRowsInSortedOrder()
    {
        // 5 rows with values [30, 10, 40, 20, 50] — sorted ascending = [10, 20, 30, 40, 50].
        Row[] rows = CreateNumberedRows(30f, 10f, 40f, 20f, 50f);
        InMemoryTableProvider provider = CreateInMemoryProvider("test", rows);

        ValueIndexEntry[] entries =
        [
            new(DataValue.FromFloat32(30f), ChunkIndex: 0, RowOffsetInChunk: 0),
            new(DataValue.FromFloat32(10f), ChunkIndex: 0, RowOffsetInChunk: 1),
            new(DataValue.FromFloat32(40f), ChunkIndex: 0, RowOffsetInChunk: 2),
            new(DataValue.FromFloat32(20f), ChunkIndex: 0, RowOffsetInChunk: 3),
            new(DataValue.FromFloat32(50f), ChunkIndex: 0, RowOffsetInChunk: 4),
        ];

        IColumnIndex columnIndex = BuildBPlusTreeColumnIndex("value", entries, DataKind.Float32);
        IReadOnlyList<IndexChunk> chunks = [CreateChunk(0, rows.Length)];

        IndexScanOperator indexScan = new(
            provider, requiredColumns: null, columnIndex, chunks, descending: false, "value");

        TableCatalog catalog = CreateCatalog();
        catalog.Add(provider);

        ExecutionContext context = CreateExecutionContext(catalog: catalog);

        List<Row> results = await indexScan.CollectRowsAsync(context);

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
        InMemoryTableProvider provider = CreateInMemoryProvider("test", rows);

        ValueIndexEntry[] entries =
        [
            new(DataValue.FromFloat32(30f), ChunkIndex: 0, RowOffsetInChunk: 0),
            new(DataValue.FromFloat32(10f), ChunkIndex: 0, RowOffsetInChunk: 1),
            new(DataValue.FromFloat32(40f), ChunkIndex: 0, RowOffsetInChunk: 2),
            new(DataValue.FromFloat32(20f), ChunkIndex: 0, RowOffsetInChunk: 3),
            new(DataValue.FromFloat32(50f), ChunkIndex: 0, RowOffsetInChunk: 4),
        ];

        IColumnIndex columnIndex = BuildBPlusTreeColumnIndex("value", entries, DataKind.Float32);
        IReadOnlyList<IndexChunk> chunks = [CreateChunk(0, rows.Length)];

        IndexScanOperator indexScan = new(
            provider, requiredColumns: null, columnIndex, chunks, descending: true, "value");

        TableCatalog catalog = CreateCatalog();
        catalog.Add(provider);

        ExecutionContext context = CreateExecutionContext(catalog: catalog);

        List<Row> results = await indexScan.CollectRowsAsync(context);

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
        InMemoryTableProvider provider = CreateInMemoryProvider("test", rows);

        // Sorted index: 10(0,2), 20(1,2), 30(0,1), 40(1,1), 50(0,0), 60(1,0)
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromFloat32(50f), ChunkIndex: 0, RowOffsetInChunk: 0),
            new(DataValue.FromFloat32(30f), ChunkIndex: 0, RowOffsetInChunk: 1),
            new(DataValue.FromFloat32(10f), ChunkIndex: 0, RowOffsetInChunk: 2),
            new(DataValue.FromFloat32(60f), ChunkIndex: 1, RowOffsetInChunk: 0),
            new(DataValue.FromFloat32(40f), ChunkIndex: 1, RowOffsetInChunk: 1),
            new(DataValue.FromFloat32(20f), ChunkIndex: 1, RowOffsetInChunk: 2),
        ];

        IColumnIndex columnIndex = BuildBPlusTreeColumnIndex("value", entries, DataKind.Float32);
        IReadOnlyList<IndexChunk> chunks =
        [
            CreateChunk(0, 3),
            CreateChunk(3, 3),
        ];

        IndexScanOperator indexScan = new(
            provider, requiredColumns: null, columnIndex, chunks, descending: false, "value");

        TableCatalog catalog = CreateCatalog();
        catalog.Add(provider);

        ExecutionContext context = CreateExecutionContext(catalog: catalog);

        List<Row> results = await indexScan.CollectRowsAsync(context);

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
        InMemoryTableProvider provider = CreateInMemoryProvider("data", rows);
        provider.ProvideSourceIndex(BuildSourceIndexWithBPlusTree("value", rows, DataKind.Float32));

        TableCatalog catalog = CreateCatalog();
        catalog.Add(provider);

        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("data")),
            OrderBy: new OrderByClause(
            [
                new OrderByItem(new ColumnReference("value"), SortDirection.Ascending),
            ]));

        IQueryOperator plan = planner.Plan(statement);

        // The plan should be an IndexScanOperator, not OrderByOperator over ScanOperator.
        Assert.IsType<IndexScanOperator>(plan);
    }

    [Fact]
    public void Plan_OrderByWithoutSortedIndex_FallsBackToOrderByOperator()
    {
        Row[] rows = CreateNumberedRows(3f, 1f, 2f);
        InMemoryTableProvider provider = CreateInMemoryProvider("data", rows);

        // Provide a SourceIndex with no column index — planner can't substitute.
        SourceIndex emptyIndex = new(
            new SourceFingerprint(100, DummyHash),
            new IndexSchema(SchemaFromRows(rows), rows.Length),
            [CreateChunk(0, rows.Length)]);
        provider.ProvideSourceIndex(emptyIndex);

        TableCatalog catalog = CreateCatalog();
        catalog.Add(provider);

        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("data")),
            OrderBy: new OrderByClause(
            [
                new OrderByItem(new ColumnReference("value"), SortDirection.Ascending),
            ]));

        IQueryOperator plan = planner.Plan(statement);

        // No column index → must fall back to OrderByOperator.
        Assert.IsType<OrderByOperator>(plan);
    }

    [Fact]
    public void Plan_OrderByMultipleColumns_FallsBackToOrderByOperator()
    {
        Row[] rows = CreateNumberedRows(3f, 1f, 2f);
        InMemoryTableProvider provider = CreateInMemoryProvider("data", rows);
        provider.ProvideSourceIndex(BuildSourceIndexWithBPlusTree("value", rows, DataKind.Float32));

        TableCatalog catalog = CreateCatalog();
        catalog.Add(provider);

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
        InMemoryTableProvider provider = CreateInMemoryProvider("data", rows);
        provider.ProvideSourceIndex(BuildSourceIndexWithBPlusTree("value", rows, DataKind.Float32));

        TableCatalog catalog = CreateCatalog();
        catalog.Add(provider);

        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("data")),
            OrderBy: new OrderByClause(
            [
                new OrderByItem(new ColumnReference("value"), SortDirection.Ascending),
            ]));

        IQueryOperator plan = planner.Plan(statement);

        ExecutionContext context = CreateExecutionContext(catalog: catalog);
        List<Row> results = await plan.CollectRowsAsync(context);

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
        InMemoryTableProvider provider = CreateInMemoryProvider("data", rows);
        provider.ProvideSourceIndex(BuildSourceIndexWithBPlusTree("value", rows, DataKind.Float32));

        TableCatalog catalog = CreateCatalog();
        catalog.Add(provider);

        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("data")),
            OrderBy: new OrderByClause(
            [
                new OrderByItem(new ColumnReference("value"), SortDirection.Descending),
            ]));

        IQueryOperator plan = planner.Plan(statement);

        ExecutionContext context = CreateExecutionContext(catalog: catalog);
        List<Row> results = await plan.CollectRowsAsync(context);

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
        InMemoryTableProvider provider = CreateInMemoryProvider("data", rows);
        provider.ProvideSourceIndex(BuildSourceIndexWithBPlusTree("value", rows, DataKind.Float32));

        TableCatalog catalog = CreateCatalog();
        catalog.Add(provider);

        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("data")),
            OrderBy: new OrderByClause(
            [
                new OrderByItem(new ColumnReference("value"), SortDirection.Ascending),
            ]),
            Limit: new LiteralExpression(3L));

        IQueryOperator plan = planner.Plan(statement);

        ExecutionContext context = CreateExecutionContext(catalog: catalog);
        List<Row> results = await plan.CollectRowsAsync(context);

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
        Row[] rows = CreateNumberedRows(0f, 1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f);
        InMemoryTableProvider provider = CreateInMemoryProvider("data", rows);
        provider.ProvideSourceIndex(BuildSourceIndexWithBPlusTree("value", rows, DataKind.Float32));

        (ScanOperator scan, TableCatalog catalog) = CreateScan(provider,
            new BinaryExpression(
                new ColumnReference("value"),
                BinaryOperator.Equal,
                new LiteralExpression(5.0)));

        FilterOperator filter = new(scan, new BinaryExpression(
            new ColumnReference("value"),
            BinaryOperator.Equal,
            new LiteralExpression(5.0)));

        ExecutionContext context = CreateExecutionContext(catalog: catalog);
        List<Row> results = await filter.CollectRowsAsync(context);

        Assert.Single(results);
        Assert.Equal(5f, results[0]["value"].AsFloat32());
    }

    [Fact]
    public async Task Scan_WhereEqualityWithSortedIndex_MultipleMatches()
    {
        // Duplicate values: [1, 2, 3, 2, 1]. WHERE value = 2 yields 2 rows.
        Row[] rows = CreateNumberedRows(1f, 2f, 3f, 2f, 1f);
        InMemoryTableProvider provider = CreateInMemoryProvider("data", rows);
        provider.ProvideSourceIndex(BuildSourceIndexWithBPlusTree("value", rows, DataKind.Float32));

        (ScanOperator scan, TableCatalog catalog) = CreateScan(provider,
            new BinaryExpression(
                new ColumnReference("value"),
                BinaryOperator.Equal,
                new LiteralExpression(2.0)));

        FilterOperator filter = new(scan, new BinaryExpression(
            new ColumnReference("value"),
            BinaryOperator.Equal,
            new LiteralExpression(2.0)));

        ExecutionContext context = CreateExecutionContext(catalog: catalog);
        List<Row> results = await filter.CollectRowsAsync(context);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(2f, r["value"].AsFloat32()));
    }

    [Fact]
    public async Task Scan_WhereEqualityWithSortedIndex_NoMatches()
    {
        Row[] rows = CreateNumberedRows(1f, 2f, 3f);
        InMemoryTableProvider provider = CreateInMemoryProvider("data", rows);
        provider.ProvideSourceIndex(BuildSourceIndexWithBPlusTree("value", rows, DataKind.Float32));

        (ScanOperator scan, TableCatalog catalog) = CreateScan(provider,
            new BinaryExpression(
                new ColumnReference("value"),
                BinaryOperator.Equal,
                new LiteralExpression(99.0)));

        FilterOperator filter = new(scan, new BinaryExpression(
            new ColumnReference("value"),
            BinaryOperator.Equal,
            new LiteralExpression(99.0)));

        ExecutionContext context = CreateExecutionContext(catalog: catalog);
        List<Row> results = await filter.CollectRowsAsync(context);

        Assert.Empty(results);
    }

    [Fact]
    public async Task Scan_WhereCompoundAndWithSortedIndex_SeeksAndFilters()
    {
        // WHERE value = 2 AND index > 1 → index seek on value, filter on index.
        Row[] rows =
        [
            MakeRow(["index", "value"], DataValue.FromFloat32(0f), DataValue.FromFloat32(2f)),
            MakeRow(["index", "value"], DataValue.FromFloat32(1f), DataValue.FromFloat32(2f)),
            MakeRow(["index", "value"], DataValue.FromFloat32(2f), DataValue.FromFloat32(2f)),
            MakeRow(["index", "value"], DataValue.FromFloat32(3f), DataValue.FromFloat32(3f)),
        ];
        InMemoryTableProvider provider = CreateInMemoryProvider("data", rows);
        provider.ProvideSourceIndex(BuildSourceIndexWithBPlusTree("value", rows, DataKind.Float32));

        Expression predicate = new BinaryExpression(
            new BinaryExpression(
                new ColumnReference("value"),
                BinaryOperator.Equal,
                new LiteralExpression(2.0)),
            BinaryOperator.And,
            new BinaryExpression(
                new ColumnReference("index"),
                BinaryOperator.GreaterThan,
                new LiteralExpression(1.0)));

        (ScanOperator scan, TableCatalog catalog) = CreateScan(provider, predicate);
        FilterOperator filter = new(scan, predicate);

        ExecutionContext context = CreateExecutionContext(catalog: catalog);
        List<Row> results = await filter.CollectRowsAsync(context);

        // value=2 matches rows 0,1,2; index>1 further filters to row 2.
        Assert.Single(results);
        Assert.Equal(2f, results[0]["index"].AsFloat32());
    }

    [Fact]
    public async Task Scan_WhereOrWithSortedIndex_DoesNotUseIndexSeek()
    {
        // OR predicates cannot use index seek — must scan all.
        Row[] rows = CreateNumberedRows(1f, 2f, 3f, 4f, 5f);
        InMemoryTableProvider provider = CreateInMemoryProvider("data", rows);
        provider.ProvideSourceIndex(BuildSourceIndexWithBPlusTree("value", rows, DataKind.Float32));

        Expression predicate = new BinaryExpression(
            new BinaryExpression(
                new ColumnReference("value"),
                BinaryOperator.Equal,
                new LiteralExpression(2.0)),
            BinaryOperator.Or,
            new BinaryExpression(
                new ColumnReference("value"),
                BinaryOperator.Equal,
                new LiteralExpression(4.0)));

        (ScanOperator scan, TableCatalog catalog) = CreateScan(provider, predicate);
        FilterOperator filter = new(scan, predicate);

        ExecutionContext context = CreateExecutionContext(catalog: catalog);
        List<Row> results = await filter.CollectRowsAsync(context);

        // Should still produce correct results via normal scan + filter.
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task Scan_WhereEqualityWithSortedIndex_ReportsExactSeekRowsFetched()
    {
        // Verify the exact seek path is used by checking the ScanOperator metric.
        Row[] rows = CreateNumberedRows(0f, 1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f);
        InMemoryTableProvider provider = CreateInMemoryProvider("data", rows);
        provider.ProvideSourceIndex(BuildSourceIndexWithBPlusTree("value", rows, DataKind.Float32));

        Expression predicate = new BinaryExpression(
            new ColumnReference("value"),
            BinaryOperator.Equal,
            new LiteralExpression(5.0));

        (ScanOperator scan, TableCatalog catalog) = CreateScan(provider, predicate);
        FilterOperator filter = new(scan, predicate);

        ExecutionContext context = CreateExecutionContext(catalog: catalog);
        List<Row> results = await filter.CollectRowsAsync(context);

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
        InMemoryTableProvider provider = CreateInMemoryProvider("data", rows);
        provider.ProvideSourceIndex(BuildMultiChunkSourceIndexWithBPlusTree(
            "value", rows, chunkSize: 5, DataKind.Float32));

        Expression predicate = new BinaryExpression(
            new ColumnReference("value"),
            BinaryOperator.LessThan,
            new LiteralExpression(6.0));

        (ScanOperator scan, TableCatalog catalog) = CreateScan(provider, predicate);
        FilterOperator filter = new(scan, predicate);

        ExecutionContext context = CreateExecutionContext(catalog: catalog);
        List<Row> results = await filter.CollectRowsAsync(context);

        Assert.Equal(5, results.Count);
        Assert.Equal(1, scan.PrunedIndexChunks);
    }

    [Fact]
    public async Task Scan_WhereGreaterThanWithSortedIndex_PrunesChunks()
    {
        Row[] rows = CreateNumberedRows(1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f);
        InMemoryTableProvider provider = CreateInMemoryProvider("data", rows);
        provider.ProvideSourceIndex(BuildMultiChunkSourceIndexWithBPlusTree(
            "value", rows, chunkSize: 5, DataKind.Float32));

        Expression predicate = new BinaryExpression(
            new ColumnReference("value"),
            BinaryOperator.GreaterThan,
            new LiteralExpression(5.0));

        (ScanOperator scan, TableCatalog catalog) = CreateScan(provider, predicate);
        FilterOperator filter = new(scan, predicate);

        ExecutionContext context = CreateExecutionContext(catalog: catalog);
        List<Row> results = await filter.CollectRowsAsync(context);

        Assert.Equal(5, results.Count);
        Assert.Equal(1, scan.PrunedIndexChunks);
    }

    [Fact]
    public async Task Scan_WhereLessThanOrEqualWithSortedIndex_PrunesChunks()
    {
        // WHERE value <= 5 → chunk 1 (values 6-10) should be pruned.
        Row[] rows = CreateNumberedRows(1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f);
        InMemoryTableProvider provider = CreateInMemoryProvider("data", rows);
        provider.ProvideSourceIndex(BuildMultiChunkSourceIndexWithBPlusTree(
            "value", rows, chunkSize: 5, DataKind.Float32));

        Expression predicate = new BinaryExpression(
            new ColumnReference("value"),
            BinaryOperator.LessThanOrEqual,
            new LiteralExpression(5.0));

        (ScanOperator scan, TableCatalog catalog) = CreateScan(provider, predicate);
        FilterOperator filter = new(scan, predicate);

        ExecutionContext context = CreateExecutionContext(catalog: catalog);
        List<Row> results = await filter.CollectRowsAsync(context);

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
        InMemoryTableProvider provider = CreateInMemoryProvider("data", rows);
        provider.ProvideSourceIndex(BuildMultiChunkSourceIndexWithBPlusTree(
            "value", rows, chunkSize: 5, DataKind.Float32));

        Expression predicate = new BetweenExpression(
            new ColumnReference("value"),
            new LiteralExpression(6.0),
            new LiteralExpression(10.0));

        (ScanOperator scan, TableCatalog catalog) = CreateScan(provider, predicate);
        FilterOperator filter = new(scan, predicate);

        ExecutionContext context = CreateExecutionContext(catalog: catalog);
        List<Row> results = await filter.CollectRowsAsync(context);

        Assert.Equal(5, results.Count);
        Assert.Equal(2, scan.PrunedIndexChunks);
    }

    [Fact]
    public async Task Scan_WhereInWithSortedIndex_PrunesChunks()
    {
        // 10 rows in 2 chunks of 5. WHERE value IN (1, 3) → only chunk 0 matches.
        Row[] rows = CreateNumberedRows(1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f);
        InMemoryTableProvider provider = CreateInMemoryProvider("data", rows);
        provider.ProvideSourceIndex(BuildMultiChunkSourceIndexWithBPlusTree(
            "value", rows, chunkSize: 5, DataKind.Float32));

        Expression predicate = new InExpression(
            new ColumnReference("value"),
            [new LiteralExpression(1.0), new LiteralExpression(3.0)],
            Negated: false);

        (ScanOperator scan, TableCatalog catalog) = CreateScan(provider, predicate);
        FilterOperator filter = new(scan, predicate);

        ExecutionContext context = CreateExecutionContext(catalog: catalog);
        List<Row> results = await filter.CollectRowsAsync(context);

        Assert.Equal(2, results.Count);
        Assert.Equal(1, scan.PrunedIndexChunks);
    }

    // ───────────────────── Range predicate row seek tests ─────────────────────

    [Fact]
    public async Task Scan_WhereBetweenWithSortedIndex_SeeksToMatchingRows()
    {
        // Single chunk with 10 rows. BETWEEN 3 AND 7 should seek to 5 rows.
        Row[] rows = CreateNumberedRows(1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f);
        InMemoryTableProvider provider = CreateInMemoryProvider("data", rows);
        provider.ProvideSourceIndex(BuildSourceIndexWithBPlusTree("value", rows, DataKind.Float32));

        Expression predicate = new BetweenExpression(
            new ColumnReference("value"),
            new LiteralExpression(3.0),
            new LiteralExpression(7.0));

        (ScanOperator scan, TableCatalog catalog) = CreateScan(provider, predicate);
        FilterOperator filter = new(scan, predicate);

        ExecutionContext context = CreateExecutionContext(catalog: catalog);
        List<Row> results = await filter.CollectRowsAsync(context);

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
        InMemoryTableProvider provider = CreateInMemoryProvider("data", rows);
        provider.ProvideSourceIndex(BuildSourceIndexWithBPlusTree("value", rows, DataKind.Float32));

        Expression predicate = new InExpression(
            new ColumnReference("value"),
            [new LiteralExpression(2.0), new LiteralExpression(5.0), new LiteralExpression(8.0)],
            Negated: false);

        (ScanOperator scan, TableCatalog catalog) = CreateScan(provider, predicate);
        FilterOperator filter = new(scan, predicate);

        ExecutionContext context = CreateExecutionContext(catalog: catalog);
        List<Row> results = await filter.CollectRowsAsync(context);

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
        InMemoryTableProvider provider = CreateInMemoryProvider("data", rows);
        provider.ProvideSourceIndex(BuildMultiChunkSourceIndexWithBPlusTree(
            "value", rows, chunkSize: 5, DataKind.Float32));

        Expression predicate = new BinaryExpression(
            new LiteralExpression(5.0),
            BinaryOperator.GreaterThan,
            new ColumnReference("value"));

        (ScanOperator scan, TableCatalog catalog) = CreateScan(provider, predicate);
        FilterOperator filter = new(scan, predicate);

        ExecutionContext context = CreateExecutionContext(catalog: catalog);
        List<Row> results = await filter.CollectRowsAsync(context);

        // value < 5 → rows with values 1,2,3,4 from chunk 0.
        Assert.Equal(4, results.Count);
        Assert.Equal(1, scan.PrunedIndexChunks);
    }

    // ───────────────────── Helpers ─────────────────────

    private (ScanOperator Scan, TableCatalog Catalog) CreateScan(
        InMemoryTableProvider provider, Expression filterHint)
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(provider);

        long rowCount = provider.GetSourceIndex()?.Schema.TotalRowCount ?? 0;
        ScanOperator scan = new(provider, requiredColumns: null, rowCount);
        scan.AddFilterHint(filterHint);
        return (scan, catalog);
    }

    private static IndexChunk CreateChunk(long rowOffset, long rowCount)
    {
        return new IndexChunk(rowOffset, rowCount, new Dictionary<string, ChunkColumnStatistics>());
    }

    /// <summary>
    /// Creates rows with "index" (sequential) and "value" columns.
    /// </summary>
    private static Row[] CreateNumberedRows(params float[] values)
    {
        Row[] rows = new Row[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            rows[i] = MakeRow(
                ["index", "value"],
                DataValue.FromFloat32(i),
                DataValue.FromFloat32(values[i]));
        }

        return rows;
    }

    private static Schema SchemaFromRows(Row[] rows)
    {
        Row first = rows[0];
        ColumnInfo[] columns = new ColumnInfo[first.FieldCount];
        for (int i = 0; i < first.FieldCount; i++)
        {
            columns[i] = new ColumnInfo(first.ColumnNames[i], first[i].Kind, false);
        }

        return new Schema(columns);
    }

    /// <summary>
    /// Builds a single-column <see cref="IColumnIndex"/> backed by a B+Tree from
    /// the given entries. Entries are sorted before bulk-loading.
    /// </summary>
    private static IColumnIndex BuildBPlusTreeColumnIndex(
        string columnName, ValueIndexEntry[] entries, DataKind keyKind)
    {
        BPlusTreeIndexSet indexSet = BuildBPlusTreeIndexSet(columnName, entries, keyKind);
        if (!indexSet.TryGetIndex(columnName, out BPlusTreeColumnIndex? index))
        {
            throw new InvalidOperationException($"Column index for '{columnName}' was not built.");
        }

        return index;
    }

    private static BPlusTreeIndexSet BuildBPlusTreeIndexSet(
        string columnName, ValueIndexEntry[] entries, DataKind keyKind)
    {
        ValueIndexEntry[] sorted = (ValueIndexEntry[])entries.Clone();
        Array.Sort(sorted, (a, b) => StatisticsPredicateEvaluator.CompareValues(a.Key, b.Key));

        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);

        BPlusTreeSectionHeader? header = BPlusTreeBulkLoader.Build(
            sorted, columnName, keyKind, writer);

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

    /// <summary>
    /// Builds a single-chunk <see cref="SourceIndex"/> with a B+Tree index on the
    /// given column. Entries are derived from the rows in source order.
    /// </summary>
    private static SourceIndex BuildSourceIndexWithBPlusTree(
        string columnName, Row[] rows, DataKind keyKind)
    {
        ValueIndexEntry[] entries = new ValueIndexEntry[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            entries[i] = new ValueIndexEntry(
                rows[i][columnName], ChunkIndex: 0, RowOffsetInChunk: i);
        }

        BPlusTreeIndexSet bTreeSet = BuildBPlusTreeIndexSet(columnName, entries, keyKind);

        return new SourceIndex(
            new SourceFingerprint(100, DummyHash),
            new IndexSchema(SchemaFromRows(rows), rows.Length),
            [CreateChunk(0, rows.Length)],
            bloomFilters: null,
            bPlusTreeIndexes: bTreeSet);
    }

    /// <summary>
    /// Builds a multi-chunk <see cref="SourceIndex"/> partitioned at
    /// <paramref name="chunkSize"/>-row boundaries, with a single B+Tree index
    /// covering all rows. Per-chunk statistics are populated so range predicates
    /// can prune chunks via <see cref="ChunkColumnStatistics"/>.
    /// </summary>
    private static SourceIndex BuildMultiChunkSourceIndexWithBPlusTree(
        string columnName, Row[] rows, int chunkSize, DataKind keyKind)
    {
        int chunkCount = (rows.Length + chunkSize - 1) / chunkSize;
        List<IndexChunk> chunks = new();
        List<ValueIndexEntry> entries = new();

        for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            int start = chunkIndex * chunkSize;
            int count = Math.Min(chunkSize, rows.Length - start);

            // Collect statistics for this chunk for range pruning.
            DataValue minValue = rows[start][columnName];
            DataValue maxValue = minValue;
            for (int row = 1; row < count; row++)
            {
                DataValue v = rows[start + row][columnName];
                if (StatisticsPredicateEvaluator.CompareValues(v, minValue) < 0) minValue = v;
                if (StatisticsPredicateEvaluator.CompareValues(v, maxValue) > 0) maxValue = v;
            }

            Dictionary<string, ChunkColumnStatistics> stats = new(StringComparer.OrdinalIgnoreCase)
            {
                [columnName] = new ChunkColumnStatistics(
                    Minimum: minValue,
                    Maximum: maxValue,
                    NullCount: 0,
                    RowCount: count,
                    EstimatedCardinality: count),
            };
            chunks.Add(new IndexChunk(start, count, stats));

            for (int row = 0; row < count; row++)
            {
                entries.Add(new ValueIndexEntry(
                    rows[start + row][columnName], chunkIndex, row));
            }
        }

        BPlusTreeIndexSet bTreeSet = BuildBPlusTreeIndexSet(
            columnName, entries.ToArray(), keyKind);

        return new SourceIndex(
            new SourceFingerprint(100, DummyHash),
            new IndexSchema(SchemaFromRows(rows), rows.Length),
            chunks,
            bloomFilters: null,
            bPlusTreeIndexes: bTreeSet);
    }
}
