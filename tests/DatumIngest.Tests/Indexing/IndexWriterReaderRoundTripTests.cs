using DatumIngest.Execution;
using DatumIngest.Indexing;
using DatumIngest.Model;

namespace DatumIngest.Tests.Indexing;

public sealed class IndexWriterReaderRoundTripTests
{
    [Fact]
    public void RoundTrip_EmptyIndex_PreservesStructure()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        Schema schema = new([new ColumnInfo("id", DataKind.Float32, nullable: false)]);
        IndexSchema indexSchema = new(schema, 0);
        SourceIndex original = new(fingerprint, indexSchema, Array.Empty<IndexChunk>());

        SourceIndex restored = WriteAndRead(original);

        Assert.Equal(0, restored.Fingerprint.FileSize);
        Assert.Equal(0, restored.Schema.TotalRowCount);
        Assert.Single(restored.Schema.Schema.Columns);
        Assert.Equal("id", restored.Schema.Schema.Columns[0].Name);
        Assert.Empty(restored.Chunks);
    }

    [Fact]
    public void RoundTrip_Fingerprint_PreservesAllFields()
    {
        byte[] hash = new byte[32];
        Random.Shared.NextBytes(hash);
        SourceFingerprint fingerprint = new(123456789L, hash);
        Schema schema = new([new ColumnInfo("x", DataKind.String, nullable: true)]);
        IndexSchema indexSchema = new(schema, 100);
        SourceIndex original = new(fingerprint, indexSchema, Array.Empty<IndexChunk>());

        SourceIndex restored = WriteAndRead(original);

        Assert.Equal(123456789L, restored.Fingerprint.FileSize);
        Assert.Equal(hash, restored.Fingerprint.StripedHash);
    }

    [Fact]
    public void RoundTrip_Schema_PreservesColumnsAndRowCount()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        Schema schema = new([
            new ColumnInfo("id", DataKind.Float32, nullable: false),
            new ColumnInfo("name", DataKind.String, nullable: true),
            new ColumnInfo("created", DataKind.DateTime, nullable: true),
            new ColumnInfo("data", DataKind.UInt8Array, nullable: false),
        ]);
        IndexSchema indexSchema = new(schema, 50_000);
        SourceIndex original = new(fingerprint, indexSchema, Array.Empty<IndexChunk>());

        SourceIndex restored = WriteAndRead(original);

        Assert.Equal(50_000, restored.Schema.TotalRowCount);
        Assert.Equal(4, restored.Schema.Schema.Columns.Count);
        Assert.Equal("id", restored.Schema.Schema.Columns[0].Name);
        Assert.Equal(DataKind.Float32, restored.Schema.Schema.Columns[0].Kind);
        Assert.False(restored.Schema.Schema.Columns[0].Nullable);
        Assert.Equal("name", restored.Schema.Schema.Columns[1].Name);
        Assert.True(restored.Schema.Schema.Columns[1].Nullable);
        Assert.Equal(DataKind.DateTime, restored.Schema.Schema.Columns[2].Kind);
        Assert.Equal(DataKind.UInt8Array, restored.Schema.Schema.Columns[3].Kind);
    }

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

        Dictionary<string, ChunkColumnStatistics> chunk3Stats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["value"] = new ChunkColumnStatistics(
                DataValue.FromFloat32(150.0f), DataValue.FromFloat32(300.0f),
                NullCount: 10, RowCount: 5_000, EstimatedCardinality: 80)
        };

        List<IndexChunk> chunks =
        [
            new IndexChunk(0, 10_000, -1, -1, chunk1Stats),
            new IndexChunk(10_000, 10_000, -1, -1, chunk2Stats),
            new IndexChunk(20_000, 5_000, -1, -1, chunk3Stats),
        ];

        SourceIndex original = new(fingerprint, indexSchema, chunks);

        SourceIndex restored = WriteAndRead(original);

        Assert.Equal(3, restored.Chunks.Count);

        Assert.Equal(0, restored.Chunks[0].RowOffset);
        Assert.Equal(10_000, restored.Chunks[0].RowCount);
        Assert.Equal(-1, restored.Chunks[0].SourceByteOffset);

        Assert.Equal(10_000, restored.Chunks[1].RowOffset);
        Assert.Equal(10_000, restored.Chunks[1].RowCount);

        Assert.Equal(20_000, restored.Chunks[2].RowOffset);
        Assert.Equal(5_000, restored.Chunks[2].RowCount);
    }

    [Fact]
    public void RoundTrip_ChunkColumnStatistics_PreservesMinMaxNullCardinality()
    {
        SourceIndex original = BuildIndexWithChunk(new ChunkColumnStatistics(
            DataValue.FromFloat32(1.5f), DataValue.FromFloat32(99.9f),
            NullCount: 3, RowCount: 100, EstimatedCardinality: 42));

        SourceIndex restored = WriteAndRead(original);

        ChunkColumnStatistics stats = restored.Chunks[0].ColumnStatistics["value"];
        Assert.Equal(1.5f, stats.Minimum!.AsFloat32());
        Assert.Equal(99.9f, stats.Maximum!.AsFloat32());
        Assert.Equal(3, stats.NullCount);
        Assert.Equal(100, stats.RowCount);
        Assert.Equal(42, stats.EstimatedCardinality);
    }

    [Fact]
    public void RoundTrip_NullMinMax_PreservesCorrectly()
    {
        SourceIndex original = BuildIndexWithChunk(new ChunkColumnStatistics(
            null, null, NullCount: 100, RowCount: 100, EstimatedCardinality: 0));

        SourceIndex restored = WriteAndRead(original);

        ChunkColumnStatistics stats = restored.Chunks[0].ColumnStatistics["value"];
        Assert.Null(stats.Minimum);
        Assert.Null(stats.Maximum);
        Assert.Equal(100, stats.NullCount);
    }

    [Theory]
    [MemberData(nameof(DataValueRoundTripCases))]
    public void RoundTrip_DataValue_AllKinds(DataValue value, string description)
    {
        _ = description; // Used for test display only.

        SourceIndex original = BuildIndexWithChunk(new ChunkColumnStatistics(
            value, value, NullCount: 0, RowCount: 1, EstimatedCardinality: 1));

        SourceIndex restored = WriteAndRead(original);

        ChunkColumnStatistics stats = restored.Chunks[0].ColumnStatistics["value"];
        AssertDataValueEqual(value, stats.Minimum!);
        AssertDataValueEqual(value, stats.Maximum!);
    }

    public static TheoryData<DataValue, string> DataValueRoundTripCases => new()
    {
        { DataValue.FromFloat32(3.14f), "Float32" },
        { DataValue.FromUInt8(42), "UInt8" },
        { DataValue.FromString("hello world"), "String" },
        { DataValue.FromDate(new DateOnly(2024, 6, 15)), "Date" },
        { DataValue.FromDateTime(new DateTimeOffset(2024, 6, 15, 12, 30, 0, TimeSpan.Zero)), "DateTime" },
        { DataValue.FromJsonValue("{\"key\":\"value\"}"), "JsonValue" },
        { DataValue.FromUInt8Array([1, 2, 3, 4, 5]), "UInt8Array" },
        { DataValue.FromVector([1.0f, 2.0f, 3.0f]), "Vector" },
        { DataValue.FromMatrix([1.0f, 2.0f, 3.0f, 4.0f], 2, 2), "Matrix" },
        { DataValue.FromTensor([1.0f, 2.0f, 3.0f, 4.0f, 5.0f, 6.0f], [2, 3]), "Tensor" },
        { DataValue.FromImage([0xFF, 0xD8, 0xFF, 0xE0]), "Image" },
    };

    [Fact]
    public void RoundTrip_MultipleColumns_PreservesAll()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        Schema schema = new([
            new ColumnInfo("id", DataKind.Float32, nullable: false),
            new ColumnInfo("name", DataKind.String, nullable: true),
        ]);
        IndexSchema indexSchema = new(schema, 100);

        Dictionary<string, ChunkColumnStatistics> stats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = new ChunkColumnStatistics(
                DataValue.FromFloat32(1.0f), DataValue.FromFloat32(100.0f),
                NullCount: 0, RowCount: 100, EstimatedCardinality: 100),
            ["name"] = new ChunkColumnStatistics(
                DataValue.FromString("alice"), DataValue.FromString("zoe"),
                NullCount: 5, RowCount: 100, EstimatedCardinality: 50)
        };

        List<IndexChunk> chunks = [new IndexChunk(0, 100, -1, -1, stats)];
        SourceIndex original = new(fingerprint, indexSchema, chunks);

        SourceIndex restored = WriteAndRead(original);

        Assert.Equal(2, restored.Chunks[0].ColumnStatistics.Count);
        Assert.True(restored.Chunks[0].ColumnStatistics.ContainsKey("id"));
        Assert.True(restored.Chunks[0].ColumnStatistics.ContainsKey("name"));
        Assert.Equal("alice", restored.Chunks[0].ColumnStatistics["name"].Minimum!.AsString());
    }

    [Fact]
    public void RoundTrip_ByteOffsets_PreservedWhenSet()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        Schema schema = new([new ColumnInfo("x", DataKind.Float32, nullable: false)]);
        IndexSchema indexSchema = new(schema, 100);

        Dictionary<string, ChunkColumnStatistics> stats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["x"] = new ChunkColumnStatistics(
                DataValue.FromFloat32(1.0f), DataValue.FromFloat32(10.0f),
                NullCount: 0, RowCount: 100, EstimatedCardinality: 10)
        };

        List<IndexChunk> chunks = [new IndexChunk(0, 100, 1024, 8192, stats)];
        SourceIndex original = new(fingerprint, indexSchema, chunks);

        SourceIndex restored = WriteAndRead(original);

        Assert.Equal(1024, restored.Chunks[0].SourceByteOffset);
        Assert.Equal(8192, restored.Chunks[0].SourceByteLength);
    }

    [Fact]
    public void Read_InvalidMagic_ThrowsInvalidDataException()
    {
        using MemoryStream stream = new([0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

        IndexReader reader = new();
        Assert.Throws<InvalidDataException>(() => reader.Read(stream));
    }

    [Fact]
    public void Read_UnsupportedVersion_ThrowsInvalidDataException()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        // Write valid magic but version 99.
        writer.Write("DTIX"u8);
        writer.Write((ushort)99);
        writer.Write((ushort)0);
        writer.Write(0L);
        writer.Flush();

        stream.Position = 0;
        IndexReader reader = new();
        Assert.Throws<InvalidDataException>(() => reader.Read(stream));
    }

    // ───────────── Helpers ─────────────

    private static SourceIndex WriteAndRead(SourceIndex index)
    {
        using MemoryStream stream = new();
        IndexWriter writer = new();
        SourceIndexSet indexSet = SourceIndexSet.Create("test", index);
        writer.Write(indexSet, stream);

        stream.Position = 0;
        IndexReader reader = new();
        SourceIndexSet restoredSet = reader.Read(stream);
        return restoredSet.Tables["test"];
    }

    private static SourceIndex BuildIndexWithChunk(ChunkColumnStatistics statistics)
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        Schema schema = new([new ColumnInfo("value", DataKind.Float32, nullable: true)]);
        IndexSchema indexSchema = new(schema, 100);

        Dictionary<string, ChunkColumnStatistics> stats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["value"] = statistics
        };

        List<IndexChunk> chunks = [new IndexChunk(0, 100, -1, -1, stats)];
        return new SourceIndex(fingerprint, indexSchema, chunks);
    }

    private static void AssertDataValueEqual(DataValue expected, DataValue actual)
    {
        Assert.Equal(expected.Kind, actual.Kind);

        switch (expected.Kind)
        {
            case DataKind.Float32:
                Assert.Equal(expected.AsFloat32(), actual.AsFloat32());
                break;
            case DataKind.UInt8:
                Assert.Equal(expected.AsUInt8(), actual.AsUInt8());
                break;
            case DataKind.String:
                Assert.Equal(expected.AsString(), actual.AsString());
                break;
            case DataKind.Date:
                Assert.Equal(expected.AsDate(), actual.AsDate());
                break;
            case DataKind.DateTime:
                Assert.Equal(expected.AsDateTime(), actual.AsDateTime());
                break;
            case DataKind.JsonValue:
                Assert.Equal(expected.AsJsonValue(), actual.AsJsonValue());
                break;
            case DataKind.UInt8Array:
                Assert.Equal(expected.AsUInt8Array(), actual.AsUInt8Array());
                break;
            case DataKind.Vector:
                Assert.Equal(expected.AsVector(), actual.AsVector());
                break;
            case DataKind.Matrix:
                float[] expectedMatrix = expected.AsMatrix(out int expectedRows, out int expectedColumns);
                float[] actualMatrix = actual.AsMatrix(out int actualRows, out int actualColumns);
                Assert.Equal(expectedRows, actualRows);
                Assert.Equal(expectedColumns, actualColumns);
                Assert.Equal(expectedMatrix, actualMatrix);
                break;
            case DataKind.Tensor:
                float[] expectedTensor = expected.AsTensor(out int[] expectedShape);
                float[] actualTensor = actual.AsTensor(out int[] actualShape);
                Assert.Equal(expectedShape, actualShape);
                Assert.Equal(expectedTensor, actualTensor);
                break;
            case DataKind.Image:
                Assert.Equal(expected.AsImage(), actual.AsImage());
                break;
        }
    }

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
            new IndexChunk(0, 100, -1, -1, stats),
            new IndexChunk(100, 100, -1, -1, stats),
        ];

        BloomFilter filter0 = new(expectedElements: 100);
        filter0.Add(DataValue.FromFloat32(1.0f));
        filter0.Add(DataValue.FromFloat32(50.0f));

        BloomFilter filter1 = new(expectedElements: 100);
        filter1.Add(DataValue.FromFloat32(51.0f));
        filter1.Add(DataValue.FromFloat32(100.0f));

        Dictionary<string, BloomFilter[]> bloomFilters = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = [filter0, filter1]
        };

        BloomFilterSet bloomFilterSet = new(bloomFilters, chunkCount: 2);
        SourceIndex original = new(fingerprint, indexSchema, chunks, bloomFilterSet);

        SourceIndex restored = WriteAndRead(original);

        Assert.NotNull(restored.BloomFilters);
        Assert.True(restored.BloomFilters.HasColumn("id"));
        Assert.Equal(2, restored.BloomFilters.ChunkCount);

        Assert.True(restored.BloomFilters.TryGetFilter("id", 0, out BloomFilter? restoredFilter0));
        Assert.True(restoredFilter0!.MayContain(DataValue.FromFloat32(1.0f)));
        Assert.True(restoredFilter0.MayContain(DataValue.FromFloat32(50.0f)));
        Assert.False(restoredFilter0.MayContain(DataValue.FromFloat32(999.0f)));

        Assert.True(restored.BloomFilters.TryGetFilter("id", 1, out BloomFilter? restoredFilter1));
        Assert.True(restoredFilter1!.MayContain(DataValue.FromFloat32(51.0f)));
        Assert.True(restoredFilter1.MayContain(DataValue.FromFloat32(100.0f)));
    }

    [Fact]
    public void RoundTrip_NoBloomFilters_PreservesNull()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        Schema schema = new([new ColumnInfo("id", DataKind.Float32, nullable: false)]);
        IndexSchema indexSchema = new(schema, 10);
        SourceIndex original = new(fingerprint, indexSchema, Array.Empty<IndexChunk>());

        SourceIndex restored = WriteAndRead(original);

        Assert.Null(restored.BloomFilters);
    }

    [Fact]
    public void RoundTrip_BloomFilters_MultipleColumns()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        Schema schema = new([
            new ColumnInfo("id", DataKind.Float32, nullable: false),
            new ColumnInfo("name", DataKind.String, nullable: true),
        ]);
        IndexSchema indexSchema = new(schema, 100);

        Dictionary<string, ChunkColumnStatistics> stats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = new ChunkColumnStatistics(
                DataValue.FromFloat32(1.0f), DataValue.FromFloat32(50.0f),
                NullCount: 0, RowCount: 100, EstimatedCardinality: 50),
            ["name"] = new ChunkColumnStatistics(
                DataValue.FromString("alice"), DataValue.FromString("zoe"),
                NullCount: 0, RowCount: 100, EstimatedCardinality: 40)
        };

        List<IndexChunk> chunks = [new IndexChunk(0, 100, -1, -1, stats)];

        BloomFilter idFilter = new(expectedElements: 100);
        idFilter.Add(DataValue.FromFloat32(42.0f));

        BloomFilter nameFilter = new(expectedElements: 100);
        nameFilter.Add(DataValue.FromString("alice"));

        Dictionary<string, BloomFilter[]> bloomFilters = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = [idFilter],
            ["name"] = [nameFilter]
        };

        BloomFilterSet bloomFilterSet = new(bloomFilters, chunkCount: 1);
        SourceIndex original = new(fingerprint, indexSchema, chunks, bloomFilterSet);

        SourceIndex restored = WriteAndRead(original);

        Assert.NotNull(restored.BloomFilters);
        Assert.Equal(2, restored.BloomFilters.ColumnNames.Count);

        Assert.True(restored.BloomFilters.TryGetFilter("id", 0, out BloomFilter? idResult));
        Assert.True(idResult!.MayContain(DataValue.FromFloat32(42.0f)));

        Assert.True(restored.BloomFilters.TryGetFilter("name", 0, out BloomFilter? nameResult));
        Assert.True(nameResult!.MayContain(DataValue.FromString("alice")));
    }

    [Fact]
    public void RoundTrip_SortedIndexes_PreservesEntries()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        Schema schema = new([
            new ColumnInfo("id", DataKind.Float32, nullable: false),
            new ColumnInfo("name", DataKind.String, nullable: true),
        ]);
        IndexSchema indexSchema = new(schema, 300);

        Dictionary<string, ChunkColumnStatistics> stats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = new ChunkColumnStatistics(
                DataValue.FromFloat32(1.0f), DataValue.FromFloat32(100.0f),
                NullCount: 0, RowCount: 100, EstimatedCardinality: 100),
            ["name"] = new ChunkColumnStatistics(
                DataValue.FromString("alice"), DataValue.FromString("zoe"),
                NullCount: 0, RowCount: 100, EstimatedCardinality: 50)
        };

        List<IndexChunk> chunks =
        [
            new IndexChunk(0, 100, -1, -1, stats),
            new IndexChunk(100, 100, -1, -1, stats),
            new IndexChunk(200, 100, -1, -1, stats),
        ];

        Dictionary<string, SortedValueIndex> sortedIndexes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = SortedValueIndex.BuildFromUnsorted(
            [
                new ValueIndexEntry(DataValue.FromFloat32(50.0f), 1, 10),
                new ValueIndexEntry(DataValue.FromFloat32(1.0f), 0, 0),
                new ValueIndexEntry(DataValue.FromFloat32(100.0f), 2, 99),
            ]),
            ["name"] = SortedValueIndex.BuildFromUnsorted(
            [
                new ValueIndexEntry(DataValue.FromString("bob"), 0, 5),
                new ValueIndexEntry(DataValue.FromString("alice"), 0, 0),
            ]),
        };

        SortedValueIndexSet sortedIndexSet = new(sortedIndexes);
        SourceIndex original = new(fingerprint, indexSchema, chunks, sortedIndexes: sortedIndexSet);

        SourceIndex restored = WriteAndRead(original);

        Assert.NotNull(restored.SortedIndexes);
        Assert.Equal(2, restored.SortedIndexes.Count);
        Assert.True(restored.SortedIndexes.HasColumn("id"));
        Assert.True(restored.SortedIndexes.HasColumn("name"));

        Assert.True(restored.SortedIndexes.TryGetIndex("id", out SortedValueIndex? idIndex));
        Assert.Equal(3, idIndex!.Count);

        // Verify lookup works on deserialized index.
        IReadOnlyList<ValueIndexEntry> found = idIndex.FindExact(DataValue.FromFloat32(50.0f));
        Assert.Single(found);
        Assert.Equal(1, found[0].ChunkIndex);
        Assert.Equal(10, found[0].RowOffsetInChunk);

        Assert.True(restored.SortedIndexes.TryGetIndex("name", out SortedValueIndex? nameIndex));
        IReadOnlyList<ValueIndexEntry> nameFound = nameIndex!.FindExact(DataValue.FromString("alice"));
        Assert.Single(nameFound);
        Assert.Equal(0, nameFound[0].ChunkIndex);
    }

    [Fact]
    public void RoundTrip_NoSortedIndexes_PreservesNull()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        Schema schema = new([new ColumnInfo("id", DataKind.Float32, nullable: false)]);
        IndexSchema indexSchema = new(schema, 10);
        SourceIndex original = new(fingerprint, indexSchema, Array.Empty<IndexChunk>());

        SourceIndex restored = WriteAndRead(original);

        Assert.Null(restored.SortedIndexes);
    }

    [Fact]
    public void RoundTrip_ZipDirectory_PreservesEntries()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        Schema schema = new([new ColumnInfo("x", DataKind.Float32, nullable: false)]);
        IndexSchema indexSchema = new(schema, 10);

        ZipDirectoryEntry[] zipEntries =
        [
            new("data/file1.csv", CompressedSize: 1024, UncompressedSize: 4096, LocalHeaderOffset: 0, Crc32: 0xDEADBEEF),
            new("data/file2.csv", CompressedSize: 2048, UncompressedSize: 8192, LocalHeaderOffset: 4096, Crc32: 0xCAFEBABE),
        ];

        ZipDirectoryCache zipDirectory = new(zipEntries);
        SourceIndex original = new(fingerprint, indexSchema, Array.Empty<IndexChunk>(), zipDirectory: zipDirectory);

        SourceIndex restored = WriteAndRead(original);

        Assert.NotNull(restored.ZipDirectory);
        Assert.Equal(2, restored.ZipDirectory.Count);

        ReadOnlySpan<ZipDirectoryEntry> restoredEntries = restored.ZipDirectory.Entries;
        Assert.Equal("data/file1.csv", restoredEntries[0].FileName);
        Assert.Equal(1024, restoredEntries[0].CompressedSize);
        Assert.Equal(4096, restoredEntries[0].UncompressedSize);
        Assert.Equal(0, restoredEntries[0].LocalHeaderOffset);
        Assert.Equal(0xDEADBEEFu, restoredEntries[0].Crc32);

        Assert.Equal("data/file2.csv", restoredEntries[1].FileName);
        Assert.Equal(2048, restoredEntries[1].CompressedSize);
        Assert.Equal(8192, restoredEntries[1].UncompressedSize);
        Assert.Equal(4096, restoredEntries[1].LocalHeaderOffset);
        Assert.Equal(0xCAFEBABEu, restoredEntries[1].Crc32);
    }

    [Fact]
    public void RoundTrip_NoZipDirectory_PreservesNull()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        Schema schema = new([new ColumnInfo("id", DataKind.Float32, nullable: false)]);
        IndexSchema indexSchema = new(schema, 10);
        SourceIndex original = new(fingerprint, indexSchema, Array.Empty<IndexChunk>());

        SourceIndex restored = WriteAndRead(original);

        Assert.Null(restored.ZipDirectory);
    }

    [Fact]
    public void RoundTrip_AllSections_PreservedTogether()
    {
        SourceFingerprint fingerprint = new(42, new byte[32]);
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
            new IndexChunk(0, 100, -1, -1, stats),
            new IndexChunk(100, 100, -1, -1, stats),
        ];

        // Bloom filters
        BloomFilter filter0 = new(100);
        filter0.Add(DataValue.FromFloat32(1.0f));
        BloomFilter filter1 = new(100);
        filter1.Add(DataValue.FromFloat32(50.0f));
        BloomFilterSet bloomFilterSet = new(
            new Dictionary<string, BloomFilter[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = [filter0, filter1]
            },
            chunkCount: 2);

        // Sorted indexes
        Dictionary<string, SortedValueIndex> sortedIndexes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = SortedValueIndex.BuildFromUnsorted(
            [
                new ValueIndexEntry(DataValue.FromFloat32(1.0f), 0, 0),
                new ValueIndexEntry(DataValue.FromFloat32(50.0f), 1, 25),
            ]),
        };
        SortedValueIndexSet sortedIndexSet = new(sortedIndexes);

        // Zip directory
        ZipDirectoryEntry[] zipEntries = [new("test.csv", 100, 200, 0, 0x12345678)];
        ZipDirectoryCache zipDirectory = new(zipEntries);

        SourceIndex original = new(fingerprint, indexSchema, chunks, bloomFilterSet, sortedIndexSet, zipDirectory);

        SourceIndex restored = WriteAndRead(original);

        Assert.Equal(42, restored.Fingerprint.FileSize);
        Assert.Equal(2, restored.Chunks.Count);
        Assert.NotNull(restored.BloomFilters);
        Assert.NotNull(restored.SortedIndexes);
        Assert.NotNull(restored.ZipDirectory);
        Assert.Equal(1, restored.ZipDirectory.Count);
        Assert.True(restored.SortedIndexes.HasColumn("id"));
    }

    [Fact]
    public void RoundTrip_CompressedSpillWriter_PreservesSortedIndexEntries()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        HashSet<string> indexColumns = ["id"];
        SourceIndexBuilder builder = new(chunkSize: 3, indexColumns: indexColumns);
        IncrementalIndexBuilder incremental = builder.CreateIncrementalBuilder(fingerprint);

        // Add 9 rows across 3 chunks (chunkSize=3) to exercise multi-run merge.
        float[] values = [9.0f, 2.0f, 7.0f, 4.0f, 1.0f, 8.0f, 3.0f, 6.0f, 5.0f];
        foreach (float value in values)
        {
            string[] names = ["id"];
            DataValue[] dataValues = [DataValue.FromFloat32(value)];
            incremental.AddRow(new Row(names, dataValues));
        }

        SourceIndex index = incremental.Finalize();

        // Serialize through the compressed spill writer path.
        using MemoryStream stream = new();
        IndexWriter writer = new();
        SourceIndexSet indexSet = SourceIndexSet.Create("test", index);
        writer.Write(indexSet, stream, incremental.SpillWriter, compressIndexes: true);

        // Read back and verify.
        stream.Position = 0;
        IndexReader reader = new();
        SourceIndexSet restoredSet = reader.Read(stream);
        SourceIndex restored = restoredSet.Tables["test"];

        Assert.NotNull(restored.SortedIndexes);
        Assert.True(restored.SortedIndexes.HasColumn("id"));
        Assert.True(restored.SortedIndexes.TryGetIndex("id", out SortedValueIndex? idIndex));
        Assert.Equal(9, idIndex!.Count);

        // Verify all values survived and lookup works on the round-tripped index.
        foreach (float value in values)
        {
            IReadOnlyList<ValueIndexEntry> found = idIndex.FindExact(DataValue.FromFloat32(value));
            Assert.Single(found);
        }

        // Verify sorted order: first entry should be 1.0, last should be 9.0.
        IReadOnlyList<ValueIndexEntry> first = idIndex.FindExact(DataValue.FromFloat32(1.0f));
        Assert.Equal(1, first[0].ChunkIndex); // rows 3-5 → chunk 1, value 1.0 is the 2nd row in chunk 1
        Assert.Equal(1, first[0].RowOffsetInChunk);

        incremental.Dispose();
    }
}
