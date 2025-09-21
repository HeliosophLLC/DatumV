using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Tests.Indexing;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Round-trip tests for <see cref="RowSerializer"/>, verifying that every
/// <see cref="DataKind"/> plus null values survive binary serialization.
/// </summary>
public class RowSerializerTests
{
    /// <summary>
    /// Every <see cref="DataKind"/> must survive a write→read round-trip through
    /// <see cref="RowSerializer.WriteDataValue"/> and <see cref="RowSerializer.ReadDataValue"/>.
    /// Uses the shared <see cref="IndexWriterRoundTripTests.CreateSampleValue"/> factory.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllDataKinds))]
    public void WriteDataValue_RoundTrips(DataKind kind)
    {
        DataValue original = IndexWriterRoundTripTests.CreateSampleValue(kind);
        AssertSingleValueRoundTrip(original);
    }

    /// <summary>
    /// Null values of every <see cref="DataKind"/> must round-trip, preserving the kind tag.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllDataKinds))]
    public void WriteDataValue_Null_RoundTrips(DataKind kind)
    {
        AssertSingleValueRoundTrip(DataValue.Null(kind));
    }

    /// <summary>
    /// Guards against a new <see cref="DataKind"/> being added without updating the
    /// serializer or test data. If this fails, add the new kind to
    /// <see cref="IndexWriterRoundTripTests.CreateSampleValue"/> and the serializer.
    /// </summary>
    [Fact]
    public void AllDataKinds_AreCoveredByTests()
    {
        HashSet<DataKind> allKinds = new(Enum.GetValues<DataKind>());
        HashSet<DataKind> testedKinds = new(AllDataKinds().Select(args => (DataKind)args[0]));

        HashSet<DataKind> missing = new(allKinds);
        missing.ExceptWith(testedKinds);

        Assert.True(missing.Count == 0,
            $"DataKind values not covered by RowSerializer round-trip tests: {string.Join(", ", missing)}. " +
            "Add them to CreateSampleValue and the serializer.");
    }

    public static IEnumerable<object[]> AllDataKinds() =>
        Enum.GetValues<DataKind>().Select(k => new object[] { k });

    [Fact]
    public void RoundTrip_Scalar()
    {
        AssertSingleValueRoundTrip(DataValue.FromFloat32(3.14f));
    }

    [Fact]
    public void RoundTrip_UInt8()
    {
        AssertSingleValueRoundTrip(DataValue.FromUInt8(255));
    }

    [Fact]
    public void RoundTrip_Int8()
    {
        AssertSingleValueRoundTrip(DataValue.FromInt8(-7));
    }

    [Fact]
    public void RoundTrip_Int16()
    {
        AssertSingleValueRoundTrip(DataValue.FromInt16(-1234));
    }

    [Fact]
    public void RoundTrip_UInt16()
    {
        AssertSingleValueRoundTrip(DataValue.FromUInt16(5678));
    }

    [Fact]
    public void RoundTrip_Int32()
    {
        AssertSingleValueRoundTrip(DataValue.FromInt32(-100_000));
    }

    [Fact]
    public void RoundTrip_UInt32()
    {
        AssertSingleValueRoundTrip(DataValue.FromUInt32(200_000));
    }

    [Fact]
    public void RoundTrip_Int64()
    {
        AssertSingleValueRoundTrip(DataValue.FromInt64(-9_000_000_000L));
    }

    [Fact]
    public void RoundTrip_UInt64()
    {
        AssertSingleValueRoundTrip(DataValue.FromUInt64(18_000_000_000UL));
    }

    [Fact]
    public void RoundTrip_Float64()
    {
        AssertSingleValueRoundTrip(DataValue.FromFloat64(2.718281828));
    }

    [Fact]
    public void RoundTrip_String()
    {
        AssertSingleValueRoundTrip(DataValue.FromString("hello world"));
    }

    [Fact]
    public void RoundTrip_Vector()
    {
        AssertSingleValueRoundTrip(DataValue.FromVector([1.0f, 2.0f, 3.0f]));
    }

    [Fact]
    public void RoundTrip_Matrix()
    {
        AssertSingleValueRoundTrip(DataValue.FromMatrix([1f, 2f, 3f, 4f, 5f, 6f], 2, 3));
    }

    [Fact]
    public void RoundTrip_Tensor()
    {
        AssertSingleValueRoundTrip(DataValue.FromTensor([1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f], [2, 2, 2]));
    }

    [Fact]
    public void RoundTrip_UInt8Array()
    {
        AssertSingleValueRoundTrip(DataValue.FromUInt8Array([0x01, 0x02, 0xFF]));
    }

    [Fact]
    public void RoundTrip_Image()
    {
        AssertSingleValueRoundTrip(DataValue.FromImage([0x89, 0x50, 0x4E, 0x47]));
    }

    [Fact]
    public void RoundTrip_Date()
    {
        AssertSingleValueRoundTrip(DataValue.FromDate(new DateOnly(2026, 3, 25)));
    }

    [Fact]
    public void RoundTrip_DateTime()
    {
        AssertSingleValueRoundTrip(
            DataValue.FromDateTime(new DateTimeOffset(2026, 3, 25, 14, 30, 0, TimeSpan.FromHours(2))));
    }

    [Fact]
    public void RoundTrip_JsonValue()
    {
        AssertSingleValueRoundTrip(DataValue.FromJsonValue("{\"key\":\"value\"}"));
    }

    [Fact]
    public void RoundTrip_Uuid()
    {
        AssertSingleValueRoundTrip(DataValue.FromUuid(Guid.Parse("12345678-1234-1234-1234-123456789abc")));
    }

    [Fact]
    public void RoundTrip_BooleanTrue()
    {
        AssertSingleValueRoundTrip(DataValue.FromBoolean(true));
    }

    [Fact]
    public void RoundTrip_BooleanFalse()
    {
        AssertSingleValueRoundTrip(DataValue.FromBoolean(false));
    }

    [Fact]
    public void RoundTrip_Time()
    {
        AssertSingleValueRoundTrip(DataValue.FromTime(new TimeOnly(14, 30, 45)));
    }

    [Fact]
    public void RoundTrip_Duration()
    {
        AssertSingleValueRoundTrip(DataValue.FromDuration(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void RoundTrip_Array()
    {
        AssertSingleValueRoundTrip(DataValue.FromArray(
            DataKind.Float32, [DataValue.FromFloat32(1f), DataValue.FromFloat32(2f)]));
    }

    [Fact]
    public void RoundTrip_Struct()
    {
        AssertSingleValueRoundTrip(DataValue.FromStruct(2, [DataValue.FromString("a"), DataValue.FromInt32(1)]));
    }

    [Fact]
    public void RoundTrip_NestedArray()
    {
        DataValue inner = DataValue.FromArray(
            DataKind.Int32, [DataValue.FromInt32(10), DataValue.FromInt32(20)]);
        DataValue outer = DataValue.FromArray(DataKind.Array, [inner]);
        AssertSingleValueRoundTrip(outer);
    }

    [Fact]
    public void RoundTrip_NestedStruct()
    {
        DataValue inner = DataValue.FromStruct(1, [DataValue.FromBoolean(true)]);
        DataValue outer = DataValue.FromStruct(2, [DataValue.FromString("x"), inner]);
        AssertSingleValueRoundTrip(outer);
    }

    [Fact]
    public void RoundTrip_MixedNullAndNonNull()
    {
        Row original = new(
            ["id", "name", "score"],
            [DataValue.FromFloat32(1f), DataValue.Null(DataKind.String), DataValue.FromFloat32(99.5f)]);

        Row restored = WriteAndReadSingleRow(original);

        Assert.Equal(3, restored.FieldCount);
        Assert.Equal(1f, restored["id"].AsFloat32());
        Assert.True(restored["name"].IsNull);
        Assert.Equal(DataKind.String, restored["name"].Kind);
        Assert.Equal(99.5f, restored["score"].AsFloat32());
    }

    [Fact]
    public void RoundTrip_MultipleRows_SharedSchema()
    {
        Row[] originals =
        [
            new(["a", "b"], [DataValue.FromFloat32(1f), DataValue.FromString("x")]),
            new(["a", "b"], [DataValue.FromFloat32(2f), DataValue.FromString("y")]),
            new(["a", "b"], [DataValue.FromFloat32(3f), DataValue.FromString("z")]),
        ];

        using MemoryStream stream = new();

        using (BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            RowSerializer.WriteSchema(writer, originals[0]);
            foreach (Row row in originals)
            {
                RowSerializer.WriteRow(writer, row);
            }
        }

        stream.Position = 0;

        using BinaryReader reader = new(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        RowSerializer.ReadSchema(reader, out string[] names, out Dictionary<string, int> nameIndex);

        for (int index = 0; index < originals.Length; index++)
        {
            Row restored = RowSerializer.ReadRow(reader, names, nameIndex);
            Assert.Equal(originals[index].FieldCount, restored.FieldCount);

            for (int column = 0; column < restored.FieldCount; column++)
            {
                Assert.Equal(originals[index][column], restored[column]);
            }
        }
    }

    [Fact]
    public void ReadSchema_ProducesCaseInsensitiveNameIndex()
    {
        Row original = new(
            ["MyColumn", "OtherColumn"],
            [DataValue.FromFloat32(1f), DataValue.FromFloat32(2f)]);

        using MemoryStream stream = new();

        using (BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            RowSerializer.WriteSchema(writer, original);
            RowSerializer.WriteRow(writer, original);
        }

        stream.Position = 0;

        using BinaryReader reader = new(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        RowSerializer.ReadSchema(reader, out string[] names, out Dictionary<string, int> nameIndex);
        Row restored = RowSerializer.ReadRow(reader, names, nameIndex);

        Assert.Equal(1f, restored["mycolumn"].AsFloat32());
        Assert.Equal(2f, restored["OTHERCOLUMN"].AsFloat32());
    }

    [Fact]
    public void RoundTrip_AllDataKindsTogether()
    {
        DataKind[] allKinds = Enum.GetValues<DataKind>();
        string[] names = allKinds.Select(k => k.ToString()).ToArray();
        DataValue[] values = allKinds.Select(IndexWriterRoundTripTests.CreateSampleValue).ToArray();

        Row original = new(names, values);

        Row restored = WriteAndReadSingleRow(original);

        Assert.Equal(original.FieldCount, restored.FieldCount);
        for (int index = 0; index < original.FieldCount; index++)
        {
            Assert.Equal(original.ColumnNames[index], restored.ColumnNames[index]);
            Assert.Equal(original[index], restored[index]);
        }
    }

    [Fact]
    public void RoundTrip_EmptyArrays()
    {
        Row original = new(
            ["vec", "bytes"],
            [DataValue.FromVector([]), DataValue.FromUInt8Array([])]);

        Row restored = WriteAndReadSingleRow(original);

        Assert.Empty(restored["vec"].AsVector());
        Assert.Empty(restored["bytes"].AsUInt8Array());
    }

    // ─────────────────────── Helpers ───────────────────────

    private static void AssertSingleValueRoundTrip(DataValue value)
    {
        Row original = new(["col"], [value]);
        Row restored = WriteAndReadSingleRow(original);

        Assert.Equal(1, restored.FieldCount);
        Assert.Equal("col", restored.ColumnNames[0]);
        Assert.Equal(value, restored["col"]);
    }

    private static Row WriteAndReadSingleRow(Row original)
    {
        using MemoryStream stream = new();

        using (BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            RowSerializer.WriteSchema(writer, original);
            RowSerializer.WriteRow(writer, original);
        }

        stream.Position = 0;

        using BinaryReader reader = new(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        RowSerializer.ReadSchema(reader, out string[] names, out Dictionary<string, int> nameIndex);
        return RowSerializer.ReadRow(reader, names, nameIndex);
    }
}
