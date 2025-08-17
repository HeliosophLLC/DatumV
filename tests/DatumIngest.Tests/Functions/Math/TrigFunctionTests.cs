using DatumIngest.Functions.Math;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Math;

public class TrigFunctionTests
{
    [Fact]
    public void Sin_Zero() => Assert.Equal(0f, new SinFunction().Execute([DataValue.FromFloat32(0)]).AsFloat32(), 1e-5f);

    [Fact]
    public void Sin_PiOverTwo()
    {
        Assert.Equal(1f, new SinFunction().Execute([DataValue.FromFloat32(MathF.PI / 2)]).AsFloat32(), 1e-5f);
    }

    [Fact]
    public void Cos_Zero() => Assert.Equal(1f, new CosFunction().Execute([DataValue.FromFloat32(0)]).AsFloat32(), 1e-5f);

    [Fact]
    public void Cos_Pi()
    {
        Assert.Equal(-1f, new CosFunction().Execute([DataValue.FromFloat32(MathF.PI)]).AsFloat32(), 1e-5f);
    }

    [Fact]
    public void Tan_Zero() => Assert.Equal(0f, new TanFunction().Execute([DataValue.FromFloat32(0)]).AsFloat32(), 1e-5f);

    [Fact]
    public void Asin_One()
    {
        Assert.Equal(MathF.PI / 2, new AsinFunction().Execute([DataValue.FromFloat32(1)]).AsFloat32(), 1e-5f);
    }

    [Fact]
    public void Acos_One()
    {
        Assert.Equal(0f, new AcosFunction().Execute([DataValue.FromFloat32(1)]).AsFloat32(), 1e-5f);
    }

    [Fact]
    public void Atan_Zero()
    {
        Assert.Equal(0f, new AtanFunction().Execute([DataValue.FromFloat32(0)]).AsFloat32(), 1e-5f);
    }

    [Fact]
    public void Atan2_OneOne()
    {
        Atan2Function function = new();
        float result = function.Execute([DataValue.FromFloat32(1), DataValue.FromFloat32(1)]).AsFloat32();
        Assert.Equal(MathF.PI / 4, result, 1e-5f);
    }

    [Fact]
    public void Sinh_Zero() => Assert.Equal(0f, new SinhFunction().Execute([DataValue.FromFloat32(0)]).AsFloat32(), 1e-5f);

    [Fact]
    public void Cosh_Zero() => Assert.Equal(1f, new CoshFunction().Execute([DataValue.FromFloat32(0)]).AsFloat32(), 1e-5f);

    [Fact]
    public void Tanh_Zero() => Assert.Equal(0f, new TanhFunction().Execute([DataValue.FromFloat32(0)]).AsFloat32(), 1e-5f);

    [Fact]
    public void Tanh_LargePositive()
    {
        float result = new TanhFunction().Execute([DataValue.FromFloat32(100)]).AsFloat32();
        Assert.Equal(1f, result, 1e-3f);
    }

    [Fact]
    public void Degrees_PiIs180()
    {
        Assert.Equal(180f, new DegreesFunction().Execute([DataValue.FromFloat32(MathF.PI)]).AsFloat32(), 1e-3f);
    }

    [Fact]
    public void Radians_180IsPi()
    {
        Assert.Equal(MathF.PI, new RadiansFunction().Execute([DataValue.FromFloat32(180)]).AsFloat32(), 1e-5f);
    }

    [Fact]
    public void Pi_ReturnsConstant()
    {
        PiFunction function = new();
        Assert.Equal(DataKind.Float32, function.ValidateArguments([]));
        Assert.Equal(MathF.PI, function.Execute([]).AsFloat32(), 1e-5f);
    }

    [Fact]
    public void Euler_ReturnsConstant()
    {
        EulerFunction function = new();
        Assert.Equal(DataKind.Float32, function.ValidateArguments([]));
        Assert.Equal(MathF.E, function.Execute([]).AsFloat32(), 1e-5f);
    }

    [Fact]
    public void Pi_WithArgs_Throws()
    {
        Assert.Throws<ArgumentException>(() => new PiFunction().ValidateArguments([DataKind.Float32]));
    }

    [Fact]
    public void Sin_Vector()
    {
        SinFunction function = new();
        DataValue result = function.Execute([DataValue.FromVector([0f, MathF.PI / 2, MathF.PI])]);
        float[] values = result.AsVector();
        Assert.Equal(0f, values[0], 1e-5f);
        Assert.Equal(1f, values[1], 1e-5f);
        Assert.Equal(0f, values[2], 1e-4f);
    }

    [Fact]
    public void Sin_Null_ReturnsNull()
    {
        Assert.True(new SinFunction().Execute([DataValue.Null(DataKind.Float32)]).IsNull);
    }
}
