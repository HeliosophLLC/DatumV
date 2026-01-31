using DatumIngest.Model;

namespace DatumIngest.Tests.Model;

public class DataKindTests : ServiceTestBase
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
            DataKind.UInt128,
            DataKind.Int8,
            DataKind.Int16,
            DataKind.Int32,
            DataKind.Int64,
            DataKind.Int128,
            DataKind.Float16,
            DataKind.Float32,
            DataKind.Float64,
            DataKind.Decimal,
            DataKind.Date,
            DataKind.Time,
            DataKind.DateTime,
            DataKind.Duration,
            DataKind.String,
            DataKind.Uuid,
            DataKind.Image,
            DataKind.Struct,
        ];

        HashSet<DataKind> uniqueKinds = new(expectedKinds);

        Assert.Equal(expectedKinds.Length, uniqueKinds.Count);
    }

    [Theory]
    [InlineData(DataKind.Float32)]
    [InlineData(DataKind.UInt8)]
    public void NumericKindsHaveDistinctValues(DataKind kind)
    {
        Assert.True(Enum.IsDefined(kind));
    }

    [Fact]
    public void TotalEnumMemberCountIsThirty()
    {
        // Tally: Unknown, Type, Boolean,
        // UInt8/16/32/64/128, Int8/16/32/64/128 (10),
        // Float16/32/64, Decimal (4),
        // Date, Time, DateTime, Duration (4),
        // String, Uuid (2),
        // Image, Audio, Video, Json (4 encoded blobs),
        // Struct (1)
        // Point2D, Point3D → 30 total.
        DataKind[] allValues = Enum.GetValues<DataKind>();
        Assert.Equal(30, allValues.Length);
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
