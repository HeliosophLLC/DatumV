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
            DataKind.Float32,
            DataKind.Vector,
            DataKind.Matrix,
            DataKind.Tensor,
            DataKind.UInt8,
            DataKind.UInt8Array,
            DataKind.Image,
            DataKind.String,
            DataKind.Date,
            DataKind.DateTime,
            DataKind.JsonValue,
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
    public void TotalEnumMemberCountIsTwentyFive()
    {
        DataKind[] allValues = Enum.GetValues<DataKind>();
        Assert.Equal(25, allValues.Length);
    }
}
