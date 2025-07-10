using DatumQuery.Functions.Math;
using DatumQuery.Model;

namespace DatumQuery.Tests.Functions.Math;

public class RoundingFunctionTests
{
    [Fact]
    public void Ceil_RoundsUp()
    {
        Assert.Equal(4f, new CeilFunction().Execute([DataValue.FromScalar(3.2f)]).AsScalar());
    }

    [Fact]
    public void Ceil_Negative()
    {
        Assert.Equal(-3f, new CeilFunction().Execute([DataValue.FromScalar(-3.7f)]).AsScalar());
    }

    [Fact]
    public void Floor_RoundsDown()
    {
        Assert.Equal(3f, new FloorFunction().Execute([DataValue.FromScalar(3.8f)]).AsScalar());
    }

    [Fact]
    public void Floor_Negative()
    {
        Assert.Equal(-4f, new FloorFunction().Execute([DataValue.FromScalar(-3.2f)]).AsScalar());
    }

    [Fact]
    public void Truncate_Positive()
    {
        Assert.Equal(3f, new TruncateFunction().Execute([DataValue.FromScalar(3.9f)]).AsScalar());
    }

    [Fact]
    public void Truncate_Negative()
    {
        Assert.Equal(-3f, new TruncateFunction().Execute([DataValue.FromScalar(-3.9f)]).AsScalar());
    }

    [Fact]
    public void Round_NoDecimals()
    {
        RoundFunction function = new();
        Assert.Equal(4f, function.Execute([DataValue.FromScalar(3.7f)]).AsScalar());
    }

    [Fact]
    public void Round_TwoDecimals()
    {
        RoundFunction function = new();
        Assert.Equal(3.14f, function.Execute([DataValue.FromScalar(3.1415f), DataValue.FromScalar(2)]).AsScalar(), 1e-5f);
    }

    [Fact]
    public void Round_Vector()
    {
        RoundFunction function = new();
        DataValue result = function.Execute([DataValue.FromVector([1.1f, 2.5f, 3.9f])]);
        Assert.Equal([1f, 3f, 4f], result.AsVector());
    }

    [Fact]
    public void Quantize_HalfStep()
    {
        QuantizeFunction function = new();
        Assert.Equal(3.5f, function.Execute([DataValue.FromScalar(3.7f), DataValue.FromScalar(0.5f)]).AsScalar());
    }

    [Fact]
    public void Quantize_IntegerStep()
    {
        QuantizeFunction function = new();
        Assert.Equal(10f, function.Execute([DataValue.FromScalar(12f), DataValue.FromScalar(5f)]).AsScalar());
    }

    [Fact]
    public void Bucketize_InRange()
    {
        BucketizeFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromScalar(15),
            DataValue.FromVector([10f, 20f, 30f])
        ]);
        Assert.Equal(1f, result.AsScalar());
    }

    [Fact]
    public void Bucketize_BelowAll()
    {
        BucketizeFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromScalar(5),
            DataValue.FromVector([10f, 20f, 30f])
        ]);
        Assert.Equal(0f, result.AsScalar());
    }

    [Fact]
    public void Bucketize_AboveAll()
    {
        BucketizeFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromScalar(50),
            DataValue.FromVector([10f, 20f, 30f])
        ]);
        Assert.Equal(3f, result.AsScalar());
    }

    [Fact]
    public void Clip_DelegatesToClamp()
    {
        ClipFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromScalar(150),
            DataValue.FromScalar(0),
            DataValue.FromScalar(100)
        ]);
        Assert.Equal(100f, result.AsScalar());
    }

    [Fact]
    public void Round_Null_ReturnsNull()
    {
        RoundFunction function = new();
        Assert.True(function.Execute([DataValue.Null(DataKind.Scalar)]).IsNull);
    }

    [Fact]
    public void Round_Validate_InvalidArgCount()
    {
        RoundFunction function = new();
        Assert.Throws<ArgumentException>(() => function.ValidateArguments([]));
    }

    [Fact]
    public void Ceil_Vector()
    {
        CeilFunction function = new();
        DataValue result = function.Execute([DataValue.FromVector([1.1f, 2.5f, -0.3f])]);
        Assert.Equal([2f, 3f, 0f], result.AsVector());
    }
}
