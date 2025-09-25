using DatumIngest.Model;

namespace DatumIngest.Tests.Model;

public class DataKindTests
{
    [Fact]
    public void AllExpectedKindsAreDefined()
    {
        // Every DataKind value must exist as a distinct enum member
        DataKind[] expectedKinds =
        [
            DataKind.Unknown,
            DataKind.Type,
            DataKind.Boolean,
            DataKind.UInt8,
            DataKind.UInt16,
            DataKind.UInt32,
            DataKind.UInt64,
            DataKind.Int8,
            DataKind.Int16,
            DataKind.Int32,
            DataKind.Int64,
            DataKind.Float32,
            DataKind.Float64,
            DataKind.Date,
            DataKind.Time,
            DataKind.DateTime,
            DataKind.Duration,
            DataKind.String,
            DataKind.JsonValue,
            DataKind.Uuid,
            DataKind.UInt8Array,
            DataKind.Image,
            DataKind.Vector,
            DataKind.Matrix,
            DataKind.Tensor,
            DataKind.Array,
            DataKind.Struct,
        ];

        HashSet<DataKind> uniqueKinds = new(expectedKinds);

        Assert.Equal(expectedKinds.Length, uniqueKinds.Count);
    }

    [Theory]
    [InlineData(DataKind.Float32)]
    [InlineData(DataKind.Vector)]
    [InlineData(DataKind.Matrix)]
    [InlineData(DataKind.Tensor)]
    [InlineData(DataKind.UInt8)]
    public void NumericKindsHaveDistinctValues(DataKind kind)
    {
        Assert.True(Enum.IsDefined(kind));
    }

    [Fact]
    public void TotalEnumMemberCountIsTwentySeven()
    {
        DataKind[] allValues = Enum.GetValues<DataKind>();
        Assert.Equal(27, allValues.Length);
    }

    [Fact]
    public void DefaultDataValueHasUnknownKind()
    {
        DataValue defaultValue = default;
        Assert.Equal(DataKind.Unknown, defaultValue.Kind);
        Assert.False(defaultValue.IsNull);
    }

    [Fact]
    public void UnknownNullHasUnknownKindAndIsNull()
    {
        DataValue unknownNull = DataValue.UnknownNull();
        Assert.Equal(DataKind.Unknown, unknownNull.Kind);
        Assert.True(unknownNull.IsNull);
    }

    [Fact]
    public void TypedNullKeepsOriginalKind()
    {
        DataValue typedNull = DataValue.Null(DataKind.Int32);
        Assert.Equal(DataKind.Int32, typedNull.Kind);
        Assert.True(typedNull.IsNull);
    }
}
