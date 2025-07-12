using DatumQuery.Functions.Math;
using DatumQuery.Model;

namespace DatumQuery.Tests.Functions.Math;

public class UtilityFunctionTests
{
    [Fact]
    public void Coalesce_FirstNonNull()
    {
        CoalesceFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.Scalar),
            DataValue.Null(DataKind.Scalar),
            DataValue.FromScalar(42)
        ]);
        Assert.Equal(42f, result.AsScalar());
    }

    [Fact]
    public void Coalesce_FirstIsNotNull()
    {
        CoalesceFunction function = new();
        DataValue result = function.Execute([DataValue.FromScalar(1), DataValue.FromScalar(2)]);
        Assert.Equal(1f, result.AsScalar());
    }

    [Fact]
    public void Coalesce_AllNull_ReturnsNull()
    {
        CoalesceFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.Scalar), DataValue.Null(DataKind.Scalar)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Greatest_MultipleScalars()
    {
        GreatestFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromScalar(3),
            DataValue.FromScalar(7),
            DataValue.FromScalar(1)
        ]);
        Assert.Equal(7f, result.AsScalar());
    }

    [Fact]
    public void Greatest_WithNull_SkipsNull()
    {
        GreatestFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.Scalar),
            DataValue.FromScalar(5),
            DataValue.FromScalar(3)
        ]);
        Assert.Equal(5f, result.AsScalar());
    }

    [Fact]
    public void Greatest_AllNull_ReturnsNull()
    {
        GreatestFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.Scalar), DataValue.Null(DataKind.Scalar)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Least_MultipleScalars()
    {
        LeastFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromScalar(3),
            DataValue.FromScalar(7),
            DataValue.FromScalar(1)
        ]);
        Assert.Equal(1f, result.AsScalar());
    }

    [Fact]
    public void IsNan_WithNaN()
    {
        IsNanFunction function = new();
        Assert.Equal(1f, function.Execute([DataValue.FromScalar(float.NaN)]).AsScalar());
    }

    [Fact]
    public void IsNan_WithFinite()
    {
        IsNanFunction function = new();
        Assert.Equal(0f, function.Execute([DataValue.FromScalar(5)]).AsScalar());
    }

    [Fact]
    public void IsFinite_WithFinite()
    {
        IsFiniteFunction function = new();
        Assert.Equal(1f, function.Execute([DataValue.FromScalar(5)]).AsScalar());
    }

    [Fact]
    public void IsFinite_WithInfinity()
    {
        IsFiniteFunction function = new();
        Assert.Equal(0f, function.Execute([DataValue.FromScalar(float.PositiveInfinity)]).AsScalar());
    }

    [Fact]
    public void IsFinite_WithNaN()
    {
        IsFiniteFunction function = new();
        Assert.Equal(0f, function.Execute([DataValue.FromScalar(float.NaN)]).AsScalar());
    }

    [Fact]
    public void IfNull_ValueNotNull_ReturnsValue()
    {
        IfNullFunction function = new();
        DataValue result = function.Execute([DataValue.FromScalar(5), DataValue.FromScalar(10)]);
        Assert.Equal(5f, result.AsScalar());
    }

    [Fact]
    public void IfNull_ValueNull_ReturnsDefault()
    {
        IfNullFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.Scalar), DataValue.FromScalar(10)]);
        Assert.Equal(10f, result.AsScalar());
    }

    [Fact]
    public void Random_ReturnsBetweenZeroAndOne()
    {
        RandomFunction function = new();
        Assert.Equal(DataKind.Scalar, function.ValidateArguments([]));
        float result = function.Execute([]).AsScalar();
        Assert.True(result >= 0f && result < 1f);
    }

    [Fact]
    public void Random_WithArgs_Throws()
    {
        Assert.Throws<ArgumentException>(() => new RandomFunction().ValidateArguments([DataKind.Scalar]));
    }

    [Fact]
    public void IsNan_Null_ReturnsNull()
    {
        Assert.True(new IsNanFunction().Execute([DataValue.Null(DataKind.Scalar)]).IsNull);
    }

    [Fact]
    public void Greatest_Validate_LessThan2_Throws()
    {
        Assert.Throws<ArgumentException>(() => new GreatestFunction().ValidateArguments([DataKind.Scalar]));
    }

    [Fact]
    public void Greatest_Validate_NonScalar_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new GreatestFunction().ValidateArguments([DataKind.Scalar, DataKind.Vector]));
    }

    [Fact]
    public void Least_WithNull_SkipsNull()
    {
        LeastFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.Scalar),
            DataValue.FromScalar(5),
            DataValue.FromScalar(3)
        ]);
        Assert.Equal(3f, result.AsScalar());
    }
}
