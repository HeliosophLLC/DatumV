using DatumIngest.Functions.Math;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Math;

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

    // ── iif ────────────────────────────────────────────────

    [Fact]
    public void Iif_ScalarTruthy_ReturnsThenValue()
    {
        IifFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromScalar(1),
            DataValue.FromString("yes"),
            DataValue.FromString("no")
        ]);
        Assert.Equal("yes", result.AsString());
    }

    [Fact]
    public void Iif_ScalarZero_ReturnsElseValue()
    {
        IifFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromScalar(0),
            DataValue.FromString("yes"),
            DataValue.FromString("no")
        ]);
        Assert.Equal("no", result.AsString());
    }

    [Fact]
    public void Iif_BooleanTrue_ReturnsThenValue()
    {
        IifFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromBoolean(true),
            DataValue.FromScalar(10),
            DataValue.FromScalar(20)
        ]);
        Assert.Equal(10f, result.AsScalar());
    }

    [Fact]
    public void Iif_BooleanFalse_ReturnsElseValue()
    {
        IifFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromBoolean(false),
            DataValue.FromScalar(10),
            DataValue.FromScalar(20)
        ]);
        Assert.Equal(20f, result.AsScalar());
    }

    [Fact]
    public void Iif_NullCondition_ReturnsElseValue()
    {
        IifFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.Scalar),
            DataValue.FromString("yes"),
            DataValue.FromString("no")
        ]);
        Assert.Equal("no", result.AsString());
    }

    [Fact]
    public void Iif_NonZeroScalar_ReturnsThenValue()
    {
        IifFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromScalar(-5),
            DataValue.FromScalar(100),
            DataValue.FromScalar(200)
        ]);
        Assert.Equal(100f, result.AsScalar());
    }

    [Fact]
    public void Iif_Validate_WrongArgCount_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new IifFunction().ValidateArguments([DataKind.Scalar, DataKind.String]));
    }

    [Fact]
    public void Iif_Validate_NonScalarCondition_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new IifFunction().ValidateArguments([DataKind.String, DataKind.Scalar, DataKind.Scalar]));
    }

    [Fact]
    public void Iif_Validate_MismatchedThenElse_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new IifFunction().ValidateArguments([DataKind.Scalar, DataKind.String, DataKind.Scalar]));
    }

    [Fact]
    public void Iif_Validate_ReturnsSecondArgKind()
    {
        DataKind result = new IifFunction().ValidateArguments([DataKind.Scalar, DataKind.Vector, DataKind.Vector]);
        Assert.Equal(DataKind.Vector, result);
    }
}
