using System.Runtime.CompilerServices;
using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Indexing;
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
            new(DataValue.FromScalar(10f), ChunkIndex: 0, RowOffsetInChunk: 1),
            new(DataValue.FromScalar(20f), ChunkIndex: 0, RowOffsetInChunk: 3),
            new(DataValue.FromScalar(30f), ChunkIndex: 0, RowOffsetInChunk: 0),
            new(DataValue.FromScalar(40f), ChunkIndex: 0, RowOffsetInChunk: 2),
            new(DataValue.FromScalar(50f), ChunkIndex: 0, RowOffsetInChunk: 4),
        ];

        SortedValueIndex sortedIndex = new(entries);
        IReadOnlyList<IndexChunk> chunks = [CreateChunk(rowOffset: 0, rowCount: 5)];

        TableDescriptor descriptor = CreateDescriptor("test");
        IndexScanOperator indexScan = new(descriptor, null, sortedIndex, chunks, descending: false);

        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        catalog.Register(descriptor);

        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog);

        List<Row> results = await CollectRowsAsync(indexScan.ExecuteAsync(context));

        Assert.Equal(5, results.Count);
        Assert.Equal(10f, results[0]["value"].AsScalar());
        Assert.Equal(20f, results[1]["value"].AsScalar());
        Assert.Equal(30f, results[2]["value"].AsScalar());
        Assert.Equal(40f, results[3]["value"].AsScalar());
        Assert.Equal(50f, results[4]["value"].AsScalar());
    }

    [Fact]
    public async Task IndexScan_Descending_YieldsRowsInReverseSortedOrder()
    {
        Row[] rows = CreateNumberedRows(30f, 10f, 40f, 20f, 50f);
        SeekableInMemoryProvider provider = new(rows);

        ValueIndexEntry[] entries =
        [
            new(DataValue.FromScalar(10f), ChunkIndex: 0, RowOffsetInChunk: 1),
            new(DataValue.FromScalar(20f), ChunkIndex: 0, RowOffsetInChunk: 3),
            new(DataValue.FromScalar(30f), ChunkIndex: 0, RowOffsetInChunk: 0),
            new(DataValue.FromScalar(40f), ChunkIndex: 0, RowOffsetInChunk: 2),
            new(DataValue.FromScalar(50f), ChunkIndex: 0, RowOffsetInChunk: 4),
        ];

        SortedValueIndex sortedIndex = new(entries);
        IReadOnlyList<IndexChunk> chunks = [CreateChunk(rowOffset: 0, rowCount: 5)];

        TableDescriptor descriptor = CreateDescriptor("test");
        IndexScanOperator indexScan = new(descriptor, null, sortedIndex, chunks, descending: true);

        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        catalog.Register(descriptor);

        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog);

        List<Row> results = await CollectRowsAsync(indexScan.ExecuteAsync(context));

        Assert.Equal(5, results.Count);
        Assert.Equal(50f, results[0]["value"].AsScalar());
        Assert.Equal(40f, results[1]["value"].AsScalar());
        Assert.Equal(30f, results[2]["value"].AsScalar());
        Assert.Equal(20f, results[3]["value"].AsScalar());
        Assert.Equal(10f, results[4]["value"].AsScalar());
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
            new(DataValue.FromScalar(10f), ChunkIndex: 0, RowOffsetInChunk: 2),
            new(DataValue.FromScalar(20f), ChunkIndex: 1, RowOffsetInChunk: 2),
            new(DataValue.FromScalar(30f), ChunkIndex: 0, RowOffsetInChunk: 1),
            new(DataValue.FromScalar(40f), ChunkIndex: 1, RowOffsetInChunk: 1),
            new(DataValue.FromScalar(50f), ChunkIndex: 0, RowOffsetInChunk: 0),
            new(DataValue.FromScalar(60f), ChunkIndex: 1, RowOffsetInChunk: 0),
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

        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog);

        List<Row> results = await CollectRowsAsync(indexScan.ExecuteAsync(context));

        Assert.Equal(6, results.Count);
        Assert.Equal(10f, results[0]["value"].AsScalar());
        Assert.Equal(20f, results[1]["value"].AsScalar());
        Assert.Equal(30f, results[2]["value"].AsScalar());
        Assert.Equal(40f, results[3]["value"].AsScalar());
        Assert.Equal(50f, results[4]["value"].AsScalar());
        Assert.Equal(60f, results[5]["value"].AsScalar());
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
            new IndexSchema(new Schema([new ColumnInfo("value", DataKind.Scalar, false)]), 3),
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

        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog);
        List<Row> results = await CollectRowsAsync(plan.ExecuteAsync(context));

        Assert.Equal(5, results.Count);
        Assert.Equal(10f, results[0]["value"].AsScalar());
        Assert.Equal(20f, results[1]["value"].AsScalar());
        Assert.Equal(30f, results[2]["value"].AsScalar());
        Assert.Equal(40f, results[3]["value"].AsScalar());
        Assert.Equal(50f, results[4]["value"].AsScalar());
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

        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog);
        List<Row> results = await CollectRowsAsync(plan.ExecuteAsync(context));

        Assert.Equal(5, results.Count);
        Assert.Equal(50f, results[0]["value"].AsScalar());
        Assert.Equal(40f, results[1]["value"].AsScalar());
        Assert.Equal(30f, results[2]["value"].AsScalar());
        Assert.Equal(20f, results[3]["value"].AsScalar());
        Assert.Equal(10f, results[4]["value"].AsScalar());
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

        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog);
        List<Row> results = await CollectRowsAsync(plan.ExecuteAsync(context));

        Assert.Equal(3, results.Count);
        Assert.Equal(10f, results[0]["value"].AsScalar());
        Assert.Equal(20f, results[1]["value"].AsScalar());
        Assert.Equal(30f, results[2]["value"].AsScalar());
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
                [DataValue.FromScalar(i), DataValue.FromScalar(values[i])]);
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
                    new ColumnInfo("index", DataKind.Scalar, false),
                    new ColumnInfo("value", DataKind.Scalar, false),
                ]),
                rows.Length),
            [chunk],
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
                .Select(name => new ColumnInfo(name, DataKind.Scalar, false))
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
