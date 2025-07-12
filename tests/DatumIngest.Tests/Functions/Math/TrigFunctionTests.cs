using DatumIngest.Functions.Math;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Math;

public class TrigFunctionTests
{
    [Fact]
    public void Sin_Zero() => Assert.Equal(0f, new SinFunction().Execute([DataValue.FromScalar(0)]).AsScalar(), 1e-5f);

    [Fact]
    public void Sin_PiOverTwo()
    {
        Assert.Equal(1f, new SinFunction().Execute([DataValue.FromScalar(MathF.PI / 2)]).AsScalar(), 1e-5f);
    }

    [Fact]
    public void Cos_Zero() => Assert.Equal(1f, new CosFunction().Execute([DataValue.FromScalar(0)]).AsScalar(), 1e-5f);

    [Fact]
    public void Cos_Pi()
    {
        Assert.Equal(-1f, new CosFunction().Execute([DataValue.FromScalar(MathF.PI)]).AsScalar(), 1e-5f);
    }

    [Fact]
    public void Tan_Zero() => Assert.Equal(0f, new TanFunction().Execute([DataValue.FromScalar(0)]).AsScalar(), 1e-5f);

    [Fact]
    public void Asin_One()
    {
        Assert.Equal(MathF.PI / 2, new AsinFunction().Execute([DataValue.FromScalar(1)]).AsScalar(), 1e-5f);
    }

    [Fact]
    public void Acos_One()
    {
        Assert.Equal(0f, new AcosFunction().Execute([DataValue.FromScalar(1)]).AsScalar(), 1e-5f);
    }

    [Fact]
    public void Atan_Zero()
    {
        Assert.Equal(0f, new AtanFunction().Execute([DataValue.FromScalar(0)]).AsScalar(), 1e-5f);
    }

    [Fact]
    public void Atan2_OneOne()
    {
        Atan2Function function = new();
        float result = function.Execute([DataValue.FromScalar(1), DataValue.FromScalar(1)]).AsScalar();
        Assert.Equal(MathF.PI / 4, result, 1e-5f);
    }

    [Fact]
    public void Sinh_Zero() => Assert.Equal(0f, new SinhFunction().Execute([DataValue.FromScalar(0)]).AsScalar(), 1e-5f);

    [Fact]
    public void Cosh_Zero() => Assert.Equal(1f, new CoshFunction().Execute([DataValue.FromScalar(0)]).AsScalar(), 1e-5f);

    [Fact]
    public void Tanh_Zero() => Assert.Equal(0f, new TanhFunction().Execute([DataValue.FromScalar(0)]).AsScalar(), 1e-5f);

    [Fact]
    public void Tanh_LargePositive()
    {
        float result = new TanhFunction().Execute([DataValue.FromScalar(100)]).AsScalar();
        Assert.Equal(1f, result, 1e-3f);
    }

    [Fact]
    public void Degrees_PiIs180()
    {
        Assert.Equal(180f, new DegreesFunction().Execute([DataValue.FromScalar(MathF.PI)]).AsScalar(), 1e-3f);
    }

    [Fact]
    public void Radians_180IsPi()
    {
        Assert.Equal(MathF.PI, new RadiansFunction().Execute([DataValue.FromScalar(180)]).AsScalar(), 1e-5f);
    }

    [Fact]
    public void Pi_ReturnsConstant()
    {
        PiFunction function = new();
        Assert.Equal(DataKind.Scalar, function.ValidateArguments([]));
        Assert.Equal(MathF.PI, function.Execute([]).AsScalar(), 1e-5f);
    }

    [Fact]
    public void Euler_ReturnsConstant()
    {
        EulerFunction function = new();
        Assert.Equal(DataKind.Scalar, function.ValidateArguments([]));
        Assert.Equal(MathF.E, function.Execute([]).AsScalar(), 1e-5f);
    }

    [Fact]
    public void Pi_WithArgs_Throws()
    {
        Assert.Throws<ArgumentException>(() => new PiFunction().ValidateArguments([DataKind.Scalar]));
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
        Assert.True(new SinFunction().Execute([DataValue.Null(DataKind.Scalar)]).IsNull);
    }
}
