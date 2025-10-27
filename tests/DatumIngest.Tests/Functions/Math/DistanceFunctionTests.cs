using DatumIngest.Functions.Math;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Math;

public class DistanceFunctionTests : ServiceTestBase
{
    [Fact]
    public void CosineSimilarity_Identical()
    {
        CosineSimilarityFunction function = new();
        float result = function.Execute([
            DataValue.FromVector([1f, 2f, 3f]),
            DataValue.FromVector([1f, 2f, 3f])
        ]).AsFloat32();
        Assert.Equal(1f, result, 1e-5f);
    }

    [Fact]
    public void CosineSimilarity_Orthogonal()
    {
        CosineSimilarityFunction function = new();
        float result = function.Execute([
            DataValue.FromVector([1f, 0f]),
            DataValue.FromVector([0f, 1f])
        ]).AsFloat32();
        Assert.Equal(0f, result, 1e-5f);
    }

    [Fact]
    public void CosineSimilarity_Opposite()
    {
        CosineSimilarityFunction function = new();
        float result = function.Execute([
            DataValue.FromVector([1f, 0f]),
            DataValue.FromVector([-1f, 0f])
        ]).AsFloat32();
        Assert.Equal(-1f, result, 1e-5f);
    }

    [Fact]
    public void EuclideanDistance_KnownValues()
    {
        EuclideanDistanceFunction function = new();
        float result = function.Execute([
            DataValue.FromVector([0f, 0f]),
            DataValue.FromVector([3f, 4f])
        ]).AsFloat32();
        Assert.Equal(5f, result, 1e-5f);
    }

    [Fact]
    public void EuclideanDistance_Same_IsZero()
    {
        EuclideanDistanceFunction function = new();
        float result = function.Execute([
            DataValue.FromVector([1f, 2f, 3f]),
            DataValue.FromVector([1f, 2f, 3f])
        ]).AsFloat32();
        Assert.Equal(0f, result, 1e-5f);
    }

    [Fact]
    public void ManhattanDistance_KnownValues()
    {
        ManhattanDistanceFunction function = new();
        float result = function.Execute([
            DataValue.FromVector([0f, 0f]),
            DataValue.FromVector([3f, 4f])
        ]).AsFloat32();
        Assert.Equal(7f, result, 1e-5f);
    }

    [Fact]
    public void Dot_KnownValues()
    {
        DotFunction function = new();
        float result = function.Execute([
            DataValue.FromVector([1f, 2f, 3f]),
            DataValue.FromVector([4f, 5f, 6f])
        ]).AsFloat32();
        Assert.Equal(32f, result, 1e-5f); // 1*4 + 2*5 + 3*6 = 32
    }

    [Fact]
    public void Dot_Orthogonal()
    {
        DotFunction function = new();
        float result = function.Execute([
            DataValue.FromVector([1f, 0f]),
            DataValue.FromVector([0f, 1f])
        ]).AsFloat32();
        Assert.Equal(0f, result, 1e-5f);
    }

    [Fact]
    public void HammingDistance_SameString()
    {
        HammingDistanceFunction function = new();
        float result = function.Execute([
            DataValue.FromString("hello"),
            DataValue.FromString("hello")
        ]).AsFloat32();
        Assert.Equal(0f, result);
    }

    [Fact]
    public void HammingDistance_OneCharDiff()
    {
        HammingDistanceFunction function = new();
        float result = function.Execute([
            DataValue.FromString("hello"),
            DataValue.FromString("hallo")
        ]).AsFloat32();
        Assert.Equal(1f, result);
    }

    [Fact]
    public void HammingDistance_DifferentLengths()
    {
        HammingDistanceFunction function = new();
        float result = function.Execute([
            DataValue.FromString("hi"),
            DataValue.FromString("hello")
        ]).AsFloat32();
        // 3 length diff + 1 char diff ('i' vs 'e')
        Assert.Equal(4f, result);
    }

    [Fact]
    public void CosineSimilarity_Null_ReturnsNull()
    {
        CosineSimilarityFunction function = new();
        Assert.True(function.Execute([DataValue.Null(DataKind.Vector), DataValue.FromVector([1f])]).IsNull);
    }

    [Fact]
    public void CosineSimilarity_Validate_Scalar_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new CosineSimilarityFunction().ValidateArguments([DataKind.Float32, DataKind.Vector]));
    }

    [Fact]
    public void HammingDistance_Validate_NonString_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new HammingDistanceFunction().ValidateArguments([DataKind.Float32, DataKind.Float32]));
    }

    [Fact]
    public void EuclideanDistance_Null_ReturnsNull()
    {
        Assert.True(new EuclideanDistanceFunction().Execute([
            DataValue.FromVector([1f]),
            DataValue.Null(DataKind.Vector)
        ]).IsNull);
    }
}
