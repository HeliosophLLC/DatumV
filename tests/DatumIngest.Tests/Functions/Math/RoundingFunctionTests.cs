using DatumIngest.Functions.Math;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Math;

public class RoundingFunctionTests
{
    [Fact]
    public void Ceil_RoundsUp()
    {
        Assert.Equal(4f, new CeilFunction().Execute([DataValue.FromFloat32(3.2f)]).AsFloat32());
    }

    [Fact]
    public void Ceil_Negative()
    {
        Assert.Equal(-3f, new CeilFunction().Execute([DataValue.FromFloat32(-3.7f)]).AsFloat32());
    }

    [Fact]
    public void Floor_RoundsDown()
    {
        Assert.Equal(3f, new FloorFunction().Execute([DataValue.FromFloat32(3.8f)]).AsFloat32());
    }

    [Fact]
    public void Floor_Negative()
    {
        Assert.Equal(-4f, new FloorFunction().Execute([DataValue.FromFloat32(-3.2f)]).AsFloat32());
    }

    [Fact]
    public void Truncate_Positive()
    {
        Assert.Equal(3f, new TruncateFunction().Execute([DataValue.FromFloat32(3.9f)]).AsFloat32());
    }

    [Fact]
    public void Truncate_Negative()
    {
        Assert.Equal(-3f, new TruncateFunction().Execute([DataValue.FromFloat32(-3.9f)]).AsFloat32());
    }

    [Fact]
    public void Round_NoDecimals()
    {
        RoundFunction function = new();
        Assert.Equal(4f, function.Execute([DataValue.FromFloat32(3.7f)]).AsFloat32());
    }

    [Fact]
    public void Round_TwoDecimals()
    {
        RoundFunction function = new();
        Assert.Equal(3.14f, function.Execute([DataValue.FromFloat32(3.1415f), DataValue.FromFloat32(2)]).AsFloat32(), 1e-5f);
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
        Assert.Equal(3.5f, function.Execute([DataValue.FromFloat32(3.7f), DataValue.FromFloat32(0.5f)]).AsFloat32());
    }

    [Fact]
    public void Quantize_IntegerStep()
    {
        QuantizeFunction function = new();
        Assert.Equal(10f, function.Execute([DataValue.FromFloat32(12f), DataValue.FromFloat32(5f)]).AsFloat32());
    }

    [Fact]
    public void Bucketize_InRange()
    {
        BucketizeFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromFloat32(15),
            DataValue.FromVector([10f, 20f, 30f])
        ]);
        Assert.Equal(1f, result.AsFloat32());
    }

    [Fact]
    public void Bucketize_BelowAll()
    {
        BucketizeFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromFloat32(5),
            DataValue.FromVector([10f, 20f, 30f])
        ]);
        Assert.Equal(0f, result.AsFloat32());
    }

    [Fact]
    public void Bucketize_AboveAll()
    {
        BucketizeFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromFloat32(50),
            DataValue.FromVector([10f, 20f, 30f])
        ]);
        Assert.Equal(3f, result.AsFloat32());
    }

    [Fact]
    public void Clip_DelegatesToClamp()
    {
        ClipFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromFloat32(150),
            DataValue.FromFloat32(0),
            DataValue.FromFloat32(100)
        ]);
        Assert.Equal(100f, result.AsFloat32());
    }

    [Fact]
    public void Round_Null_ReturnsNull()
    {
        RoundFunction function = new();
        Assert.True(function.Execute([DataValue.Null(DataKind.Float32)]).IsNull);
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
