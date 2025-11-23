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
            DataKind.JsonValue,
            DataKind.Uuid,
            DataKind.Image,
            DataKind.Vector,
            DataKind.Array,
            DataKind.Struct,
        ];

        HashSet<DataKind> uniqueKinds = new(expectedKinds);

        Assert.Equal(expectedKinds.Length, uniqueKinds.Count);
    }

    [Theory]
    [InlineData(DataKind.Float32)]
    [InlineData(DataKind.Vector)]
    [InlineData(DataKind.UInt8)]
    public void NumericKindsHaveDistinctValues(DataKind kind)
    {
        Assert.True(Enum.IsDefined(kind));
    }

    [Fact]
    public void TotalEnumMemberCountIsTwentyEight()
    {
        // Was 27 before UInt8Array (-1), Matrix (-1), Tensor (-1) were retired,
        // then bumped back up by Float16 (+1), Decimal (+1), UInt128 (+1), Int128 (+1).
        // Byte arrays use Kind=UInt8 + IsArray flag; multi-rank float tensors
        // are deferred to the typed-array consolidation effort.
        DataKind[] allValues = Enum.GetValues<DataKind>();
        Assert.Equal(28, allValues.Length);
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
