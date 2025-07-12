using DatumIngest.Functions.Math;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Math;

public class ActivationFunctionTests
{
    [Fact]
    public void Sigmoid_Zero()
    {
        Assert.Equal(0.5f, new SigmoidFunction().Execute([DataValue.FromScalar(0)]).AsScalar(), 1e-5f);
    }

    [Fact]
    public void Sigmoid_LargePositive()
    {
        Assert.Equal(1f, new SigmoidFunction().Execute([DataValue.FromScalar(100)]).AsScalar(), 1e-3f);
    }

    [Fact]
    public void Sigmoid_LargeNegative()
    {
        Assert.Equal(0f, new SigmoidFunction().Execute([DataValue.FromScalar(-100)]).AsScalar(), 1e-3f);
    }

    [Fact]
    public void Relu_Positive()
    {
        Assert.Equal(5f, new ReluFunction().Execute([DataValue.FromScalar(5)]).AsScalar());
    }

    [Fact]
    public void Relu_Negative()
    {
        Assert.Equal(0f, new ReluFunction().Execute([DataValue.FromScalar(-5)]).AsScalar());
    }

    [Fact]
    public void Relu_Zero()
    {
        Assert.Equal(0f, new ReluFunction().Execute([DataValue.FromScalar(0)]).AsScalar());
    }

    [Fact]
    public void Relu_Vector()
    {
        ReluFunction function = new();
        DataValue result = function.Execute([DataValue.FromVector([-2f, 0f, 3f])]);
        Assert.Equal([0f, 0f, 3f], result.AsVector());
    }

    [Fact]
    public void Selu_Positive()
    {
        float result = new SeluFunction().Execute([DataValue.FromScalar(2)]).AsScalar();
        Assert.Equal(2 * 1.0507009873554805f, result, 1e-4f);
    }

    [Fact]
    public void Selu_Negative()
    {
        float result = new SeluFunction().Execute([DataValue.FromScalar(-1)]).AsScalar();
        Assert.True(result < 0);
    }

    [Fact]
    public void Gelu_Zero()
    {
        Assert.Equal(0f, new GeluFunction().Execute([DataValue.FromScalar(0)]).AsScalar(), 1e-2f);
    }

    [Fact]
    public void Gelu_Positive()
    {
        float result = new GeluFunction().Execute([DataValue.FromScalar(1)]).AsScalar();
        Assert.True(result > 0.5f && result < 1f);
    }

    [Fact]
    public void Swish_Zero()
    {
        Assert.Equal(0f, new SwishFunction().Execute([DataValue.FromScalar(0)]).AsScalar(), 1e-5f);
    }

    [Fact]
    public void Swish_Positive()
    {
        float result = new SwishFunction().Execute([DataValue.FromScalar(2)]).AsScalar();
        float expected = 2f * (1f / (1f + MathF.Exp(-2f)));
        Assert.Equal(expected, result, 1e-5f);
    }

    [Fact]
    public void Softplus_Positive()
    {
        float result = new SoftplusFunction().Execute([DataValue.FromScalar(2)]).AsScalar();
        Assert.Equal(MathF.Log(1f + MathF.Exp(2f)), result, 1e-5f);
    }

    [Fact]
    public void Softplus_Zero()
    {
        Assert.Equal(MathF.Log(2f), new SoftplusFunction().Execute([DataValue.FromScalar(0)]).AsScalar(), 1e-5f);
    }

    [Fact]
    public void Softsign_Positive()
    {
        float result = new SoftsignFunction().Execute([DataValue.FromScalar(2)]).AsScalar();
        Assert.Equal(2f / 3f, result, 1e-5f);
    }

    [Fact]
    public void Softsign_Negative()
    {
        float result = new SoftsignFunction().Execute([DataValue.FromScalar(-2)]).AsScalar();
        Assert.Equal(-2f / 3f, result, 1e-5f);
    }

    [Fact]
    public void Mish_Zero()
    {
        Assert.Equal(0f, new MishFunction().Execute([DataValue.FromScalar(0)]).AsScalar(), 1e-5f);
    }

    [Fact]
    public void HardSigmoid_Zero()
    {
        float result = new HardSigmoidFunction().Execute([DataValue.FromScalar(0)]).AsScalar();
        Assert.Equal(0.5f, result, 1e-5f);
    }

    [Fact]
    public void HardSigmoid_ClipsAbove()
    {
        Assert.Equal(1f, new HardSigmoidFunction().Execute([DataValue.FromScalar(10)]).AsScalar());
    }

    [Fact]
    public void HardSigmoid_ClipsBelow()
    {
        Assert.Equal(0f, new HardSigmoidFunction().Execute([DataValue.FromScalar(-10)]).AsScalar());
    }

    [Fact]
    public void HardSwish_Zero()
    {
        Assert.Equal(0f, new HardSwishFunction().Execute([DataValue.FromScalar(0)]).AsScalar(), 1e-5f);
    }

    [Fact]
    public void LeakyRelu_Positive()
    {
        LeakyReluFunction function = new();
        Assert.Equal(5f, function.Execute([DataValue.FromScalar(5)]).AsScalar());
    }

    [Fact]
    public void LeakyRelu_Negative_DefaultAlpha()
    {
        LeakyReluFunction function = new();
        Assert.Equal(-0.05f, function.Execute([DataValue.FromScalar(-5)]).AsScalar(), 1e-5f);
    }

    [Fact]
    public void LeakyRelu_CustomAlpha()
    {
        LeakyReluFunction function = new();
        float result = function.Execute([DataValue.FromScalar(-5), DataValue.FromScalar(0.1f)]).AsScalar();
        Assert.Equal(-0.5f, result, 1e-5f);
    }

    [Fact]
    public void LeakyRelu_Vector()
    {
        LeakyReluFunction function = new();
        DataValue result = function.Execute([DataValue.FromVector([-2f, 0f, 3f])]);
        float[] values = result.AsVector();
        Assert.Equal(-0.02f, values[0], 1e-5f);
        Assert.Equal(0f, values[1]);
        Assert.Equal(3f, values[2]);
    }

    [Fact]
    public void Elu_Positive()
    {
        EluFunction function = new();
        Assert.Equal(3f, function.Execute([DataValue.FromScalar(3)]).AsScalar());
    }

    [Fact]
    public void Elu_Negative_DefaultAlpha()
    {
        EluFunction function = new();
        float result = function.Execute([DataValue.FromScalar(-1)]).AsScalar();
        float expected = 1f * (MathF.Exp(-1f) - 1f);
        Assert.Equal(expected, result, 1e-5f);
    }

    [Fact]
    public void Elu_CustomAlpha()
    {
        EluFunction function = new();
        float result = function.Execute([DataValue.FromScalar(-1), DataValue.FromScalar(2f)]).AsScalar();
        float expected = 2f * (MathF.Exp(-1f) - 1f);
        Assert.Equal(expected, result, 1e-5f);
    }

    [Fact]
    public void Sigmoid_Null_ReturnsNull()
    {
        Assert.True(new SigmoidFunction().Execute([DataValue.Null(DataKind.Scalar)]).IsNull);
    }

    [Fact]
    public void LeakyRelu_Validate_InvalidArgCount()
    {
        Assert.Throws<ArgumentException>(() => new LeakyReluFunction().ValidateArguments([]));
    }

    [Fact]
    public void Elu_Validate_InvalidArgCount()
    {
        Assert.Throws<ArgumentException>(() => new EluFunction().ValidateArguments([]));
    }
}
