namespace DatumIngest.Tests;

using DatumIngest.Ingestion.Sampling;
using DatumIngest.Model;

/// <summary>
/// Tests for <see cref="SamplePreviewCollector"/>, covering reservoir sampling,
/// value conversion for all <see cref="DataKind"/> types, and round-trip serialisation.
/// </summary>
public sealed class SamplePreviewCollectorTests : ServiceTestBase
{
    /// <summary>
    /// A fresh arena per test method, used as the <see cref="IValueStore"/> for
    /// reference-type payloads (strings, vectors, images, arrays, structs). Disposed
    /// by xUnit when the test class instance is GC'd.
    /// </summary>
    private readonly Arena _arena = new();


    // ──────────────────── Reservoir sizing ────────────────────

    [Fact]
    public void Build_FewerRowsThanSampleSize_RetainsAllRows()
    {
        ColumnLookup lookup = new(["value"]);
        Schema schema = new([new ColumnInfo("value", DataKind.Float32, nullable: false)]);
        SamplePreviewCollector collector = new(sampleSize: 25);

        for (int i = 0; i < 10; i++)
        {
            collector.Consider(MakeRow(lookup, DataValue.FromFloat32(i)), _arena);
        }

        SamplePreview preview = collector.Build(schema);

        Assert.Equal(10, preview.Samples.Count);
        Assert.Single(preview.Features);
        Assert.Equal("value", preview.Features[0].Name);
        Assert.Equal("float32", preview.Features[0].Kind);
    }

    [Fact]
    public void Build_MoreRowsThanSampleSize_RetainsExactlySampleSize()
    {
        ColumnLookup lookup = new(["value"]);
        Schema schema = new([new ColumnInfo("value", DataKind.Float32, nullable: false)]);
        SamplePreviewCollector collector = new(sampleSize: 5);

        for (int i = 0; i < 100; i++)
        {
            collector.Consider(MakeRow(lookup, DataValue.FromFloat32(i)), _arena);
        }

        SamplePreview preview = collector.Build(schema);

        Assert.Equal(5, preview.Samples.Count);
    }

    // ──────────────────── Value conversion ────────────────────

    [Fact]
    public void ConvertValue_Scalar_ReturnsFloat()
    {
        object? result = SamplePreviewCollector.ConvertValue(DataValue.FromFloat32(42.5f), _arena);

        Assert.Equal(42.5f, result);
    }

    [Fact]
    public void ConvertValue_UInt8_ReturnsByte()
    {
        object? result = SamplePreviewCollector.ConvertValue(DataValue.FromUInt8(200), _arena);

        Assert.Equal((byte)200, result);
    }

    [Fact]
    public void ConvertValue_Boolean_ReturnsBool()
    {
        Assert.Equal(true, SamplePreviewCollector.ConvertValue(DataValue.FromBoolean(true), _arena));
        Assert.Equal(false, SamplePreviewCollector.ConvertValue(DataValue.FromBoolean(false), _arena));
    }

    [Fact]
    public void ConvertValue_String_ReturnsString()
    {
        object? result = SamplePreviewCollector.ConvertValue(DataValue.FromString("hello", _arena), _arena);

        Assert.Equal("hello", result);
    }

    [Fact]
    public void ConvertValue_Null_ReturnsNull()
    {
        Assert.Null(SamplePreviewCollector.ConvertValue(DataValue.Null(DataKind.Float32), _arena));
        Assert.Null(SamplePreviewCollector.ConvertValue(DataValue.Null(DataKind.String), _arena));
        Assert.Null(SamplePreviewCollector.ConvertValue(DataValue.Null(DataKind.Image), _arena));
    }

    [Fact]
    public void ConvertValue_Vector_ReturnsObjectArray()
    {
        float[] vector = [1.0f, 2.0f, 3.0f];
        object? result = SamplePreviewCollector.ConvertValue(DataValue.FromArenaArray<float>(vector, DataKind.Float32, _arena), _arena);

        object[] array = Assert.IsType<object[]>(result);
        Assert.Equal(3, array.Length);
        Assert.Equal(1.0f, array[0]);
        Assert.Equal(2.0f, array[1]);
        Assert.Equal(3.0f, array[2]);
    }

    [Fact]
    public void ConvertValue_UInt8Array_ReturnsSentinel()
    {
        object? result = SamplePreviewCollector.ConvertValue(
            DataValue.FromByteArray([0x00, 0xFF], _arena), _arena);

        Assert.Equal("[binary data]", result);
    }

    [Fact]
    public void ConvertValue_Date_ReturnsIso8601String()
    {
        DateOnly date = new(2025, 3, 15);
        object? result = SamplePreviewCollector.ConvertValue(DataValue.FromDate(date), _arena);

        Assert.Equal("2025-03-15", result);
    }

    [Fact]
    public void ConvertValue_DateTime_ReturnsIso8601String()
    {
        DateTimeOffset dateTime = new(2025, 3, 15, 14, 30, 0, TimeSpan.Zero);
        object? result = SamplePreviewCollector.ConvertValue(DataValue.FromTimestampTz(dateTime), _arena);

        string resultString = Assert.IsType<string>(result);
        Assert.Contains("2025-03-15", resultString);
    }

    [Fact]
    public void ConvertValue_Uuid_ReturnsString()
    {
        Guid uuid = Guid.NewGuid();
        object? result = SamplePreviewCollector.ConvertValue(DataValue.FromUuid(uuid), _arena);

        Assert.Equal(uuid.ToString(), result);
    }

    [Fact]
    public void ConvertValue_Array_ReturnsConvertedElements()
    {
        // Post-PR2: typed Float32 array goes through the AsArraySpan<float>
        // path in SamplePreviewCollector, which returns a boxed float[].
        DataValue array = DataValue.FromArenaArray<float>([1.0f, 2.0f], DataKind.Float32, _arena);
        object? result = SamplePreviewCollector.ConvertValue(array, _arena);

        object?[] converted = Assert.IsType<object?[]>(result);
        Assert.Equal(2, converted.Length);
        Assert.Equal(1.0f, converted[0]);
        Assert.Equal(2.0f, converted[1]);
    }

    // ──────────────────── Feature list ────────────────────

    [Fact]
    public void Build_MultipleColumns_FeaturesMatchSchema()
    {
        Schema schema = new([
            new ColumnInfo("name", DataKind.String, nullable: false),
            new ColumnInfo("score", DataKind.Float32, nullable: false),
            new ColumnInfo("flag", DataKind.Boolean, nullable: true),
        ]);

        ColumnLookup lookup = new(["name", "score", "flag"]);

        SamplePreviewCollector collector = new(sampleSize: 25);
        collector.Consider(MakeRow(lookup,
            DataValue.FromString("Alice", _arena),
            DataValue.FromFloat32(95.5f),
            DataValue.FromBoolean(true)
        ), _arena);

        SamplePreview preview = collector.Build(schema);

        Assert.Equal(3, preview.Features.Count);
        Assert.Equal("name", preview.Features[0].Name);
        Assert.Equal("string", preview.Features[0].Kind);
        Assert.Equal("score", preview.Features[1].Name);
        Assert.Equal("float32", preview.Features[1].Kind);
        Assert.Equal("flag", preview.Features[2].Name);
        Assert.Equal("boolean", preview.Features[2].Kind);

        Assert.Single(preview.Samples);
        Assert.Equal("Alice", preview.Samples[0][0]);
        Assert.Equal(95.5f, preview.Samples[0][1]);
        Assert.Equal(true, preview.Samples[0][2]);
    }

    // ──────────────────── Serialisation round-trip ────────────────────

    [Fact]
    public void Serialize_Deserialize_RoundTrip_PreservesValues()
    {
        Schema schema = new([
            new ColumnInfo("label", DataKind.String, nullable: false),
            new ColumnInfo("score", DataKind.Float32, nullable: false),
            new ColumnInfo("active", DataKind.Boolean, nullable: true),
        ]);

        ColumnLookup lookup = new(["label", "score", "active"]);

        SamplePreviewCollector collector = new(sampleSize: 25);
        collector.Consider(MakeRow(lookup,
            DataValue.FromString("test", _arena),
            DataValue.FromFloat32(1.5f),
            DataValue.FromBoolean(true)
        ), _arena);
        collector.Consider(MakeRow(lookup,
            DataValue.FromString("other", _arena),
            DataValue.FromFloat32(2.0f),
            DataValue.Null(DataKind.Boolean)
        ), _arena);

        SamplePreview original = collector.Build(schema);
        string json = SamplePreviewSerializer.Serialize(original);
        SamplePreview? deserialized = SamplePreviewSerializer.Deserialize(json);

        Assert.NotNull(deserialized);
        Assert.Equal(original.Features.Count, deserialized.Features.Count);
        Assert.Equal(original.Samples.Count, deserialized.Samples.Count);

        for (int i = 0; i < original.Features.Count; i++)
        {
            Assert.Equal(original.Features[i].Name, deserialized.Features[i].Name);
            Assert.Equal(original.Features[i].Kind, deserialized.Features[i].Kind);
        }

        Assert.Equal("test", deserialized.Samples[0][0]);
        Assert.Equal(1.5f, deserialized.Samples[0][1]);
        Assert.Equal(true, deserialized.Samples[0][2]);

        Assert.Equal("other", deserialized.Samples[1][0]);
        Assert.Equal(2.0f, deserialized.Samples[1][1]);
        Assert.Null(deserialized.Samples[1][2]);
    }

    [Fact]
    public void Serialize_Deserialize_Vector_PreservesStructure()
    {
        ColumnLookup lookup = new(["embedding"]);
        Schema schema = new([new ColumnInfo("embedding", DataKind.Float32, nullable: false) { IsArray = true }]);
        SamplePreviewCollector collector = new(sampleSize: 25);
        collector.Consider(
            MakeRow(lookup, DataValue.FromArenaArray<float>([1.0f, 2.0f, 3.0f], DataKind.Float32, _arena)),
            _arena);

        SamplePreview original = collector.Build(schema);
        string json = SamplePreviewSerializer.Serialize(original);
        SamplePreview? deserialized = SamplePreviewSerializer.Deserialize(json);

        Assert.NotNull(deserialized);
        object?[] vectorRow = deserialized.Samples[0];
        object?[] vector = Assert.IsType<object?[]>(vectorRow[0]);
        Assert.Equal(3, vector.Length);
        Assert.Equal(1.0f, vector[0]);
        Assert.Equal(2.0f, vector[1]);
        Assert.Equal(3.0f, vector[2]);
    }
}
