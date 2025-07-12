using DatumIngest.Functions.Math;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Math;

public class PowerFunctionTests
{
    [Fact]
    public void Sqrt_Scalar()
    {
        Assert.Equal(3f, new SqrtFunction().Execute([DataValue.FromScalar(9)]).AsScalar());
    }

    [Fact]
    public void Sqrt_NegativeReturnsNaN()
    {
        Assert.True(float.IsNaN(new SqrtFunction().Execute([DataValue.FromScalar(-1)]).AsScalar()));
    }

    [Fact]
    public void Cbrt_Positive()
    {
        Assert.Equal(MathF.Cbrt(27), new CbrtFunction().Execute([DataValue.FromScalar(27)]).AsScalar());
    }

    [Fact]
    public void Cbrt_Negative()
    {
        Assert.Equal(MathF.Cbrt(-8), new CbrtFunction().Execute([DataValue.FromScalar(-8)]).AsScalar());
    }

    [Fact]
    public void Square_Scalar()
    {
        Assert.Equal(25f, new SquareFunction().Execute([DataValue.FromScalar(5)]).AsScalar());
    }

    [Fact]
    public void Square_Negative()
    {
        Assert.Equal(9f, new SquareFunction().Execute([DataValue.FromScalar(-3)]).AsScalar());
    }

    [Fact]
    public void Exp_Zero()
    {
        Assert.Equal(1f, new ExpFunction().Execute([DataValue.FromScalar(0)]).AsScalar());
    }

    [Fact]
    public void Exp_One()
    {
        Assert.Equal(MathF.E, new ExpFunction().Execute([DataValue.FromScalar(1)]).AsScalar(), 1e-5f);
    }

    [Fact]
    public void Exp2_Three()
    {
        Assert.Equal(8f, new Exp2Function().Execute([DataValue.FromScalar(3)]).AsScalar());
    }

    [Fact]
    public void Ln_E()
    {
        Assert.Equal(1f, new LnFunction().Execute([DataValue.FromScalar(MathF.E)]).AsScalar(), 1e-5f);
    }

    [Fact]
    public void Ln_One()
    {
        Assert.Equal(0f, new LnFunction().Execute([DataValue.FromScalar(1)]).AsScalar());
    }

    [Fact]
    public void Log2_Eight()
    {
        Assert.Equal(3f, new Log2Function().Execute([DataValue.FromScalar(8)]).AsScalar());
    }

    [Fact]
    public void Log10_Thousand()
    {
        Assert.Equal(3f, new Log10Function().Execute([DataValue.FromScalar(1000)]).AsScalar(), 1e-5f);
    }

    [Fact]
    public void Pow_TwoToThree()
    {
        PowFunction function = new();
        Assert.Equal(8f, function.Execute([DataValue.FromScalar(2), DataValue.FromScalar(3)]).AsScalar());
    }

    [Fact]
    public void Pow_Vector()
    {
        PowFunction function = new();
        DataValue result = function.Execute([DataValue.FromVector([2f, 3f, 4f]), DataValue.FromScalar(2)]);
        Assert.Equal([4f, 9f, 16f], result.AsVector());
    }

    [Fact]
    public void Log_CustomBase()
    {
        LogFunction function = new();
        Assert.Equal(3f, function.Execute([DataValue.FromScalar(8), DataValue.FromScalar(2)]).AsScalar(), 1e-5f);
    }

    [Fact]
    public void Sqrt_Vector()
    {
        SqrtFunction function = new();
        DataValue result = function.Execute([DataValue.FromVector([4f, 9f, 16f])]);
        Assert.Equal([2f, 3f, 4f], result.AsVector());
    }

    [Fact]
    public void Sqrt_Null_ReturnsNull()
    {
        Assert.True(new SqrtFunction().Execute([DataValue.Null(DataKind.Scalar)]).IsNull);
    }
}
