using DatumIngest.Indexing;
using DatumIngest.Model;

namespace DatumIngest.Tests.Indexing;

public sealed class IndexWriterReaderRoundTripTests
{
    [Fact]
    public void RoundTrip_EmptyIndex_PreservesStructure()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        Schema schema = new([new ColumnInfo("id", DataKind.Scalar, nullable: false)]);
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
            new ColumnInfo("id", DataKind.Scalar, nullable: false),
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
        Assert.Equal(DataKind.Scalar, restored.Schema.Schema.Columns[0].Kind);
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
        Schema schema = new([new ColumnInfo("value", DataKind.Scalar, nullable: true)]);
        IndexSchema indexSchema = new(schema, 25_000);

        Dictionary<string, ChunkColumnStatistics> chunk1Stats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["value"] = new ChunkColumnStatistics(
                DataValue.FromScalar(1.0f), DataValue.FromScalar(100.0f),
                NullCount: 5, RowCount: 10_000, EstimatedCardinality: 95)
        };

        Dictionary<string, ChunkColumnStatistics> chunk2Stats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["value"] = new ChunkColumnStatistics(
                DataValue.FromScalar(50.0f), DataValue.FromScalar(200.0f),
                NullCount: 0, RowCount: 10_000, EstimatedCardinality: 150)
        };

        Dictionary<string, ChunkColumnStatistics> chunk3Stats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["value"] = new ChunkColumnStatistics(
                DataValue.FromScalar(150.0f), DataValue.FromScalar(300.0f),
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
            DataValue.FromScalar(1.5f), DataValue.FromScalar(99.9f),
            NullCount: 3, RowCount: 100, EstimatedCardinality: 42));

        SourceIndex restored = WriteAndRead(original);

        ChunkColumnStatistics stats = restored.Chunks[0].ColumnStatistics["value"];
        Assert.Equal(1.5f, stats.Minimum!.AsScalar());
        Assert.Equal(99.9f, stats.Maximum!.AsScalar());
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
        { DataValue.FromScalar(3.14f), "Scalar" },
        { DataValue.FromUInt8(42), "UInt8" },
        { DataValue.FromString("hello world"), "String" },
        { DataValue.FromDate(new DateOnly(2024, 6, 15)), "Date" },
        { DataValue.FromDateTime(new DateTime(2024, 6, 15, 12, 30, 0, DateTimeKind.Utc)), "DateTime" },
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
            new ColumnInfo("id", DataKind.Scalar, nullable: false),
            new ColumnInfo("name", DataKind.String, nullable: true),
        ]);
        IndexSchema indexSchema = new(schema, 100);

        Dictionary<string, ChunkColumnStatistics> stats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = new ChunkColumnStatistics(
                DataValue.FromScalar(1.0f), DataValue.FromScalar(100.0f),
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
        Schema schema = new([new ColumnInfo("x", DataKind.Scalar, nullable: false)]);
        IndexSchema indexSchema = new(schema, 100);

        Dictionary<string, ChunkColumnStatistics> stats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["x"] = new ChunkColumnStatistics(
                DataValue.FromScalar(1.0f), DataValue.FromScalar(10.0f),
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
        writer.Write(index, stream);

        stream.Position = 0;
        IndexReader reader = new();
        return reader.Read(stream);
    }

    private static SourceIndex BuildIndexWithChunk(ChunkColumnStatistics statistics)
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        Schema schema = new([new ColumnInfo("value", DataKind.Scalar, nullable: true)]);
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
            case DataKind.Scalar:
                Assert.Equal(expected.AsScalar(), actual.AsScalar());
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
}
