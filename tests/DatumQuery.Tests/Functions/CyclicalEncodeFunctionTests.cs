using Axon.QueryEngine.Functions.Scalar;
using Axon.QueryEngine.Model;

namespace Axon.QueryEngine.Tests.Functions;

/// <summary>
/// Tests for <see cref="CyclicalEncodeFunction"/>.
/// </summary>
public class CyclicalEncodeFunctionTests
{
    private readonly CyclicalEncodeFunction _function = new();

    [Fact]
    public void Name_IsCyclicalEncode()
    {
        Assert.Equal("cyclical_encode", _function.Name);
    }

    [Fact]
    public void CyclicalEncode_ZeroValue()
    {
        // cyclical_encode(0, 12) → [sin(0), cos(0)] = [0, 1]
        DataValue result = _function.Execute([DataValue.FromScalar(0f), DataValue.FromScalar(12f)]);
        float[] vector = result.AsVector();
        Assert.Equal(2, vector.Length);
        Assert.Equal(0f, vector[0], 1e-5f);
        Assert.Equal(1f, vector[1], 1e-5f);
    }

    [Fact]
    public void CyclicalEncode_QuarterPeriod()
    {
        // cyclical_encode(3, 12) → [sin(π/2), cos(π/2)] = [1, 0]
        DataValue result = _function.Execute([DataValue.FromScalar(3f), DataValue.FromScalar(12f)]);
        float[] vector = result.AsVector();
        Assert.Equal(2, vector.Length);
        Assert.Equal(1f, vector[0], 1e-5f);
        Assert.Equal(0f, vector[1], 1e-5f);
    }

    [Fact]
    public void CyclicalEncode_HalfPeriod()
    {
        // cyclical_encode(6, 12) → [sin(π), cos(π)] ≈ [0, -1]
        DataValue result = _function.Execute([DataValue.FromScalar(6f), DataValue.FromScalar(12f)]);
        float[] vector = result.AsVector();
        Assert.Equal(2, vector.Length);
        Assert.Equal(0f, vector[0], 1e-5f);
        Assert.Equal(-1f, vector[1], 1e-5f);
    }

    [Fact]
    public void CyclicalEncode_FullPeriod()
    {
        // cyclical_encode(12, 12) → [sin(2π), cos(2π)] ≈ [0, 1]
        DataValue result = _function.Execute([DataValue.FromScalar(12f), DataValue.FromScalar(12f)]);
        float[] vector = result.AsVector();
        Assert.Equal(2, vector.Length);
        Assert.Equal(0f, vector[0], 1e-5f);
        Assert.Equal(1f, vector[1], 1e-5f);
    }

    [Fact]
    public void CyclicalEncode_HourEncoding()
    {
        // cyclical_encode(6, 24) → [sin(π/2), cos(π/2)] = [1, 0]
        DataValue result = _function.Execute([DataValue.FromScalar(6f), DataValue.FromScalar(24f)]);
        float[] vector = result.AsVector();
        Assert.Equal(1f, vector[0], 1e-5f);
        Assert.Equal(0f, vector[1], 1e-5f);
    }

    [Fact]
    public void CyclicalEncode_NullValue_ReturnsTypedNull()
    {
        DataValue result = _function.Execute([DataValue.Null(DataKind.Scalar), DataValue.FromScalar(12f)]);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Vector, result.Kind);
    }

    [Fact]
    public void CyclicalEncode_NullPeriod_ReturnsTypedNull()
    {
        DataValue result = _function.Execute([DataValue.FromScalar(6f), DataValue.Null(DataKind.Scalar)]);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Vector, result.Kind);
    }

    [Fact]
    public void CyclicalEncode_ReturnsVector()
    {
        DataValue result = _function.Execute([DataValue.FromScalar(1f), DataValue.FromScalar(7f)]);
        Assert.Equal(DataKind.Vector, result.Kind);
        Assert.Equal(2, result.AsVector().Length);
    }

    [Fact]
    public void ValidateArguments_RejectsNonScalarValue()
    {
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments([DataKind.String, DataKind.Scalar]));
    }

    [Fact]
    public void ValidateArguments_RejectsNonScalarPeriod()
    {
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments([DataKind.Scalar, DataKind.String]));
    }

    [Fact]
    public void ValidateArguments_RejectsWrongArgumentCount()
    {
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments([DataKind.Scalar]));
    }

    [Fact]
    public void ValidateArguments_ReturnsVector()
    {
        DataKind result = _function.ValidateArguments([DataKind.Scalar, DataKind.Scalar]);
        Assert.Equal(DataKind.Vector, result);
    }
}
