using System.Runtime.CompilerServices;
using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Indexing;
using DatumIngest.Indexing.Bitmap;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Indexing;

/// <summary>
/// Tests for bitmap-index-based chunk pruning and row-level filtering
/// in <see cref="ScanOperator"/>. Verifies that equality predicates
/// on bitmap-indexed columns eliminate chunks and individual rows.
/// </summary>
public sealed class BitmapPruningTests
{
    private static readonly FunctionRegistry DefaultFunctions = FunctionRegistry.CreateDefault();
    private static readonly byte[] DummyHash = new byte[32];

    // ───────────────────── Chunk-level pruning ─────────────────────

    [Fact]
    public async Task BitmapPruning_EqualityOnSingleChunk_PrunesAbsentValue()
    {
        // 5 rows in 1 chunk: color = [red, red, blue, blue, red].
        // WHERE color = 'green' → value absent from chunk → prune entire chunk.
        Row[] rows = CreateStringRows("color", "red", "red", "blue", "blue", "red");
        InMemoryProvider provider = new(rows);
        BitmapIndexSet bitmapIndexes = BuildBitmapIndex("color", rows, chunkSize: 5);

        SourceIndex sourceIndex = CreateSourceIndexWithBitmaps(rows, bitmapIndexes, chunkSize: 5);

        (ScanOperator scan, TableCatalog catalog) = CreateScanWithBitmaps(provider, sourceIndex,
            new BinaryExpression(
                new ColumnReference("color"),
                BinaryOperator.Equal,
                new LiteralExpression("green")));

        List<Row> results = await ExecuteScanAsync(scan, catalog);

        Assert.Empty(results);
        Assert.Equal(1, scan.TotalIndexChunks);
        Assert.Equal(1, scan.PrunedIndexChunks);
    }

    [Fact]
    public async Task BitmapPruning_EqualityOnMultipleChunks_PrunesCorrectChunks()
    {
        // 6 rows in 2 chunks of 3:
        // Chunk 0: [red, blue, red]   Chunk 1: [green, green, green]
        // WHERE color = 'red' → chunk 1 has no 'red' → pruned.
        Row[] rows = CreateStringRows("color", "red", "blue", "red", "green", "green", "green");
        InMemoryProvider provider = new(rows);
        BitmapIndexSet bitmapIndexes = BuildBitmapIndex("color", rows, chunkSize: 3);

        SourceIndex sourceIndex = CreateSourceIndexWithBitmaps(rows, bitmapIndexes, chunkSize: 3);

        (ScanOperator scan, TableCatalog catalog) = CreateScanWithBitmaps(provider, sourceIndex,
            new BinaryExpression(
                new ColumnReference("color"),
                BinaryOperator.Equal,
                new LiteralExpression("red")));

        List<Row> results = await ExecuteScanAsync(scan, catalog);

        // Chunk 0 survives pruning, and row-level bitmap filtering removes 'blue'.
        Assert.Equal(2, results.Count);
        Assert.All(results, row => Assert.Equal("red", row["color"].AsString()));
        Assert.Equal(2, scan.TotalIndexChunks);
        Assert.Equal(1, scan.PrunedIndexChunks);
    }

    [Fact]
    public async Task BitmapPruning_AndPredicate_BothColumnsIndexed()
    {
        // 6 rows in 2 chunks of 3:
        // Chunk 0: color=[red, red, blue],    size=[S, M, S]
        // Chunk 1: color=[green, green, red],  size=[M, L, L]
        // WHERE color = 'blue' AND size = 'L'
        // color='blue': present in chunk 0, absent in chunk 1 → prune chunk 1
        // size='L': absent from chunk 0, present in chunk 1 → prune chunk 0
        // Both chunks pruned → empty result.
        Row[] rows =
        [
            new(["color", "size"], [DataValue.FromString("red"),   DataValue.FromString("S")]),
            new(["color", "size"], [DataValue.FromString("red"),   DataValue.FromString("M")]),
            new(["color", "size"], [DataValue.FromString("blue"),  DataValue.FromString("S")]),
            new(["color", "size"], [DataValue.FromString("green"), DataValue.FromString("M")]),
            new(["color", "size"], [DataValue.FromString("green"), DataValue.FromString("L")]),
            new(["color", "size"], [DataValue.FromString("red"),   DataValue.FromString("L")]),
        ];

        InMemoryProvider provider = new(rows);
        BitmapIndexSet bitmapIndexes = BuildMultiColumnBitmapIndex(
            ["color", "size"], rows, chunkSize: 3);

        SourceIndex sourceIndex = CreateSourceIndexWithBitmaps(rows, bitmapIndexes, chunkSize: 3);

        (ScanOperator scan, TableCatalog catalog) = CreateScanWithBitmaps(provider, sourceIndex,
            new BinaryExpression(
                new BinaryExpression(
                    new ColumnReference("color"),
                    BinaryOperator.Equal,
                    new LiteralExpression("blue")),
                BinaryOperator.And,
                new BinaryExpression(
                    new ColumnReference("size"),
                    BinaryOperator.Equal,
                    new LiteralExpression("L"))));

        List<Row> results = await ExecuteScanAsync(scan, catalog);

        Assert.Empty(results);
        Assert.Equal(2, scan.TotalIndexChunks);
        Assert.Equal(2, scan.PrunedIndexChunks);
    }

    [Fact]
    public async Task BitmapPruning_InPredicate_PrunesChunksMissingAllValues()
    {
        // 6 rows in 2 chunks of 3:
        // Chunk 0: [red, blue, red]   Chunk 1: [green, green, green]
        // WHERE color IN ('red', 'blue') → chunk 1 has neither → pruned.
        Row[] rows = CreateStringRows("color", "red", "blue", "red", "green", "green", "green");
        InMemoryProvider provider = new(rows);
        BitmapIndexSet bitmapIndexes = BuildBitmapIndex("color", rows, chunkSize: 3);

        SourceIndex sourceIndex = CreateSourceIndexWithBitmaps(rows, bitmapIndexes, chunkSize: 3);

        (ScanOperator scan, TableCatalog catalog) = CreateScanWithBitmaps(provider, sourceIndex,
            new InExpression(
                new ColumnReference("color"),
                [new LiteralExpression("red"), new LiteralExpression("blue")],
                Negated: false));

        List<Row> results = await ExecuteScanAsync(scan, catalog);

        Assert.Equal(3, results.Count);
        Assert.Equal(2, scan.TotalIndexChunks);
        Assert.Equal(1, scan.PrunedIndexChunks);
    }

    // ───────────────────── Row-level bitmap filtering ─────────────────────

    [Fact]
    public async Task BitmapRowFilter_EqualityPredicate_FiltersNonMatchingRows()
    {
        // 5 rows in 1 chunk: color = [red, blue, red, green, red].
        // WHERE color = 'red' → bitmap filter yields rows 0, 2, 4.
        Row[] rows = CreateStringRows("color", "red", "blue", "red", "green", "red");
        InMemoryProvider provider = new(rows);
        BitmapIndexSet bitmapIndexes = BuildBitmapIndex("color", rows, chunkSize: 5);

        SourceIndex sourceIndex = CreateSourceIndexWithBitmaps(rows, bitmapIndexes, chunkSize: 5);

        (ScanOperator scan, TableCatalog catalog) = CreateScanWithBitmaps(provider, sourceIndex,
            new BinaryExpression(
                new ColumnReference("color"),
                BinaryOperator.Equal,
                new LiteralExpression("red")));

        List<Row> results = await ExecuteScanAsync(scan, catalog);

        Assert.Equal(3, results.Count);
        Assert.All(results, row => Assert.Equal("red", row["color"].AsString()));
    }

    [Fact]
    public async Task BitmapRowFilter_NotEqualPredicate_FiltersMatchingRows()
    {
        // 5 rows: color = [red, blue, red, green, red].
        // WHERE color != 'red' → bitmap NOT yields rows 1, 3.
        Row[] rows = CreateStringRows("color", "red", "blue", "red", "green", "red");
        InMemoryProvider provider = new(rows);
        BitmapIndexSet bitmapIndexes = BuildBitmapIndex("color", rows, chunkSize: 5);

        SourceIndex sourceIndex = CreateSourceIndexWithBitmaps(rows, bitmapIndexes, chunkSize: 5);

        (ScanOperator scan, TableCatalog catalog) = CreateScanWithBitmaps(provider, sourceIndex,
            new BinaryExpression(
                new ColumnReference("color"),
                BinaryOperator.NotEqual,
                new LiteralExpression("red")));

        List<Row> results = await ExecuteScanAsync(scan, catalog);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, row => row["color"].AsString() == "blue");
        Assert.Contains(results, row => row["color"].AsString() == "green");
    }

    [Fact]
    public async Task BitmapRowFilter_AndAcrossTwoColumns_IntersectsBitmaps()
    {
        // 6 rows: color=[R,R,B,B,R,B], size=[S,M,S,M,S,M]
        // WHERE color='R' AND size='S' → rows 0, 4.
        Row[] rows =
        [
            new(["color", "size"], [DataValue.FromString("R"), DataValue.FromString("S")]),
            new(["color", "size"], [DataValue.FromString("R"), DataValue.FromString("M")]),
            new(["color", "size"], [DataValue.FromString("B"), DataValue.FromString("S")]),
            new(["color", "size"], [DataValue.FromString("B"), DataValue.FromString("M")]),
            new(["color", "size"], [DataValue.FromString("R"), DataValue.FromString("S")]),
            new(["color", "size"], [DataValue.FromString("B"), DataValue.FromString("M")]),
        ];

        InMemoryProvider provider = new(rows);
        BitmapIndexSet bitmapIndexes = BuildMultiColumnBitmapIndex(
            ["color", "size"], rows, chunkSize: 6);

        SourceIndex sourceIndex = CreateSourceIndexWithBitmaps(rows, bitmapIndexes, chunkSize: 6);

        (ScanOperator scan, TableCatalog catalog) = CreateScanWithBitmaps(provider, sourceIndex,
            new BinaryExpression(
                new BinaryExpression(
                    new ColumnReference("color"),
                    BinaryOperator.Equal,
                    new LiteralExpression("R")),
                BinaryOperator.And,
                new BinaryExpression(
                    new ColumnReference("size"),
                    BinaryOperator.Equal,
                    new LiteralExpression("S"))));

        List<Row> results = await ExecuteScanAsync(scan, catalog);

        Assert.Equal(2, results.Count);
        Assert.All(results, row =>
        {
            Assert.Equal("R", row["color"].AsString());
            Assert.Equal("S", row["size"].AsString());
        });
    }

    [Fact]
    public async Task BitmapRowFilter_OrPredicate_UnionsBitmaps()
    {
        // 5 rows: color = [red, blue, green, blue, red].
        // WHERE color = 'red' OR color = 'green' → rows 0, 2, 4.
        Row[] rows = CreateStringRows("color", "red", "blue", "green", "blue", "red");
        InMemoryProvider provider = new(rows);
        BitmapIndexSet bitmapIndexes = BuildBitmapIndex("color", rows, chunkSize: 5);

        SourceIndex sourceIndex = CreateSourceIndexWithBitmaps(rows, bitmapIndexes, chunkSize: 5);

        (ScanOperator scan, TableCatalog catalog) = CreateScanWithBitmaps(provider, sourceIndex,
            new BinaryExpression(
                new BinaryExpression(
                    new ColumnReference("color"),
                    BinaryOperator.Equal,
                    new LiteralExpression("red")),
                BinaryOperator.Or,
                new BinaryExpression(
                    new ColumnReference("color"),
                    BinaryOperator.Equal,
                    new LiteralExpression("green"))));

        List<Row> results = await ExecuteScanAsync(scan, catalog);

        Assert.Equal(3, results.Count);
        Assert.DoesNotContain(results, row => row["color"].AsString() == "blue");
    }

    [Fact]
    public async Task BitmapRowFilter_InPredicate_UnionsValueBitmaps()
    {
        // 5 rows: color = [red, blue, green, blue, red].
        // WHERE color IN ('red', 'green') → rows 0, 2, 4.
        Row[] rows = CreateStringRows("color", "red", "blue", "green", "blue", "red");
        InMemoryProvider provider = new(rows);
        BitmapIndexSet bitmapIndexes = BuildBitmapIndex("color", rows, chunkSize: 5);

        SourceIndex sourceIndex = CreateSourceIndexWithBitmaps(rows, bitmapIndexes, chunkSize: 5);

        (ScanOperator scan, TableCatalog catalog) = CreateScanWithBitmaps(provider, sourceIndex,
            new InExpression(
                new ColumnReference("color"),
                [new LiteralExpression("red"), new LiteralExpression("green")],
                Negated: false));

        List<Row> results = await ExecuteScanAsync(scan, catalog);

        Assert.Equal(3, results.Count);
        Assert.DoesNotContain(results, row => row["color"].AsString() == "blue");
    }

    [Fact]
    public async Task BitmapRowFilter_MultipleChunks_FiltersPerChunk()
    {
        // 6 rows in 2 chunks of 3:
        // Chunk 0: [red, blue, red]   Chunk 1: [red, green, blue]
        // WHERE color = 'red' → chunk 0 rows 0,2; chunk 1 row 0.
        Row[] rows = CreateStringRows("color", "red", "blue", "red", "red", "green", "blue");
        InMemoryProvider provider = new(rows);
        BitmapIndexSet bitmapIndexes = BuildBitmapIndex("color", rows, chunkSize: 3);

        SourceIndex sourceIndex = CreateSourceIndexWithBitmaps(rows, bitmapIndexes, chunkSize: 3);

        (ScanOperator scan, TableCatalog catalog) = CreateScanWithBitmaps(provider, sourceIndex,
            new BinaryExpression(
                new ColumnReference("color"),
                BinaryOperator.Equal,
                new LiteralExpression("red")));

        List<Row> results = await ExecuteScanAsync(scan, catalog);

        Assert.Equal(3, results.Count);
        Assert.All(results, row => Assert.Equal("red", row["color"].AsString()));
    }

    // ───────────────────── Coexistence with sorted index ─────────────────────

    [Fact]
    public async Task BitmapAndSortedCoexist_BothApplyPruning()
    {
        // 6 rows, 2 chunks of 3. Sorted index on "value", bitmap on "color".
        // WHERE value > 10 AND color = 'red'
        // Sorted prunes chunk 0 (values 1..3, all < 10). Bitmap prunes nothing
        // (red appears in both). Net result: chunk 1 rows with color='red'.
        Row[] rows =
        [
            new(["value", "color"], [DataValue.FromFloat32(1f), DataValue.FromString("red")]),
            new(["value", "color"], [DataValue.FromFloat32(2f), DataValue.FromString("blue")]),
            new(["value", "color"], [DataValue.FromFloat32(3f), DataValue.FromString("red")]),
            new(["value", "color"], [DataValue.FromFloat32(11f), DataValue.FromString("blue")]),
            new(["value", "color"], [DataValue.FromFloat32(12f), DataValue.FromString("red")]),
            new(["value", "color"], [DataValue.FromFloat32(13f), DataValue.FromString("red")]),
        ];

        InMemoryProvider provider = new(rows);
        BitmapIndexSet bitmapIndexes = BuildMultiColumnBitmapIndex(["color"], rows, chunkSize: 3);

        // Build sorted index on "value" with 2 chunks.
        List<ValueIndexEntry> entries = new();
        for (int i = 0; i < rows.Length; i++)
        {
            int chunkIndex = i / 3;
            int rowInChunk = i % 3;
            entries.Add(new ValueIndexEntry(rows[i]["value"], chunkIndex, rowInChunk));
        }

        SortedValueIndex sortedIndex = SortedValueIndex.BuildFromUnsorted(entries.ToArray());
        Dictionary<string, SortedValueIndex> sortedDict = new(StringComparer.OrdinalIgnoreCase)
        {
            ["value"] = sortedIndex
        };
        SortedValueIndexSet sortedSet = new(sortedDict);

        List<IndexChunk> chunks = new();
        for (int c = 0; c < 2; c++)
        {
            int start = c * 3;
            Dictionary<string, ChunkColumnStatistics> stats = new(StringComparer.OrdinalIgnoreCase)
            {
                ["value"] = new ChunkColumnStatistics(
                    rows[start]["value"], rows[start + 2]["value"], 0, 3, 3)
            };
            chunks.Add(new IndexChunk(start, 3, -1, -1, stats));
        }

        SourceIndex sourceIndex = new(
            new SourceFingerprint(100, DummyHash),
            new IndexSchema(
                new Schema([
                    new ColumnInfo("value", DataKind.Float32, false),
                    new ColumnInfo("color", DataKind.String, false),
                ]),
                rows.Length),
            chunks,
            bloomFilters: null,
            sortedIndexes: sortedSet,
            zipDirectory: null,
            bPlusTreeIndexes: null,
            bitmapIndexes: bitmapIndexes);

        Expression filter = new BinaryExpression(
            new BinaryExpression(
                new ColumnReference("value"),
                BinaryOperator.GreaterThan,
                new LiteralExpression(10.0)),
            BinaryOperator.And,
            new BinaryExpression(
                new ColumnReference("color"),
                BinaryOperator.Equal,
                new LiteralExpression("red")));

        (ScanOperator scan, TableCatalog catalog) = CreateScanWithIndex(provider, sourceIndex, filter);

        List<Row> results = await ExecuteScanAsync(scan, catalog);

        // Chunk 0 pruned by statistics. Chunk 1 survives, bitmap row filter keeps red only.
        Assert.Equal(2, results.Count);
        Assert.All(results, row => Assert.Equal("red", row["color"].AsString()));
        Assert.Equal(1, scan.PrunedIndexChunks);
    }

    // ───────────────────── Explain reports bitmap pruning ─────────────────────

    [Fact]
    public void Explain_ReportsBitmapPruningCapability()
    {
        Row[] rows = CreateStringRows("color", "red", "blue");
        BitmapIndexSet bitmapIndexes = BuildBitmapIndex("color", rows, chunkSize: 2);
        SourceIndex sourceIndex = CreateSourceIndexWithBitmaps(rows, bitmapIndexes, chunkSize: 2);

        ScanOperator scan = new(CreateDescriptor("test"), null);
        scan.SetSourceIndex(sourceIndex);

        OperatorPlanDescription plan = scan.DescribeForExplain();
        Assert.NotNull(plan.AccessStrategy);
        Assert.Contains(plan.AccessStrategy.PruningCapabilities,
            capability => capability.Technique == PruningTechnique.BitmapPruning);
    }

    // ───────────────────── Helpers ─────────────────────

    private static Row[] CreateStringRows(string columnName, params string[] values)
    {
        Row[] rows = new Row[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            rows[i] = new Row([columnName], [DataValue.FromString(values[i])]);
        }

        return rows;
    }

    private static TableDescriptor CreateDescriptor(string name)
    {
        return new TableDescriptor("test", name, $"{name}.test",
            new Dictionary<string, string>());
    }

    /// <summary>
    /// Builds a <see cref="BitmapIndexSet"/> for a single column from row data.
    /// </summary>
    private static BitmapIndexSet BuildBitmapIndex(string columnName, Row[] rows, int chunkSize)
    {
        return BuildMultiColumnBitmapIndex([columnName], rows, chunkSize);
    }

    /// <summary>
    /// Builds a <see cref="BitmapIndexSet"/> for multiple columns from row data.
    /// </summary>
    private static BitmapIndexSet BuildMultiColumnBitmapIndex(
        string[] columnNames, Row[] rows, int chunkSize)
    {
        Dictionary<string, BitmapChunkAccumulator> accumulators = new(StringComparer.OrdinalIgnoreCase);
        foreach (string col in columnNames)
        {
            accumulators[col] = new BitmapChunkAccumulator();
        }

        int chunkCount = (rows.Length + chunkSize - 1) / chunkSize;

        for (int chunk = 0; chunk < chunkCount; chunk++)
        {
            int start = chunk * chunkSize;
            int count = Math.Min(chunkSize, rows.Length - start);

            foreach (BitmapChunkAccumulator accumulator in accumulators.Values)
            {
                accumulator.BeginChunk(chunkSize);
            }

            for (int r = 0; r < count; r++)
            {
                foreach (string col in columnNames)
                {
                    accumulators[col].Add(rows[start + r][col], r);
                }
            }

            foreach (BitmapChunkAccumulator accumulator in accumulators.Values)
            {
                accumulator.FinalizeChunk(count);
            }
        }

        Dictionary<string, BitmapColumnIndex> indexes = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, BitmapChunkAccumulator> entry in accumulators)
        {
            BitmapColumnIndex? index = entry.Value.Build();
            if (index is not null)
            {
                indexes[entry.Key] = index;
            }
        }

        return new BitmapIndexSet(indexes);
    }

    /// <summary>
    /// Creates a <see cref="SourceIndex"/> with bitmap indexes only (no sorted index).
    /// </summary>
    private static SourceIndex CreateSourceIndexWithBitmaps(
        Row[] rows, BitmapIndexSet bitmapIndexes, int chunkSize)
    {
        int chunkCount = (rows.Length + chunkSize - 1) / chunkSize;
        List<IndexChunk> chunks = new();

        for (int c = 0; c < chunkCount; c++)
        {
            int start = c * chunkSize;
            int count = Math.Min(chunkSize, rows.Length - start);
            chunks.Add(new IndexChunk(start, count, -1, -1,
                new Dictionary<string, ChunkColumnStatistics>()));
        }

        IReadOnlyList<string> columnNames = rows[0].ColumnNames;
        ColumnInfo[] columns = columnNames
            .Select(name => new ColumnInfo(name, DataKind.String, false))
            .ToArray();

        return new SourceIndex(
            new SourceFingerprint(100, DummyHash),
            new IndexSchema(new Schema(columns), rows.Length),
            chunks,
            bloomFilters: null,
            sortedIndexes: null,
            zipDirectory: null,
            bPlusTreeIndexes: null,
            bitmapIndexes: bitmapIndexes);
    }

    private static (ScanOperator Scan, TableCatalog Catalog) CreateScanWithBitmaps(
        InMemoryProvider provider, SourceIndex sourceIndex, Expression filterHint)
    {
        return CreateScanWithIndex(provider, sourceIndex, filterHint);
    }

    private static (ScanOperator Scan, TableCatalog Catalog) CreateScanWithIndex(
        InMemoryProvider provider, SourceIndex sourceIndex, Expression filterHint)
    {
        TableDescriptor descriptor = CreateDescriptor("data");
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => provider);
        catalog.Register(descriptor);

        ScanOperator scan = new(descriptor, null);
        scan.SetSourceIndex(sourceIndex);
        scan.AddFilterHint(filterHint);
        return (scan, catalog);
    }

    private static async Task<List<Row>> ExecuteScanAsync(ScanOperator scan, TableCatalog catalog)
    {
        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog);

        List<Row> rows = new();
        await foreach (Row row in scan.ExecuteAsync(context))
        {
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>
    /// Non-seekable in-memory provider for testing fallback streaming paths.
    /// </summary>
    private sealed class InMemoryProvider : ITableProvider
    {
        private readonly Row[] _rows;

        internal InMemoryProvider(Row[] rows)
        {
            _rows = rows;
        }

        public Task<Schema> GetSchemaAsync(
            TableDescriptor descriptor, CancellationToken cancellationToken)
        {
            ColumnInfo[] columns = _rows[0].ColumnNames
                .Select(name => new ColumnInfo(name, DataKind.String, false))
                .ToArray();
            return Task.FromResult(new Schema(columns));
        }

        public Task<ProviderCapabilities> GetCapabilitiesAsync(
            TableDescriptor descriptor, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ProviderCapabilities(
                EstimatedRowCount: _rows.Length,
                EstimatedRowSizeBytes: null,
                SupportsSeek: false,
                ColumnCosts: new Dictionary<string, ColumnCost>()));
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
    }
}
