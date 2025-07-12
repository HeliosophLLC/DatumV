using DatumIngest.Functions.Math;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Math;

public class SoftmaxFunctionTests
{
    [Fact]
    public void Softmax_SumsToOne()
    {
        SoftmaxFunction function = new();
        DataValue result = function.Execute([DataValue.FromVector([1f, 2f, 3f])]);
        float[] values = result.AsVector();
        Assert.Equal(3, values.Length);
        float sum = values[0] + values[1] + values[2];
        Assert.Equal(1f, sum, 1e-5f);
    }

    [Fact]
    public void Softmax_MaxElementHasHighestProb()
    {
        SoftmaxFunction function = new();
        DataValue result = function.Execute([DataValue.FromVector([1f, 5f, 2f])]);
        float[] values = result.AsVector();
        Assert.True(values[1] > values[0]);
        Assert.True(values[1] > values[2]);
    }

    [Fact]
    public void Softmax_AllEqual_Uniform()
    {
        SoftmaxFunction function = new();
        DataValue result = function.Execute([DataValue.FromVector([1f, 1f, 1f])]);
        float[] values = result.AsVector();
        Assert.Equal(values[0], values[1], 1e-5f);
        Assert.Equal(values[1], values[2], 1e-5f);
    }

    [Fact]
    public void Softmax_LargeValues_NumericallyStable()
    {
        SoftmaxFunction function = new();
        DataValue result = function.Execute([DataValue.FromVector([1000f, 1001f, 1002f])]);
        float[] values = result.AsVector();
        float sum = values[0] + values[1] + values[2];
        Assert.Equal(1f, sum, 1e-4f);
        Assert.False(float.IsNaN(values[0]));
    }

    [Fact]
    public void Softmax_Null_ReturnsNull()
    {
        Assert.True(new SoftmaxFunction().Execute([DataValue.Null(DataKind.Vector)]).IsNull);
    }

    [Fact]
    public void Softmax_Validate_NonVector_Throws()
    {
        Assert.Throws<ArgumentException>(() => new SoftmaxFunction().ValidateArguments([DataKind.Scalar]));
    }

    [Fact]
    public void LogSoftmax_SumsCorrectly()
    {
        LogSoftmaxFunction function = new();
        DataValue result = function.Execute([DataValue.FromVector([1f, 2f, 3f])]);
        float[] values = result.AsVector();
        Assert.Equal(3, values.Length);
        // All values should be negative (log of probability)
        Assert.True(values[0] < 0);
        Assert.True(values[1] < 0);
        Assert.True(values[2] < 0);
        // exp(log_softmax) should sum to ~1
        float expSum = MathF.Exp(values[0]) + MathF.Exp(values[1]) + MathF.Exp(values[2]);
        Assert.Equal(1f, expSum, 1e-4f);
    }

    [Fact]
    public void LogSoftmax_LargeValues_Stable()
    {
        LogSoftmaxFunction function = new();
        DataValue result = function.Execute([DataValue.FromVector([1000f, 1001f, 1002f])]);
        float[] values = result.AsVector();
        Assert.False(float.IsNaN(values[0]));
        Assert.False(float.IsInfinity(values[0]));
    }

    [Fact]
    public void L2Normalize_UnitLength()
    {
        L2NormalizeFunction function = new();
        DataValue result = function.Execute([DataValue.FromVector([3f, 4f])]);
        float[] values = result.AsVector();
        Assert.Equal(0.6f, values[0], 1e-5f);
        Assert.Equal(0.8f, values[1], 1e-5f);
        float norm = MathF.Sqrt(values[0] * values[0] + values[1] * values[1]);
        Assert.Equal(1f, norm, 1e-5f);
    }

    [Fact]
    public void L2Normalize_ZeroVector_ReturnsZero()
    {
        L2NormalizeFunction function = new();
        DataValue result = function.Execute([DataValue.FromVector([0f, 0f, 0f])]);
        float[] values = result.AsVector();
        Assert.Equal([0f, 0f, 0f], values);
    }

    [Fact]
    public void L2Normalize_Null_ReturnsNull()
    {
        Assert.True(new L2NormalizeFunction().Execute([DataValue.Null(DataKind.Vector)]).IsNull);
    }
}
