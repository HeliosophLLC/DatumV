using DatumIngest.Catalog;
using DatumIngest.Execution.Operators;
using DatumIngest.Indexing;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;
using DatumIngest.Indexing.Bloom;
using DatumIngest.Catalog.Providers;

namespace DatumIngest.Tests.Indexing;

/// <summary>
/// Integration tests for bloom-filter-based chunk pruning during hash joins.
/// Verifies that the <see cref="JoinOperator"/> pushes build-side key values
/// to the probe-side <see cref="ScanOperator"/> via bloom filters, skipping
/// chunks where no build-side key could possibly match.
/// </summary>
public sealed class BloomJoinPruningTests : ServiceTestBase
{
    private readonly Arena _store;

    public BloomJoinPruningTests()
    {
        _store = CreateArena();
    }

    [Fact]
    public async Task HashJoin_WithBloomFilters_PrunesChunksWithNoMatchingKeys()
    {
        ColumnLookup lookup = new(["id", "value"]);
        // Arrange: Build a left (probe) side with 3 chunks of 2 rows each.
        // Chunk 0: ids 1, 2 | Chunk 1: ids 3, 4 | Chunk 2: ids 5, 6
        Row[] leftRows =
        [
            MakeRow(lookup, DataValue.FromFloat32(1.0f), DataValue.FromString("a")),
            MakeRow(lookup, DataValue.FromFloat32(2.0f), DataValue.FromString("b")),
            MakeRow(lookup, DataValue.FromFloat32(3.0f), DataValue.FromString("c")),
            MakeRow(lookup, DataValue.FromFloat32(4.0f), DataValue.FromString("d")),
            MakeRow(lookup, DataValue.FromFloat32(5.0f), DataValue.FromString("e")),
            MakeRow(lookup, DataValue.FromFloat32(6.0f), DataValue.FromString("f")),
        ];

        // Build bloom filters per chunk for the "id" column.
        BloomFilter chunk0Bloom = new(expectedElements: 10);
        chunk0Bloom.Add(DataValue.FromFloat32(1.0f), _store);
        chunk0Bloom.Add(DataValue.FromFloat32(2.0f), _store);

        BloomFilter chunk1Bloom = new(expectedElements: 10);
        chunk1Bloom.Add(DataValue.FromFloat32(3.0f), _store);
        chunk1Bloom.Add(DataValue.FromFloat32(4.0f), _store);

        BloomFilter chunk2Bloom = new(expectedElements: 10);
        chunk2Bloom.Add(DataValue.FromFloat32(5.0f), _store);
        chunk2Bloom.Add(DataValue.FromFloat32(6.0f), _store);

        Dictionary<string, BloomFilter[]> bloomFilters = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = [chunk0Bloom, chunk1Bloom, chunk2Bloom]
        };

        BloomFilterSet bloomFilterSet = new(bloomFilters, chunkCount: 3);

        // Build source index with 3 chunks, each having 2 rows.
        Dictionary<string, ChunkColumnStatistics> chunk0Stats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = new(DataValue.FromFloat32(1.0f), DataValue.FromFloat32(2.0f), 0, 2, 2)
        };
        Dictionary<string, ChunkColumnStatistics> chunk1Stats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = new(DataValue.FromFloat32(3.0f), DataValue.FromFloat32(4.0f), 0, 2, 2)
        };
        Dictionary<string, ChunkColumnStatistics> chunk2Stats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = new(DataValue.FromFloat32(5.0f), DataValue.FromFloat32(6.0f), 0, 2, 2)
        };

        List<IndexChunk> chunks =
        [
            new IndexChunk(0, 2, chunk0Stats),
            new IndexChunk(2, 2, chunk1Stats),
            new IndexChunk(4, 2, chunk2Stats),
        ];

        SourceFingerprint fingerprint = new(0, new byte[32]);
        Schema schema = new([new ColumnInfo("id", DataKind.Float32, nullable: false)]);
        IndexSchema indexSchema = new(schema, 6);
        SourceIndex sourceIndex = new(fingerprint, indexSchema, chunks, bloomFilterSet);

        // Create ScanOperator for the left side with the source index.
        TableCatalog catalog = CreateCatalog(("left", leftRows));
        ((InMemoryTableProvider)catalog["left"]).ProvideSourceIndex(sourceIndex);
        ScanOperator scanOperator = new(catalog["left"], null, leftRows.Length);

        // Right (build) side: only has key value 3.0 — should match chunk 1 only.
        MockOperator rightSide = CreateMockOperator(["rid", "data"], [[3.0f, "match"]]);

        // Create join: left.id = right.rid
        JoinOperator join = new(
            scanOperator,
            rightSide,
            JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("id"),
                BinaryOperator.Equal,
                new ColumnReference("rid")));

        ExecutionContext context = CreateExecutionContext();

        // Act
        List<Row> results = await join.ExecuteAsync(context).CollectRowsAsync();

        // Assert: only row with id=3 should match.
        Assert.Single(results);
        Assert.Equal(3.0f, results[0]["id"].AsFloat32());

        // Chunks 0 and 2 should have been pruned by bloom filters.
        Assert.Equal(3, scanOperator.TotalIndexChunks);
        Assert.Equal(2, scanOperator.PrunedIndexChunks);
    }

    [Fact]
    public async Task HashJoin_WithBloomFilters_NoPruningWhenAllChunksMatch()
    {
        // All chunks contain values that might match — nothing should be pruned.
        Row[] leftRows =
        [
            MakeRow(["id"], DataValue.FromFloat32(1.0f)),
            MakeRow(["id"], DataValue.FromFloat32(2.0f)),
        ];

        BloomFilter chunk0Bloom = new(expectedElements: 10);
        chunk0Bloom.Add(DataValue.FromFloat32(1.0f), _store);
        chunk0Bloom.Add(DataValue.FromFloat32(2.0f), _store);

        Dictionary<string, BloomFilter[]> bloomFilters = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = [chunk0Bloom]
        };

        BloomFilterSet bloomFilterSet = new(bloomFilters, chunkCount: 1);

        Dictionary<string, ChunkColumnStatistics> stats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = new(DataValue.FromFloat32(1.0f), DataValue.FromFloat32(2.0f), 0, 2, 2)
        };

        List<IndexChunk> chunks = [new IndexChunk(0, 2, stats)];

        SourceFingerprint fingerprint = new(0, new byte[32]);
        Schema schema = new([new ColumnInfo("id", DataKind.Float32, nullable: false)]);
        IndexSchema indexSchema = new(schema, 2);
        SourceIndex sourceIndex = new(fingerprint, indexSchema, chunks, bloomFilterSet);

        TableCatalog catalog = CreateCatalog(("left", leftRows));
        ((InMemoryTableProvider)catalog["left"]).ProvideSourceIndex(sourceIndex);
        ScanOperator scanOperator = new(catalog["left"], null, leftRows.Length);

        MockOperator rightSide = CreateMockOperator(["rid"], [[1.0f]]);

        JoinOperator join = new(
            scanOperator,
            rightSide,
            JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("id"),
                BinaryOperator.Equal,
                new ColumnReference("rid")));

        ExecutionContext context = CreateExecutionContext();

        List<Row> results = await join.ExecuteAsync(context).CollectRowsAsync();

        Assert.Single(results);
        Assert.Equal(1, scanOperator.TotalIndexChunks);
        Assert.Equal(0, scanOperator.PrunedIndexChunks);
    }

    [Fact]
    public async Task HashJoin_WithoutBloomFilters_NoPruning()
    {
        // Source index exists but has no bloom filters — no pruning should occur.
        Row[] leftRows =
        [
            MakeRow(["id"], DataValue.FromFloat32(1.0f)),
            MakeRow(["id"], DataValue.FromFloat32(2.0f)),
        ];

        Dictionary<string, ChunkColumnStatistics> stats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = new(DataValue.FromFloat32(1.0f), DataValue.FromFloat32(2.0f), 0, 2, 2)
        };

        List<IndexChunk> chunks = [new IndexChunk(0, 2, stats)];

        SourceFingerprint fingerprint = new(0, new byte[32]);
        Schema schema = new([new ColumnInfo("id", DataKind.Float32, nullable: false)]);
        IndexSchema indexSchema = new(schema, 2);
        SourceIndex sourceIndex = new(fingerprint, indexSchema, chunks);

        TableCatalog catalog = CreateCatalog(("left", leftRows));
        ((InMemoryTableProvider)catalog["left"]).ProvideSourceIndex(sourceIndex);
        ScanOperator scanOperator = new(catalog["left"], null, leftRows.Length);

        MockOperator rightSide = CreateMockOperator(["rid"], [[999.0f]]);

        JoinOperator join = new(
            scanOperator,
            rightSide,
            JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("id"),
                BinaryOperator.Equal,
                new ColumnReference("rid")));

        ExecutionContext context = CreateExecutionContext();

        List<Row> results = await join.ExecuteAsync(context).CollectRowsAsync();

        Assert.Empty(results);
        // No bloom filters → no bloom pruning (stats would remain 0).
        Assert.Equal(0, scanOperator.TotalIndexChunks);
    }

    [Fact]
    public async Task HashJoin_WithAliasOperator_BloomPruningStillWorks()
    {
        // Probe side is wrapped in AliasOperator — FindScanOperator should traverse it.
        Row[] leftRows =
        [
            MakeRow(["id"], DataValue.FromFloat32(1.0f)),
            MakeRow(["id"], DataValue.FromFloat32(2.0f)),
            MakeRow(["id"], DataValue.FromFloat32(3.0f)),
            MakeRow(["id"], DataValue.FromFloat32(4.0f)),
        ];

        BloomFilter chunk0Bloom = new(expectedElements: 10);
        chunk0Bloom.Add(DataValue.FromFloat32(1.0f), _store);
        chunk0Bloom.Add(DataValue.FromFloat32(2.0f), _store);

        BloomFilter chunk1Bloom = new(expectedElements: 10);
        chunk1Bloom.Add(DataValue.FromFloat32(3.0f), _store);
        chunk1Bloom.Add(DataValue.FromFloat32(4.0f), _store);

        Dictionary<string, BloomFilter[]> bloomFilters = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = [chunk0Bloom, chunk1Bloom]
        };

        BloomFilterSet bloomFilterSet = new(bloomFilters, chunkCount: 2);

        Dictionary<string, ChunkColumnStatistics> chunk0Stats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = new(DataValue.FromFloat32(1.0f), DataValue.FromFloat32(2.0f), 0, 2, 2)
        };
        Dictionary<string, ChunkColumnStatistics> chunk1Stats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = new(DataValue.FromFloat32(3.0f), DataValue.FromFloat32(4.0f), 0, 2, 2)
        };

        List<IndexChunk> chunks =
        [
            new IndexChunk(0, 2, chunk0Stats),
            new IndexChunk(2, 2, chunk1Stats),
        ];

        SourceFingerprint fingerprint = new(0, new byte[32]);
        Schema schema = new([new ColumnInfo("id", DataKind.Float32, nullable: false)]);
        IndexSchema indexSchema = new(schema, 4);
        SourceIndex sourceIndex = new(fingerprint, indexSchema, chunks, bloomFilterSet);

        TableCatalog catalog = CreateCatalog(("left", leftRows));
        ((InMemoryTableProvider)catalog["left"]).ProvideSourceIndex(sourceIndex);
        ScanOperator scanOperator = new(catalog["left"], null, leftRows.Length);

        // Wrap in alias to test traversal.
        AliasOperator aliased = new(scanOperator, "a");

        // Right side has key 4.0 — only chunk 1 should match.
        MockOperator rightSide = CreateMockOperator(["rid"], [[4.0f]]);

        JoinOperator join = new(
            aliased,
            rightSide,
            JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("a", "id"),
                BinaryOperator.Equal,
                new ColumnReference("rid")));

        ExecutionContext context = CreateExecutionContext();

        List<Row> results = await join.ExecuteAsync(context).CollectRowsAsync();

        Assert.Single(results);
        Assert.Equal(2, scanOperator.TotalIndexChunks);
        Assert.Equal(1, scanOperator.PrunedIndexChunks);
    }

    /// <summary>
    /// Multi-level bloom pruning: when the probe side of an outer join
    /// itself contains an inner join, bloom keys from the outer build side
    /// should reach <see cref="ScanOperator"/> instances buried inside the
    /// inner join.  This verifies the <see cref="JoinOperator.CollectScanOperators"/>
    /// traversal through nested <see cref="JoinOperator"/> nodes.
    /// </summary>
    [Fact]
    public async Task HashJoin_NestedJoin_BloomPruningReachesBuriedScan()
    {
        ColumnLookup lookup = new(["order_id", "product_id"]);
        // Inner-left scan: "orders" table with 2 chunks on column "product_id".
        // Chunk 0: product_id 1.0, 2.0 | Chunk 1: product_id 3.0, 4.0
        Row[] orderRows =
        [
            MakeRow(lookup, DataValue.FromFloat32(100.0f), DataValue.FromFloat32(1.0f)),
            MakeRow(lookup, DataValue.FromFloat32(101.0f), DataValue.FromFloat32(2.0f)),
            MakeRow(lookup, DataValue.FromFloat32(102.0f), DataValue.FromFloat32(3.0f)),
            MakeRow(lookup, DataValue.FromFloat32(103.0f), DataValue.FromFloat32(4.0f)),
        ];

        BloomFilter orderChunk0Bloom = new(expectedElements: 10);
        orderChunk0Bloom.Add(DataValue.FromFloat32(1.0f), _store);
        orderChunk0Bloom.Add(DataValue.FromFloat32(2.0f), _store);

        BloomFilter orderChunk1Bloom = new(expectedElements: 10);
        orderChunk1Bloom.Add(DataValue.FromFloat32(3.0f), _store);
        orderChunk1Bloom.Add(DataValue.FromFloat32(4.0f), _store);

        Dictionary<string, BloomFilter[]> orderBloomFilters = new(StringComparer.OrdinalIgnoreCase)
        {
            ["product_id"] = [orderChunk0Bloom, orderChunk1Bloom]
        };

        BloomFilterSet orderBloomSet = new(orderBloomFilters, chunkCount: 2);

        Dictionary<string, ChunkColumnStatistics> orderChunk0Stats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["product_id"] = new(DataValue.FromFloat32(1.0f), DataValue.FromFloat32(2.0f), 0, 2, 2)
        };
        Dictionary<string, ChunkColumnStatistics> orderChunk1Stats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["product_id"] = new(DataValue.FromFloat32(3.0f), DataValue.FromFloat32(4.0f), 0, 2, 2)
        };

        List<IndexChunk> orderChunks =
        [
            new IndexChunk(0, 2, orderChunk0Stats),
            new IndexChunk(2, 2, orderChunk1Stats),
        ];

        SourceFingerprint orderFingerprint = new(0, new byte[32]);
        Schema orderSchema = new([new ColumnInfo("product_id", DataKind.Float32, nullable: false)]);
        IndexSchema orderIndexSchema = new(orderSchema, 4);
        SourceIndex orderSourceIndex = new(orderFingerprint, orderIndexSchema, orderChunks, orderBloomSet);

        TableCatalog catalog = CreateCatalog(("orders", orderRows));
        ((InMemoryTableProvider)catalog["orders"]).ProvideSourceIndex(orderSourceIndex);
        ScanOperator orderScan = new(catalog["orders"], null, orderRows.Length);

        // Inner right: "customers" table — simple, no bloom needed.
        // Must expose order_id so the inner join's equality predicate
        // resolves; the original test was missing this column.
        MockOperator customerSide = CreateMockOperator(
            ["customer_id", "order_id"], [[1.0f, 100.0f]]);

        // Inner join: orders JOIN customers ON orders.order_id = customers.order_id
        JoinOperator innerJoin = new(
            orderScan,
            customerSide,
            JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("order_id"),
                BinaryOperator.Equal,
                new ColumnReference("order_id")));

        // Outer right (build): "products" table has product_id = 3.0.
        // This should prune the orders scan to chunk 1 only.
        MockOperator productsSide = CreateMockOperator(["product_id"], [[3.0f, "Widget"]]);

        // Outer join: (orders ⋈ customers) JOIN products ON orders.product_id = products.product_id
        JoinOperator outerJoin = new(
            innerJoin,
            productsSide,
            JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("product_id"),
                BinaryOperator.Equal,
                new ColumnReference("product_id")));

        ExecutionContext context = CreateExecutionContext();

        // Act
        List<Row> results = await outerJoin.ExecuteAsync(context).CollectRowsAsync();

        // Assert: the orders scan should show bloom pruning from the outer join's build side.
        // product_id=3.0 exists only in chunk 1, so chunk 0 should be pruned.
        Assert.Equal(2, orderScan.TotalIndexChunks);
        Assert.Equal(1, orderScan.PrunedIndexChunks);
    }

    /// <summary>
    /// <see cref="JoinOperator.CollectScanOperators"/> should discover all
    /// <see cref="ScanOperator"/> instances through nested joins, aliases,
    /// filters, and projections.
    /// </summary>
    [Fact]
    public void CollectScanOperators_FindsAllScansInNestedJoinTree()
    {
        InMemoryTableProvider t1 = CreateInMemoryProvider("t1", []);
        InMemoryTableProvider t2 = CreateInMemoryProvider("t2", []);
        InMemoryTableProvider t3 = CreateInMemoryProvider("t3", []);

        ScanOperator scan1 = new(t1, null, 123);
        ScanOperator scan2 = new(t2, null, 234);
        ScanOperator scan3 = new(t3, null, 345);

        // Wrap scan2 in AliasOperator.
        AliasOperator aliased2 = new(scan2, "a2");

        // Inner join: scan1 ⋈ aliased scan2.
        JoinOperator innerJoin = new(scan1, aliased2, JoinType.Inner, null);

        // Outer join: innerJoin ⋈ scan3.
        JoinOperator outerJoin = new(innerJoin, scan3, JoinType.Inner, null);

        List<ScanOperator> results = new();
        JoinOperator.CollectScanOperators(outerJoin, results);

        // Should find all 3 scans.
        Assert.Equal(3, results.Count);
        Assert.Contains(scan1, results);
        Assert.Contains(scan2, results);
        Assert.Contains(scan3, results);
    }
}