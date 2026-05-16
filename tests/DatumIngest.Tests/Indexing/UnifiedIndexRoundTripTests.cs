using System.Text;
using DatumIngest.Indexing;
using DatumIngest.Indexing.Bitmap;
using DatumIngest.Indexing.BTree;
using DatumIngest.Model;
using DatumIngest.Indexing.Sorted;
using DatumIngest.Indexing.Bloom;

namespace DatumIngest.Tests.Indexing;

/// <summary>
/// Round-trip tests for the v5 unified index format. Writes a <see cref="SourceIndexSet"/>
/// through <see cref="UnifiedIndexWriter"/> to a temp file, reads it back via
/// <see cref="UnifiedIndexReader.Open"/>, and validates that all sections
/// (fingerprint, schema, chunks, bloom filters, sorted indexes, B+Tree pages,
/// bitmap indexes) survive the cycle.
/// </summary>
public sealed class UnifiedIndexRoundTripTests : ServiceTestBase
{
    private readonly Arena _store;
    private readonly string _tempDirectory;

    /// <summary>Creates a temporary directory for test files.</summary>
    public UnifiedIndexRoundTripTests()
    {
        _tempDirectory = Path.Combine(
            Path.GetTempPath(), "UnifiedIndexTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _store = CreateArena();
    }

    /// <summary>Cleans up test files.</summary>
    public override void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        base.Dispose();
    }

    // ────────────────────── Header / structure ──────────────────────

    [Fact]
    public void RoundTrip_EmptyIndex_PreservesStructure()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        Schema schema = new([new ColumnInfo("id", DataKind.Float32, nullable: false)]);
        IndexSchema indexSchema = new(schema, 0);
        SourceIndex original = new(fingerprint, indexSchema, Array.Empty<IndexChunk>());

        using MappedSourceIndexSet mapped = WriteAndReopen("empty", original);
        SourceIndex restored = mapped.IndexSet.Tables["test"];

        Assert.Equal(0, restored.Fingerprint.FileSize);
        Assert.Equal(0, restored.Schema.TotalRowCount);
        Assert.Single(restored.Schema.Schema.Columns);
        Assert.Equal("id", restored.Schema.Schema.Columns[0].Name);
        Assert.Empty(restored.Chunks);
    }

    // ────────────────────── Fingerprint ──────────────────────

    [Fact]
    public void RoundTrip_Fingerprint_PreservesAllFields()
    {
        byte[] hash = new byte[32];
        Random.Shared.NextBytes(hash);
        SourceFingerprint fingerprint = new(123456789L, hash);
        Schema schema = new([new ColumnInfo("x", DataKind.String, nullable: true)]);
        IndexSchema indexSchema = new(schema, 100);
        SourceIndex original = new(fingerprint, indexSchema, Array.Empty<IndexChunk>());

        using MappedSourceIndexSet mapped = WriteAndReopen("fingerprint", original);
        SourceIndex restored = mapped.IndexSet.Tables["test"];

        Assert.Equal(123456789L, restored.Fingerprint.FileSize);
        Assert.Equal(hash, restored.Fingerprint.StripedHash);
    }

    // ────────────────────── Schema ──────────────────────

    [Fact]
    public void RoundTrip_Schema_PreservesColumnsAndRowCount()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        Schema schema = new([
            new ColumnInfo("id", DataKind.Float32, nullable: false),
            new ColumnInfo("name", DataKind.String, nullable: true),
            new ColumnInfo("created", DataKind.TimestampTz, nullable: true),
            new ColumnInfo("flag", DataKind.Boolean, nullable: false),
        ]);
        IndexSchema indexSchema = new(schema, 50_000);
        SourceIndex original = new(fingerprint, indexSchema, Array.Empty<IndexChunk>());

        using MappedSourceIndexSet mapped = WriteAndReopen("schema", original);
        SourceIndex restored = mapped.IndexSet.Tables["test"];

        Assert.Equal(50_000, restored.Schema.TotalRowCount);
        Assert.Equal(4, restored.Schema.Schema.Columns.Count);
        Assert.Equal("id", restored.Schema.Schema.Columns[0].Name);
        Assert.Equal(DataKind.Float32, restored.Schema.Schema.Columns[0].Kind);
        Assert.False(restored.Schema.Schema.Columns[0].Nullable);
        Assert.Equal("name", restored.Schema.Schema.Columns[1].Name);
        Assert.True(restored.Schema.Schema.Columns[1].Nullable);
        Assert.Equal(DataKind.TimestampTz, restored.Schema.Schema.Columns[2].Kind);
        Assert.Equal(DataKind.Boolean, restored.Schema.Schema.Columns[3].Kind);
    }

    // ────────────────────── Chunk directory ──────────────────────

    [Fact]
    public void RoundTrip_ChunkDirectory_PreservesChunkMetadata()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        Schema schema = new([new ColumnInfo("value", DataKind.Float32, nullable: true)]);
        IndexSchema indexSchema = new(schema, 25_000);

        Dictionary<string, ChunkColumnStatistics> chunk1Stats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["value"] = new ChunkColumnStatistics(
                DataValue.FromFloat32(1.0f), DataValue.FromFloat32(100.0f),
                NullCount: 5, RowCount: 10_000, EstimatedCardinality: 95)
        };

        Dictionary<string, ChunkColumnStatistics> chunk2Stats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["value"] = new ChunkColumnStatistics(
                DataValue.FromFloat32(50.0f), DataValue.FromFloat32(200.0f),
                NullCount: 0, RowCount: 10_000, EstimatedCardinality: 150)
        };

        List<IndexChunk> chunks =
        [
            new IndexChunk(0, 10_000, chunk1Stats),
            new IndexChunk(10_000, 10_000, chunk2Stats),
        ];

        SourceIndex original = new(fingerprint, indexSchema, chunks);

        using MappedSourceIndexSet mapped = WriteAndReopen("chunks", original);
        SourceIndex restored = mapped.IndexSet.Tables["test"];

        Assert.Equal(2, restored.Chunks.Count);

        Assert.Equal(0, restored.Chunks[0].RowOffset);
        Assert.Equal(10_000, restored.Chunks[0].RowCount);

        Assert.Equal(10_000, restored.Chunks[1].RowOffset);
        Assert.Equal(10_000, restored.Chunks[1].RowCount);
    }

    [Fact]
    public void RoundTrip_ChunkColumnStatistics_PreservesFloat32MinMax()
    {
        SourceIndex original = BuildIndexWithChunk(
            DataKind.Float32,
            new ChunkColumnStatistics(
                DataValue.FromFloat32(1.5f), DataValue.FromFloat32(99.9f),
                NullCount: 3, RowCount: 100, EstimatedCardinality: 42));

        using MappedSourceIndexSet mapped = WriteAndReopen("stats_float32", original);
        SourceIndex restored = mapped.IndexSet.Tables["test"];
        ChunkColumnStatistics stats = restored.Chunks[0].ColumnStatistics["value"];

        Assert.Equal(1.5f, stats.Minimum.GetValueOrDefault().AsFloat32());
        Assert.Equal(99.9f, stats.Maximum.GetValueOrDefault().AsFloat32());
        Assert.Equal(3, stats.NullCount);
        Assert.Equal(100, stats.RowCount);
        Assert.Equal(42, stats.EstimatedCardinality);
    }

    [Fact]
    public void RoundTrip_ChunkColumnStatistics_PreservesInt32MinMax()
    {
        SourceIndex original = BuildIndexWithChunk(
            DataKind.Int32,
            new ChunkColumnStatistics(
                DataValue.FromInt32(-100), DataValue.FromInt32(500),
                NullCount: 0, RowCount: 1000, EstimatedCardinality: 600));

        using MappedSourceIndexSet mapped = WriteAndReopen("stats_int32", original);
        SourceIndex restored = mapped.IndexSet.Tables["test"];
        ChunkColumnStatistics stats = restored.Chunks[0].ColumnStatistics["value"];

        Assert.Equal(-100, stats.Minimum.GetValueOrDefault().AsInt32());
        Assert.Equal(500, stats.Maximum.GetValueOrDefault().AsInt32());
    }

    [Fact]
    public void RoundTrip_ChunkColumnStatistics_PreservesDateMinMax()
    {
        SourceIndex original = BuildIndexWithChunk(
            DataKind.Date,
            new ChunkColumnStatistics(
                DataValue.FromDate(new DateOnly(2020, 1, 1)),
                DataValue.FromDate(new DateOnly(2025, 12, 31)),
                NullCount: 0, RowCount: 100, EstimatedCardinality: 100));

        using MappedSourceIndexSet mapped = WriteAndReopen("stats_date", original);
        SourceIndex restored = mapped.IndexSet.Tables["test"];
        ChunkColumnStatistics stats = restored.Chunks[0].ColumnStatistics["value"];

        Assert.Equal(new DateOnly(2020, 1, 1), stats.Minimum.GetValueOrDefault().AsDate());
        Assert.Equal(new DateOnly(2025, 12, 31), stats.Maximum.GetValueOrDefault().AsDate());
    }

    [Fact]
    public void RoundTrip_ChunkColumnStatistics_NullMinMaxPreserved()
    {
        SourceIndex original = BuildIndexWithChunk(
            DataKind.Float32,
            new ChunkColumnStatistics(null, null, NullCount: 100, RowCount: 100, EstimatedCardinality: 0));

        using MappedSourceIndexSet mapped = WriteAndReopen("stats_null", original);
        SourceIndex restored = mapped.IndexSet.Tables["test"];
        ChunkColumnStatistics stats = restored.Chunks[0].ColumnStatistics["value"];

        Assert.Null(stats.Minimum);
        Assert.Null(stats.Maximum);
        Assert.Equal(100, stats.NullCount);
    }

    [Fact]
    public void RoundTrip_ChunkColumnStatistics_StringMinMax()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        Schema schema = new([new ColumnInfo("name", DataKind.String, nullable: true)]);
        IndexSchema indexSchema = new(schema, 100);

        Dictionary<string, ChunkColumnStatistics> stats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = new ChunkColumnStatistics(
                DataValue.FromString("alice"), DataValue.FromString("zoe"),
                NullCount: 0, RowCount: 100, EstimatedCardinality: 50)
        };

        List<IndexChunk> chunks = [new IndexChunk(0, 100, stats)];
        SourceIndex original = new(fingerprint, indexSchema, chunks);

        using MappedSourceIndexSet mapped = WriteAndReopen("stats_string", original);
        SourceIndex restored = mapped.IndexSet.Tables["test"];
        ChunkColumnStatistics restoredStats = restored.Chunks[0].ColumnStatistics["name"];

        Assert.Equal("alice", restoredStats.Minimum.GetValueOrDefault().AsString());
        Assert.Equal("zoe", restoredStats.Maximum.GetValueOrDefault().AsString());
    }

    // ────────────────────── Bloom filters ──────────────────────

    [Fact]
    public void RoundTrip_BloomFilters_PreservesMembership()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        Schema schema = new([new ColumnInfo("id", DataKind.Float32, nullable: false)]);
        IndexSchema indexSchema = new(schema, 200);

        Dictionary<string, ChunkColumnStatistics> stats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = new ChunkColumnStatistics(
                DataValue.FromFloat32(1.0f), DataValue.FromFloat32(100.0f),
                NullCount: 0, RowCount: 100, EstimatedCardinality: 100)
        };

        List<IndexChunk> chunks =
        [
            new IndexChunk(0, 100, stats),
            new IndexChunk(100, 100, stats),
        ];

        BloomFilter filter0 = new(expectedElements: 100);
        filter0.Add(DataValue.FromFloat32(1.0f), _store);
        filter0.Add(DataValue.FromFloat32(50.0f), _store);

        BloomFilter filter1 = new(expectedElements: 100);
        filter1.Add(DataValue.FromFloat32(51.0f), _store);
        filter1.Add(DataValue.FromFloat32(100.0f), _store);

        Dictionary<string, BloomFilter[]> bloomFilters = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = [filter0, filter1]
        };

        BloomFilterSet bloomFilterSet = new(bloomFilters, chunkCount: 2);
        SourceIndex original = new(fingerprint, indexSchema, chunks, bloomFilterSet);

        using MappedSourceIndexSet mapped = WriteAndReopen("bloom", original);
        SourceIndex restored = mapped.IndexSet.Tables["test"];

        Assert.NotNull(restored.BloomFilters);
        Assert.True(restored.BloomFilters.HasColumn("id"));
        Assert.Equal(2, restored.BloomFilters.ChunkCount);

        Assert.True(restored.BloomFilters.TryGetFilter("id", 0, out BloomFilter? restoredFilter0));
        Assert.True(restoredFilter0!.MayContain(DataValue.FromFloat32(1.0f), _store));
        Assert.True(restoredFilter0.MayContain(DataValue.FromFloat32(50.0f), _store));
        Assert.False(restoredFilter0.MayContain(DataValue.FromFloat32(999.0f), _store));

        Assert.True(restored.BloomFilters.TryGetFilter("id", 1, out BloomFilter? restoredFilter1));
        Assert.True(restoredFilter1!.MayContain(DataValue.FromFloat32(51.0f), _store));
    }

    // ────────────────────── Sorted indexes ──────────────────────

    // Sorted-index round-trip tests were removed together with the heap-backed
    // SortedValueIndexSet + WriteSortedIndexes(SortedValueIndexSet) path.
    // Coverage for the surviving streaming path (SortedIndexSpillWriter →
    // WriteStreamedSortedIndexes → SortedIndex) lives in the builder tests.

    // PR13d (v8): per-column B+Tree indexes are no longer carried in the
    // unified `.datum-index` sidecar. Round-trip tests for that path were
    // retired alongside the BTreePages section.

    // ────────────────────── Bitmap indexes ──────────────────────

    [Fact]
    public void RoundTrip_BitmapIndexes_PreservesCompressedBitmaps()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        Schema schema = new([new ColumnInfo("color", DataKind.String, nullable: false)]);
        IndexSchema indexSchema = new(schema, 100);

        Dictionary<string, ChunkColumnStatistics> stats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["color"] = new ChunkColumnStatistics(
                DataValue.FromString("blue"), DataValue.FromString("red"),
                NullCount: 0, RowCount: 100, EstimatedCardinality: 2)
        };

        List<IndexChunk> chunks = [new IndexChunk(0, 100, stats)];

        // Create simple compressed bitmaps (just raw bytes for testing).
        byte[] redBitmap = [0xFF, 0x00, 0xFF];
        byte[] blueBitmap = [0x00, 0xFF, 0x00];

        Dictionary<DataValue, byte[][]> compressedBitmaps = new()
        {
            [DataValue.FromString("red")] = [redBitmap],
            [DataValue.FromString("blue")] = [blueBitmap],
        };

        BitmapColumnIndex bitmapColumn = new(compressedBitmaps, chunkCount: 1, [100]);
        Dictionary<string, BitmapColumnIndex> bitmapIndexes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["color"] = bitmapColumn
        };

        BitmapIndexSet bitmapSet = new(bitmapIndexes);
        SourceIndex original = new(fingerprint, indexSchema, chunks,
            bloomFilters: null,
            bitmapIndexes: bitmapSet);

        using MappedSourceIndexSet mapped = WriteAndReopen("bitmap", original);
        SourceIndex restored = mapped.IndexSet.Tables["test"];

        Assert.NotNull(restored.BitmapIndexes);
        Assert.True(restored.BitmapIndexes.TryGetIndex("color", out BitmapColumnIndex? restoredBitmap));
        Assert.Equal(2, restoredBitmap.DistinctValues.Count);
        Assert.Equal(1, restoredBitmap.ChunkCount);

        IReadOnlyDictionary<DataValue, byte[][]> restoredCompressed = restoredBitmap.CompressedBitmaps;
        Assert.Equal(redBitmap, restoredCompressed[DataValue.FromString("red")][0]);
        Assert.Equal(blueBitmap, restoredCompressed[DataValue.FromString("blue")][0]);
    }

    // ────────────────────── Multi-table ──────────────────────

    [Fact]
    public void RoundTrip_MultipleTables_PreservesAllTables()
    {
        SourceFingerprint fingerprint = new(42, new byte[32]);

        Schema schemaA = new([new ColumnInfo("id", DataKind.Int32, nullable: false)]);
        IndexSchema indexSchemaA = new(schemaA, 100);
        SourceIndex indexA = new(fingerprint, indexSchemaA, Array.Empty<IndexChunk>());

        Schema schemaB = new([
            new ColumnInfo("key", DataKind.String, nullable: false),
            new ColumnInfo("value", DataKind.Float64, nullable: true),
        ]);
        IndexSchema indexSchemaB = new(schemaB, 500);
        SourceIndex indexB = new(fingerprint, indexSchemaB, Array.Empty<IndexChunk>());

        Dictionary<string, SourceIndex> tables = new(StringComparer.Ordinal)
        {
            ["alpha"] = indexA,
            ["beta"] = indexB,
        };

        SourceIndexSet indexSet = new(fingerprint, tables);

        string filePath = Path.Combine(_tempDirectory, "multi_table.datum-index");

        using (FileStream stream = new(filePath, FileMode.Create, FileAccess.ReadWrite))
        {
            UnifiedIndexWriter.Write(indexSet, stream);
        }

        using MappedSourceIndexSet mapped = UnifiedIndexReader.Open(filePath);

        Assert.Equal(2, mapped.IndexSet.Tables.Count);
        Assert.True(mapped.IndexSet.Tables.ContainsKey("alpha"));
        Assert.True(mapped.IndexSet.Tables.ContainsKey("beta"));

        Assert.Equal(100, mapped.IndexSet.Tables["alpha"].Schema.TotalRowCount);
        Assert.Single(mapped.IndexSet.Tables["alpha"].Schema.Schema.Columns);

        Assert.Equal(500, mapped.IndexSet.Tables["beta"].Schema.TotalRowCount);
        Assert.Equal(2, mapped.IndexSet.Tables["beta"].Schema.Schema.Columns.Count);
    }

    // ────────────────────── Combined sections ──────────────────────

    [Fact]
    public void RoundTrip_AllSections_CombinedIntegration()
    {
        SourceFingerprint fingerprint = new(999, new byte[32]);
        Schema schema = new([
            new ColumnInfo("id", DataKind.Int32, nullable: false),
            new ColumnInfo("name", DataKind.String, nullable: true),
        ]);
        IndexSchema indexSchema = new(schema, 200);

        Dictionary<string, ChunkColumnStatistics> stats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = new ChunkColumnStatistics(
                DataValue.FromInt32(1), DataValue.FromInt32(100),
                NullCount: 0, RowCount: 100, EstimatedCardinality: 100),
            ["name"] = new ChunkColumnStatistics(
                DataValue.FromString("alice"), DataValue.FromString("zoe"),
                NullCount: 5, RowCount: 100, EstimatedCardinality: 50)
        };

        List<IndexChunk> chunks =
        [
            new IndexChunk(0, 100, stats),
            new IndexChunk(100, 100, stats),
        ];

        // Bloom filters.
        BloomFilter idBloom0 = new(expectedElements: 100);
        idBloom0.Add(DataValue.FromInt32(42), _store);
        BloomFilter idBloom1 = new(expectedElements: 100);

        Dictionary<string, BloomFilter[]> bloomFilters = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = [idBloom0, idBloom1],
        };

        BloomFilterSet bloomFilterSet = new(bloomFilters, chunkCount: 2);
        SourceIndex original = new(fingerprint, indexSchema, chunks, bloomFilterSet);

        using MappedSourceIndexSet mapped = WriteAndReopen("combined", original);
        SourceIndex restored = mapped.IndexSet.Tables["test"];

        Assert.Equal(999, restored.Fingerprint.FileSize);
        Assert.Equal(200, restored.Schema.TotalRowCount);
        Assert.Equal(2, restored.Schema.Schema.Columns.Count);
        Assert.Equal(2, restored.Chunks.Count);
        Assert.Equal(100, restored.Chunks[0].RowCount);
        Assert.True(restored.Chunks[0].ColumnStatistics.ContainsKey("id"));
        Assert.Equal(1, restored.Chunks[0].ColumnStatistics["id"].Minimum.GetValueOrDefault().AsInt32());
        Assert.NotNull(restored.BloomFilters);
        Assert.True(restored.BloomFilters.TryGetFilter("id", 0, out BloomFilter? restoredBloom));
        Assert.True(restoredBloom!.MayContain(DataValue.FromInt32(42), _store));
    }

    // ────────────────────── Error handling ──────────────────────

    [Fact]
    public void Open_InvalidMagic_ThrowsInvalidDataException()
    {
        string filePath = Path.Combine(_tempDirectory, "bad_magic.datum-index");
        File.WriteAllBytes(filePath, new byte[64]);

        Assert.Throws<InvalidDataException>(() => UnifiedIndexReader.Open(filePath));
    }

    [Fact]
    public void Open_WrongVersion_ThrowsInvalidDataException()
    {
        string filePath = Path.Combine(_tempDirectory, "bad_version.datum-index");

        using (FileStream stream = new(filePath, FileMode.Create))
        using (BinaryWriter writer = new(stream))
        {
            writer.Write("DXIX"u8);
            writer.Write(99); // Bad version.
            writer.Write(0); // Flags.
            writer.Write(0); // Section count.
            writer.Write(0L); // File length.
        }

        Assert.Throws<InvalidDataException>(() => UnifiedIndexReader.Open(filePath));
    }

    // ────────────────────── Helpers ──────────────────────

    private MappedSourceIndexSet WriteAndReopen(string testName, SourceIndex index)
    {
        SourceIndexSet indexSet = SourceIndexSet.Create("test", index);
        string filePath = Path.Combine(_tempDirectory, testName + ".datum-index");

        using (FileStream stream = new(filePath, FileMode.Create, FileAccess.ReadWrite))
        {
            UnifiedIndexWriter.Write(indexSet, stream);
        }

        return UnifiedIndexReader.Open(filePath);
    }

    private static SourceIndex BuildIndexWithChunk(DataKind kind, ChunkColumnStatistics statistics)
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        Schema schema = new([new ColumnInfo("value", kind, nullable: true)]);
        IndexSchema indexSchema = new(schema, 100);

        Dictionary<string, ChunkColumnStatistics> stats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["value"] = statistics
        };

        List<IndexChunk> chunks = [new IndexChunk(0, 100, stats)];
        return new SourceIndex(fingerprint, indexSchema, chunks);
    }

    // PR13d (v8): BuildBPlusTree helper retired alongside the BTreePages
    // section. Per-column tree round-trip coverage lives in MutableBPlusTreeTests.
}
