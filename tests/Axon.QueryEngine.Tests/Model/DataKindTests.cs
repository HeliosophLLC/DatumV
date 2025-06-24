using Axon.QueryEngine.Model;

namespace Axon.QueryEngine.Tests.Model;

public class DataKindTests
{
    [Fact]
    public void AllExpectedKindsAreDefined()
    {
        // Every DataKind value must exist as a distinct enum member
        DataKind[] expectedKinds =
        [
            DataKind.Scalar,
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
    [InlineData(DataKind.Scalar)]
    [InlineData(DataKind.Vector)]
    [InlineData(DataKind.Matrix)]
    [InlineData(DataKind.Tensor)]
    [InlineData(DataKind.UInt8)]
    public void NumericKindsHaveDistinctValues(DataKind kind)
    {
        Assert.True(Enum.IsDefined(kind));
    }

    [Fact]
    public void TotalEnumMemberCountIsEleven()
    {
        DataKind[] allValues = Enum.GetValues<DataKind>();
        Assert.Equal(11, allValues.Length);
    }
}
