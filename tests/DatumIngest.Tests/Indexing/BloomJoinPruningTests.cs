using System.Runtime.CompilerServices;
using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Indexing;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Indexing;

/// <summary>
/// Integration tests for bloom-filter-based chunk pruning during hash joins.
/// Verifies that the <see cref="JoinOperator"/> pushes build-side key values
/// to the probe-side <see cref="ScanOperator"/> via bloom filters, skipping
/// chunks where no build-side key could possibly match.
/// </summary>
public sealed class BloomJoinPruningTests
{
    [Fact]
    public async Task HashJoin_WithBloomFilters_PrunesChunksWithNoMatchingKeys()
    {
        // Arrange: Build a left (probe) side with 3 chunks of 2 rows each.
        // Chunk 0: ids 1, 2 | Chunk 1: ids 3, 4 | Chunk 2: ids 5, 6
        Row[] leftRows =
        [
            MakeRow(("id", DataValue.FromScalar(1.0f)), ("value", DataValue.FromString("a"))),
            MakeRow(("id", DataValue.FromScalar(2.0f)), ("value", DataValue.FromString("b"))),
            MakeRow(("id", DataValue.FromScalar(3.0f)), ("value", DataValue.FromString("c"))),
            MakeRow(("id", DataValue.FromScalar(4.0f)), ("value", DataValue.FromString("d"))),
            MakeRow(("id", DataValue.FromScalar(5.0f)), ("value", DataValue.FromString("e"))),
            MakeRow(("id", DataValue.FromScalar(6.0f)), ("value", DataValue.FromString("f"))),
        ];

        // Build bloom filters per chunk for the "id" column.
        BloomFilter chunk0Bloom = new(expectedElements: 10);
        chunk0Bloom.Add(DataValue.FromScalar(1.0f));
        chunk0Bloom.Add(DataValue.FromScalar(2.0f));

        BloomFilter chunk1Bloom = new(expectedElements: 10);
        chunk1Bloom.Add(DataValue.FromScalar(3.0f));
        chunk1Bloom.Add(DataValue.FromScalar(4.0f));

        BloomFilter chunk2Bloom = new(expectedElements: 10);
        chunk2Bloom.Add(DataValue.FromScalar(5.0f));
        chunk2Bloom.Add(DataValue.FromScalar(6.0f));

        Dictionary<string, BloomFilter[]> bloomFilters = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = [chunk0Bloom, chunk1Bloom, chunk2Bloom]
        };

        BloomFilterSet bloomFilterSet = new(bloomFilters, chunkCount: 3);

        // Build source index with 3 chunks, each having 2 rows.
        Dictionary<string, ChunkColumnStatistics> chunk0Stats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = new(DataValue.FromScalar(1.0f), DataValue.FromScalar(2.0f), 0, 2, 2)
        };
        Dictionary<string, ChunkColumnStatistics> chunk1Stats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = new(DataValue.FromScalar(3.0f), DataValue.FromScalar(4.0f), 0, 2, 2)
        };
        Dictionary<string, ChunkColumnStatistics> chunk2Stats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = new(DataValue.FromScalar(5.0f), DataValue.FromScalar(6.0f), 0, 2, 2)
        };

        List<IndexChunk> chunks =
        [
            new IndexChunk(0, 2, -1, -1, chunk0Stats),
            new IndexChunk(2, 2, -1, -1, chunk1Stats),
            new IndexChunk(4, 2, -1, -1, chunk2Stats),
        ];

        SourceFingerprint fingerprint = new(0, new byte[32]);
        Schema schema = new([new ColumnInfo("id", DataKind.Scalar, nullable: false)]);
        IndexSchema indexSchema = new(schema, 6);
        SourceIndex sourceIndex = new(fingerprint, indexSchema, chunks, bloomFilterSet);

        // Create ScanOperator for the left side with the source index.
        TableDescriptor descriptor = new("test", "left", "left.test", new Dictionary<string, string>());
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => new InMemoryTableProvider(leftRows));
        ScanOperator scanOperator = new(descriptor, requiredColumns: null);
        scanOperator.SetSourceIndex(sourceIndex);

        // Right (build) side: only has key value 3.0 — should match chunk 1 only.
        MockOperator rightSide = new(
            MakeRow(("rid", DataValue.FromScalar(3.0f)), ("data", DataValue.FromString("match"))));

        // Create join: left.id = right.rid
        JoinOperator join = new(
            scanOperator,
            rightSide,
            JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("id"),
                BinaryOperator.Equal,
                new ColumnReference("rid")));

        ExecutionContext context = new(
            CancellationToken.None,
            FunctionRegistry.CreateDefault(),
            catalog);

        // Act
        List<Row> results = new();
        await foreach (Row row in join.ExecuteAsync(context).ConfigureAwait(false))
        {
            results.Add(row);
        }

        // Assert: only row with id=3 should match.
        Assert.Single(results);
        Assert.Equal(3.0f, results[0]["id"].AsScalar());

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
            MakeRow(("id", DataValue.FromScalar(1.0f))),
            MakeRow(("id", DataValue.FromScalar(2.0f))),
        ];

        BloomFilter chunk0Bloom = new(expectedElements: 10);
        chunk0Bloom.Add(DataValue.FromScalar(1.0f));
        chunk0Bloom.Add(DataValue.FromScalar(2.0f));

        Dictionary<string, BloomFilter[]> bloomFilters = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = [chunk0Bloom]
        };

        BloomFilterSet bloomFilterSet = new(bloomFilters, chunkCount: 1);

        Dictionary<string, ChunkColumnStatistics> stats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = new(DataValue.FromScalar(1.0f), DataValue.FromScalar(2.0f), 0, 2, 2)
        };

        List<IndexChunk> chunks = [new IndexChunk(0, 2, -1, -1, stats)];

        SourceFingerprint fingerprint = new(0, new byte[32]);
        Schema schema = new([new ColumnInfo("id", DataKind.Scalar, nullable: false)]);
        IndexSchema indexSchema = new(schema, 2);
        SourceIndex sourceIndex = new(fingerprint, indexSchema, chunks, bloomFilterSet);

        TableDescriptor descriptor = new("test", "left", "left.test", new Dictionary<string, string>());
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => new InMemoryTableProvider(leftRows));
        ScanOperator scanOperator = new(descriptor, requiredColumns: null);
        scanOperator.SetSourceIndex(sourceIndex);

        MockOperator rightSide = new(
            MakeRow(("rid", DataValue.FromScalar(1.0f))));

        JoinOperator join = new(
            scanOperator,
            rightSide,
            JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("id"),
                BinaryOperator.Equal,
                new ColumnReference("rid")));

        ExecutionContext context = new(
            CancellationToken.None,
            FunctionRegistry.CreateDefault(),
            catalog);

        List<Row> results = new();
        await foreach (Row row in join.ExecuteAsync(context).ConfigureAwait(false))
        {
            results.Add(row);
        }

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
            MakeRow(("id", DataValue.FromScalar(1.0f))),
            MakeRow(("id", DataValue.FromScalar(2.0f))),
        ];

        Dictionary<string, ChunkColumnStatistics> stats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = new(DataValue.FromScalar(1.0f), DataValue.FromScalar(2.0f), 0, 2, 2)
        };

        List<IndexChunk> chunks = [new IndexChunk(0, 2, -1, -1, stats)];

        SourceFingerprint fingerprint = new(0, new byte[32]);
        Schema schema = new([new ColumnInfo("id", DataKind.Scalar, nullable: false)]);
        IndexSchema indexSchema = new(schema, 2);
        SourceIndex sourceIndex = new(fingerprint, indexSchema, chunks);

        TableDescriptor descriptor = new("test", "left", "left.test", new Dictionary<string, string>());
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => new InMemoryTableProvider(leftRows));
        ScanOperator scanOperator = new(descriptor, requiredColumns: null);
        scanOperator.SetSourceIndex(sourceIndex);

        MockOperator rightSide = new(
            MakeRow(("rid", DataValue.FromScalar(999.0f))));

        JoinOperator join = new(
            scanOperator,
            rightSide,
            JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("id"),
                BinaryOperator.Equal,
                new ColumnReference("rid")));

        ExecutionContext context = new(
            CancellationToken.None,
            FunctionRegistry.CreateDefault(),
            catalog);

        List<Row> results = new();
        await foreach (Row row in join.ExecuteAsync(context).ConfigureAwait(false))
        {
            results.Add(row);
        }

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
            MakeRow(("id", DataValue.FromScalar(1.0f))),
            MakeRow(("id", DataValue.FromScalar(2.0f))),
            MakeRow(("id", DataValue.FromScalar(3.0f))),
            MakeRow(("id", DataValue.FromScalar(4.0f))),
        ];

        BloomFilter chunk0Bloom = new(expectedElements: 10);
        chunk0Bloom.Add(DataValue.FromScalar(1.0f));
        chunk0Bloom.Add(DataValue.FromScalar(2.0f));

        BloomFilter chunk1Bloom = new(expectedElements: 10);
        chunk1Bloom.Add(DataValue.FromScalar(3.0f));
        chunk1Bloom.Add(DataValue.FromScalar(4.0f));

        Dictionary<string, BloomFilter[]> bloomFilters = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = [chunk0Bloom, chunk1Bloom]
        };

        BloomFilterSet bloomFilterSet = new(bloomFilters, chunkCount: 2);

        Dictionary<string, ChunkColumnStatistics> chunk0Stats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = new(DataValue.FromScalar(1.0f), DataValue.FromScalar(2.0f), 0, 2, 2)
        };
        Dictionary<string, ChunkColumnStatistics> chunk1Stats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = new(DataValue.FromScalar(3.0f), DataValue.FromScalar(4.0f), 0, 2, 2)
        };

        List<IndexChunk> chunks =
        [
            new IndexChunk(0, 2, -1, -1, chunk0Stats),
            new IndexChunk(2, 2, -1, -1, chunk1Stats),
        ];

        SourceFingerprint fingerprint = new(0, new byte[32]);
        Schema schema = new([new ColumnInfo("id", DataKind.Scalar, nullable: false)]);
        IndexSchema indexSchema = new(schema, 4);
        SourceIndex sourceIndex = new(fingerprint, indexSchema, chunks, bloomFilterSet);

        TableDescriptor descriptor = new("test", "left", "left.test", new Dictionary<string, string>());
        TableCatalog catalog = new();
        catalog.RegisterProvider("test", () => new InMemoryTableProvider(leftRows));
        ScanOperator scanOperator = new(descriptor, requiredColumns: null);
        scanOperator.SetSourceIndex(sourceIndex);

        // Wrap in alias to test traversal.
        AliasOperator aliased = new(scanOperator, "a");

        // Right side has key 4.0 — only chunk 1 should match.
        MockOperator rightSide = new(
            MakeRow(("rid", DataValue.FromScalar(4.0f))));

        JoinOperator join = new(
            aliased,
            rightSide,
            JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("a", "id"),
                BinaryOperator.Equal,
                new ColumnReference("rid")));

        ExecutionContext context = new(
            CancellationToken.None,
            FunctionRegistry.CreateDefault(),
            catalog);

        List<Row> results = new();
        await foreach (Row row in join.ExecuteAsync(context).ConfigureAwait(false))
        {
            results.Add(row);
        }

        Assert.Single(results);
        Assert.Equal(2, scanOperator.TotalIndexChunks);
        Assert.Equal(1, scanOperator.PrunedIndexChunks);
    }

    // ───────────── Helpers ─────────────

    private static Row MakeRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = columns.Select(c => c.Name).ToArray();
        DataValue[] values = columns.Select(c => c.Value).ToArray();
        return new Row(names, values);
    }

    /// <summary>
    /// Simple in-memory operator that yields pre-defined rows.
    /// </summary>
    private sealed class MockOperator : IQueryOperator
    {
        private readonly Row[] _rows;

        public MockOperator(params Row[] rows)
        {
            _rows = rows;
        }

        public async IAsyncEnumerable<Row> ExecuteAsync(ExecutionContext context)
        {
            foreach (Row row in _rows)
            {
                yield return row;
            }

            await Task.CompletedTask;
        }
    }

    /// <summary>
    /// Simple in-memory table provider for testing.
    /// </summary>
    private sealed class InMemoryTableProvider : ITableProvider
    {
        private readonly Row[] _rows;

        public InMemoryTableProvider(Row[] rows)
        {
            _rows = rows;
        }

        public Task<Schema> GetSchemaAsync(TableDescriptor descriptor, CancellationToken cancellationToken)
        {
            if (_rows.Length == 0)
            {
                return Task.FromResult(new Schema([new ColumnInfo("empty", DataKind.String, nullable: true)]));
            }

            List<ColumnInfo> columns = new();
            foreach (string name in _rows[0].ColumnNames)
            {
                columns.Add(new ColumnInfo(name, _rows[0][name].Kind, nullable: true));
            }

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
