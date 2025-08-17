using DatumIngest.Execution;
using DatumIngest.Model;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Round-trip tests for <see cref="RowSerializer"/>, verifying that every
/// <see cref="DataKind"/> plus null values survive binary serialization.
/// </summary>
public class RowSerializerTests
{
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

    [Theory]
    [InlineData(DataKind.Float32)]
    [InlineData(DataKind.UInt8)]
    [InlineData(DataKind.String)]
    [InlineData(DataKind.Vector)]
    [InlineData(DataKind.Matrix)]
    [InlineData(DataKind.Tensor)]
    [InlineData(DataKind.UInt8Array)]
    [InlineData(DataKind.Image)]
    [InlineData(DataKind.Date)]
    [InlineData(DataKind.DateTime)]
    [InlineData(DataKind.JsonValue)]
    [InlineData(DataKind.Uuid)]
    [InlineData(DataKind.Boolean)]
    [InlineData(DataKind.Time)]
    [InlineData(DataKind.Duration)]
    public void RoundTrip_NullValue(DataKind kind)
    {
        AssertSingleValueRoundTrip(DataValue.Null(kind));
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
        Row original = new(
            [
                "float32", "uint8", "string", "vector", "matrix", "tensor",
                "uint8array", "image", "date", "datetime", "json", "uuid",
                "boolean", "time", "duration"
            ],
            [
                DataValue.FromFloat32(42f),
                DataValue.FromUInt8(7),
                DataValue.FromString("test"),
                DataValue.FromVector([1f, 2f]),
                DataValue.FromMatrix([1f, 2f, 3f, 4f], 2, 2),
                DataValue.FromTensor([1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f], [2, 2, 2]),
                DataValue.FromUInt8Array([0xAB, 0xCD]),
                DataValue.FromImage([0x89, 0x50]),
                DataValue.FromDate(new DateOnly(2026, 1, 1)),
                DataValue.FromDateTime(new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero)),
                DataValue.FromJsonValue("[1,2,3]"),
                DataValue.FromUuid(Guid.Empty),
                DataValue.FromBoolean(true),
                DataValue.FromTime(new TimeOnly(8, 0, 0)),
                DataValue.FromDuration(TimeSpan.FromSeconds(90)),
            ]);

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
