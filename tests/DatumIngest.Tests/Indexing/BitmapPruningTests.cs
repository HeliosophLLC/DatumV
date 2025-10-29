using System.Runtime.CompilerServices;
using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
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
public sealed class BitmapPruningTests : ServiceTestBase
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
        InMemoryTableProvider provider = CreateInMemoryProvider("data", rows);
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
        InMemoryTableProvider provider = CreateInMemoryProvider("data", rows);
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

        InMemoryTableProvider provider = CreateInMemoryProvider("data", rows);
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
        InMemoryTableProvider provider = CreateInMemoryProvider("data", rows);
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
        InMemoryTableProvider provider = CreateInMemoryProvider("data", rows);
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
        InMemoryTableProvider provider = CreateInMemoryProvider("data", rows);
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

        InMemoryTableProvider provider = CreateInMemoryProvider("data", rows);
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
        InMemoryTableProvider provider = CreateInMemoryProvider("data", rows);
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
        InMemoryTableProvider provider = CreateInMemoryProvider("data", rows);
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
        InMemoryTableProvider provider = CreateInMemoryProvider("data", rows);
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

        InMemoryTableProvider provider = CreateInMemoryProvider("data", rows);
        BitmapIndexSet bitmapIndexes = BuildMultiColumnBitmapIndex(["color"], rows, chunkSize: 3);
        List<IndexChunk> chunks = new();
        for (int c = 0; c < 2; c++)
        {
            int start = c * 3;
            Dictionary<string, ChunkColumnStatistics> stats = new(StringComparer.OrdinalIgnoreCase)
            {
                ["value"] = new ChunkColumnStatistics(
                    rows[start]["value"], rows[start + 2]["value"], 0, 3, 3)
            };
            chunks.Add(new IndexChunk(start, 3, stats));
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

    // ───────────────────── Boolean column pruning ─────────────────────

    /// <summary>
    /// Verifies that a SQL <c>TRUE</c>/<c>FALSE</c> literal correctly matches a
    /// <see cref="DataKind.Boolean"/>-typed bitmap index column. Before centralization,
    /// the removed <c>ConvertLiteralToDataValue</c> helper produced <c>Float32(1f)</c>
    /// for boolean literals, which never matched <c>Boolean(true)</c> dictionary keys,
    /// causing all chunks to be pruned and returning zero rows.
    /// Now <see cref="DataValue.FromLiteral"/> produces the correct kind, and
    /// <see cref="BitmapColumnIndex"/> normalizes lookup keys via <see cref="DataValue.CoerceToKind"/>.
    /// </summary>
    [Fact]
    public async Task BitmapPruning_BooleanLiteralTrue_PrunesChunkWithOnlyFalseValues()
    {
        // 4 rows in 2 chunks of 2:
        // Chunk 0: [false, false]   Chunk 1: [true, true]
        // WHERE flag = TRUE → chunk 0 has no true entries → should be pruned.
        Row[] rows = CreateBooleanRows("flag", false, false, true, true);
        InMemoryTableProvider provider = CreateInMemoryProvider("data", rows);
        BitmapIndexSet bitmapIndexes = BuildBitmapIndex("flag", rows, chunkSize: 2);
        SourceIndex sourceIndex = CreateSourceIndexWithBitmaps(rows, bitmapIndexes, chunkSize: 2);

        (ScanOperator scan, TableCatalog catalog) = CreateScanWithBitmaps(provider, sourceIndex,
            new BinaryExpression(
                new ColumnReference("flag"),
                BinaryOperator.Equal,
                new LiteralExpression(true)));

        List<Row> results = await ExecuteScanAsync(scan, catalog);

        Assert.Equal(2, scan.TotalIndexChunks);
        Assert.Equal(1, scan.PrunedIndexChunks);
        Assert.Equal(2, results.Count);
        Assert.All(results, row => Assert.True(row["flag"].AsBoolean()));
    }

    // ───────────────────── Cross-type coercion pruning ─────────────────────

    /// <summary>
    /// Verifies that a <c>Float64</c> literal (as the SQL parser produces for numeric
    /// constants) is correctly coerced to the bitmap column's <c>Int32</c> key kind by
    /// <see cref="BitmapColumnIndex"/>'s internal key normalization. Without coercion,
    /// <c>Float64(30.0)</c> would never match <c>Int32(30)</c> dictionary keys.
    /// </summary>
    [Fact]
    public async Task BitmapPruning_Float64LiteralAgainstInt32Column_CoercesAndPrunes()
    {
        // 4 rows in 2 chunks of 2:
        // Chunk 0: [10, 20]   Chunk 1: [30, 40]
        // WHERE quantity = 30.0 (double) → only chunk 1 should match.
        Row[] rows = CreateInt32Rows("quantity", 10, 20, 30, 40);
        InMemoryTableProvider provider = CreateInMemoryProvider("data", rows);
        BitmapIndexSet bitmapIndexes = BuildBitmapIndex("quantity", rows, chunkSize: 2);
        SourceIndex sourceIndex = CreateSourceIndexWithBitmaps(rows, bitmapIndexes, chunkSize: 2);

        (ScanOperator scan, TableCatalog catalog) = CreateScanWithBitmaps(provider, sourceIndex,
            new BinaryExpression(
                new ColumnReference("quantity"),
                BinaryOperator.Equal,
                new LiteralExpression(30.0)));

        List<Row> results = await ExecuteScanAsync(scan, catalog);

        Assert.Equal(2, scan.TotalIndexChunks);
        Assert.Equal(1, scan.PrunedIndexChunks);
        Assert.Single(results);
        Assert.Equal(30, results[0]["quantity"].AsInt32());
    }

    // ───────────────────── Explain reports bitmap pruning ─────────────────────

    [Fact]
    public void Explain_ReportsBitmapPruningCapability()
    {
        Row[] rows = CreateStringRows("color", "red", "blue");
        BitmapIndexSet bitmapIndexes = BuildBitmapIndex("color", rows, chunkSize: 2);
        SourceIndex sourceIndex = CreateSourceIndexWithBitmaps(rows, bitmapIndexes, chunkSize: 2);

        InMemoryTableProvider provider = CreateInMemoryProvider("data", rows);
        ScanOperator scan = new(provider, null, rows.Length);
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

    private static Row[] CreateBooleanRows(string columnName, params bool[] values)
    {
        Row[] rows = new Row[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            rows[i] = new Row([columnName], [DataValue.FromBoolean(values[i])]);
        }

        return rows;
    }

    private static Row[] CreateInt32Rows(string columnName, params int[] values)
    {
        Row[] rows = new Row[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            rows[i] = new Row([columnName], [DataValue.FromInt32(values[i])]);
        }

        return rows;
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
            chunks.Add(new IndexChunk(start, count, new Dictionary<string, ChunkColumnStatistics>()));
        }

        IReadOnlyList<string> columnNames = rows[0].ColumnNames;
        ColumnInfo[] columns = new ColumnInfo[columnNames.Count];
        for (int i = 0; i < columnNames.Count; i++)
        {
            columns[i] = new ColumnInfo(columnNames[i], rows[0][columnNames[i]].Kind, false);
        }

        return new SourceIndex(
            new SourceFingerprint(100, DummyHash),
            new IndexSchema(new Schema(columns), rows.Length),
            chunks,
            bloomFilters: null,
            bPlusTreeIndexes: null,
            bitmapIndexes: bitmapIndexes);
    }

    private (ScanOperator Scan, TableCatalog Catalog) CreateScanWithBitmaps(
        InMemoryTableProvider provider, SourceIndex sourceIndex, Expression filterHint)
    {
        return CreateScanWithIndex(provider, sourceIndex, filterHint);
    }

    private (ScanOperator Scan, TableCatalog Catalog) CreateScanWithIndex(
        InMemoryTableProvider provider, SourceIndex sourceIndex, Expression filterHint)
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(provider);

        ScanOperator scan = new(provider, null, provider.GetRowCount());
        scan.SetSourceIndex(sourceIndex);
        scan.AddFilterHint(filterHint);
        return (scan, catalog);
    }

    private async Task<List<Row>> ExecuteScanAsync(ScanOperator scan, TableCatalog catalog)
    {
        ExecutionContext context = CreateExecutionContext(catalog: catalog);

        return await scan.CollectRowsAsync(context);
    }
}