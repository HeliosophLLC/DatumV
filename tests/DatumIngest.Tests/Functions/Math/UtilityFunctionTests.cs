using DatumIngest.Functions.Math;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Math;

public class UtilityFunctionTests
{
    [Fact]
    public void Nullif_EqualValues_ReturnsNull()
    {
        NullifFunction function = new();
        DataValue result = function.Execute([DataValue.FromFloat32(0f), DataValue.FromFloat32(0f)]);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Float32, result.Kind);
    }

    [Fact]
    public void Nullif_UnequalValues_ReturnsFirst()
    {
        NullifFunction function = new();
        DataValue result = function.Execute([DataValue.FromFloat32(5f), DataValue.FromFloat32(0f)]);
        Assert.Equal(5f, result.AsFloat32());
    }

    [Fact]
    public void Nullif_FirstNull_ReturnsNull()
    {
        NullifFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.Float32), DataValue.FromFloat32(0f)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Nullif_SecondNull_ReturnsFirst()
    {
        NullifFunction function = new();
        DataValue result = function.Execute([DataValue.FromFloat32(5f), DataValue.Null(DataKind.Float32)]);
        Assert.Equal(5f, result.AsFloat32());
    }

    [Fact]
    public void Nullif_EqualStrings_ReturnsNull()
    {
        NullifFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("foo"), DataValue.FromString("foo")]);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.String, result.Kind);
    }

    [Fact]
    public void Nullif_UnequalStrings_ReturnsFirst()
    {
        NullifFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("foo"), DataValue.FromString("bar")]);
        Assert.Equal("foo", result.AsString());
    }

    [Fact]
    public void Nullif_MismatchedKinds_Throws()
    {
        NullifFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Float32, DataKind.String]));
    }

    [Fact]
    public void Nullif_WrongArgumentCount_Throws()
    {
        NullifFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Float32]));
    }

    [Fact]
    public void Coalesce_FirstNonNull()
    {
        CoalesceFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.Float32),
            DataValue.Null(DataKind.Float32),
            DataValue.FromFloat32(42)
        ]);
        Assert.Equal(42f, result.AsFloat32());
    }

    [Fact]
    public void Coalesce_FirstIsNotNull()
    {
        CoalesceFunction function = new();
        DataValue result = function.Execute([DataValue.FromFloat32(1), DataValue.FromFloat32(2)]);
        Assert.Equal(1f, result.AsFloat32());
    }

    [Fact]
    public void Coalesce_AllNull_ReturnsNull()
    {
        CoalesceFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.Float32), DataValue.Null(DataKind.Float32)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Greatest_MultipleScalars()
    {
        GreatestFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromFloat32(3),
            DataValue.FromFloat32(7),
            DataValue.FromFloat32(1)
        ]);
        Assert.Equal(7f, result.AsFloat32());
    }

    [Fact]
    public void Greatest_WithNull_SkipsNull()
    {
        GreatestFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.Float32),
            DataValue.FromFloat32(5),
            DataValue.FromFloat32(3)
        ]);
        Assert.Equal(5f, result.AsFloat32());
    }

    [Fact]
    public void Greatest_AllNull_ReturnsNull()
    {
        GreatestFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.Float32), DataValue.Null(DataKind.Float32)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Greatest_Strings_ReturnsMax()
    {
        GreatestFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("banana"),
            DataValue.FromString("apple"),
            DataValue.FromString("cherry")
        ]);
        Assert.Equal("cherry", result.AsString());
    }

    [Fact]
    public void Greatest_Strings_WithNull_SkipsNull()
    {
        GreatestFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.String),
            DataValue.FromString("beta"),
            DataValue.FromString("alpha")
        ]);
        Assert.Equal("beta", result.AsString());
    }

    [Fact]
    public void Greatest_Strings_AllNull_ReturnsNull()
    {
        GreatestFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.String), DataValue.Null(DataKind.String)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Greatest_Strings_Validate_ReturnsString()
    {
        DataKind result = new GreatestFunction().ValidateArguments([DataKind.String, DataKind.String]);
        Assert.Equal(DataKind.String, result);
    }

    [Fact]
    public void Least_MultipleScalars()
    {
        LeastFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromFloat32(3),
            DataValue.FromFloat32(7),
            DataValue.FromFloat32(1)
        ]);
        Assert.Equal(1f, result.AsFloat32());
    }

    [Fact]
    public void IsNan_WithNaN()
    {
        IsNanFunction function = new();
        Assert.Equal(1f, function.Execute([DataValue.FromFloat32(float.NaN)]).AsFloat32());
    }

    [Fact]
    public void IsNan_WithFinite()
    {
        IsNanFunction function = new();
        Assert.Equal(0f, function.Execute([DataValue.FromFloat32(5)]).AsFloat32());
    }

    [Fact]
    public void IsFinite_WithFinite()
    {
        IsFiniteFunction function = new();
        Assert.Equal(1f, function.Execute([DataValue.FromFloat32(5)]).AsFloat32());
    }

    [Fact]
    public void IsFinite_WithInfinity()
    {
        IsFiniteFunction function = new();
        Assert.Equal(0f, function.Execute([DataValue.FromFloat32(float.PositiveInfinity)]).AsFloat32());
    }

    [Fact]
    public void IsFinite_WithNaN()
    {
        IsFiniteFunction function = new();
        Assert.Equal(0f, function.Execute([DataValue.FromFloat32(float.NaN)]).AsFloat32());
    }

    // ───────────────── IsEvenFunction ─────────────────

    [Fact]
    public void IsEven_EvenInteger_ReturnsOne()
    {
        IsEvenFunction function = new();
        Assert.Equal(1f, function.Execute([DataValue.FromFloat32(4)]).AsFloat32());
    }

    [Fact]
    public void IsEven_OddInteger_ReturnsZero()
    {
        IsEvenFunction function = new();
        Assert.Equal(0f, function.Execute([DataValue.FromFloat32(3)]).AsFloat32());
    }

    [Fact]
    public void IsEven_Zero_ReturnsOne()
    {
        IsEvenFunction function = new();
        Assert.Equal(1f, function.Execute([DataValue.FromFloat32(0)]).AsFloat32());
    }

    [Fact]
    public void IsEven_NegativeEven_ReturnsOne()
    {
        IsEvenFunction function = new();
        Assert.Equal(1f, function.Execute([DataValue.FromFloat32(-6)]).AsFloat32());
    }

    [Fact]
    public void IsEven_NonInteger_ReturnsZero()
    {
        IsEvenFunction function = new();
        Assert.Equal(0f, function.Execute([DataValue.FromFloat32(2.5f)]).AsFloat32());
    }

    [Fact]
    public void IsEven_Null_ReturnsNull()
    {
        Assert.True(new IsEvenFunction().Execute([DataValue.Null(DataKind.Float32)]).IsNull);
    }

    [Fact]
    public void IsEven_UInt8_Even()
    {
        IsEvenFunction function = new();
        Assert.Equal(1f, function.Execute([DataValue.FromUInt8(10)]).AsFloat32());
    }

    // ───────────────── IsOddFunction ─────────────────

    [Fact]
    public void IsOdd_OddInteger_ReturnsOne()
    {
        IsOddFunction function = new();
        Assert.Equal(1f, function.Execute([DataValue.FromFloat32(3)]).AsFloat32());
    }

    [Fact]
    public void IsOdd_EvenInteger_ReturnsZero()
    {
        IsOddFunction function = new();
        Assert.Equal(0f, function.Execute([DataValue.FromFloat32(4)]).AsFloat32());
    }

    [Fact]
    public void IsOdd_Zero_ReturnsZero()
    {
        IsOddFunction function = new();
        Assert.Equal(0f, function.Execute([DataValue.FromFloat32(0)]).AsFloat32());
    }

    [Fact]
    public void IsOdd_NegativeOdd_ReturnsOne()
    {
        IsOddFunction function = new();
        Assert.Equal(1f, function.Execute([DataValue.FromFloat32(-7)]).AsFloat32());
    }

    [Fact]
    public void IsOdd_NonInteger_ReturnsZero()
    {
        IsOddFunction function = new();
        Assert.Equal(0f, function.Execute([DataValue.FromFloat32(3.5f)]).AsFloat32());
    }

    [Fact]
    public void IsOdd_Null_ReturnsNull()
    {
        Assert.True(new IsOddFunction().Execute([DataValue.Null(DataKind.Float32)]).IsNull);
    }

    [Fact]
    public void IsOdd_UInt8_Odd()
    {
        IsOddFunction function = new();
        Assert.Equal(1f, function.Execute([DataValue.FromUInt8(7)]).AsFloat32());
    }

    [Fact]
    public void IfNull_ValueNotNull_ReturnsValue()
    {
        IfNullFunction function = new();
        DataValue result = function.Execute([DataValue.FromFloat32(5), DataValue.FromFloat32(10)]);
        Assert.Equal(5f, result.AsFloat32());
    }

    [Fact]
    public void IfNull_ValueNull_ReturnsDefault()
    {
        IfNullFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.Float32), DataValue.FromFloat32(10)]);
        Assert.Equal(10f, result.AsFloat32());
    }

    [Fact]
    public void Random_ReturnsBetweenZeroAndOne()
    {
        RandomFunction function = new();
        Assert.Equal(DataKind.Float32, function.ValidateArguments([]));
        float result = function.Execute([]).AsFloat32();
        Assert.True(result >= 0f && result < 1f);
    }

    [Fact]
    public void Random_WithArgs_Throws()
    {
        Assert.Throws<ArgumentException>(() => new RandomFunction().ValidateArguments([DataKind.Float32]));
    }

    [Fact]
    public void IsNan_Null_ReturnsNull()
    {
        Assert.True(new IsNanFunction().Execute([DataValue.Null(DataKind.Float32)]).IsNull);
    }

    [Fact]
    public void Greatest_Validate_LessThan2_Throws()
    {
        Assert.Throws<ArgumentException>(() => new GreatestFunction().ValidateArguments([DataKind.Float32]));
    }

    [Fact]
    public void Greatest_Validate_NonScalar_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new GreatestFunction().ValidateArguments([DataKind.Float32, DataKind.Vector]));
    }

    [Fact]
    public void Least_WithNull_SkipsNull()
    {
        LeastFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.Float32),
            DataValue.FromFloat32(5),
            DataValue.FromFloat32(3)
        ]);
        Assert.Equal(3f, result.AsFloat32());
    }

    [Fact]
    public void Least_Strings_ReturnsMin()
    {
        LeastFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("banana"),
            DataValue.FromString("apple"),
            DataValue.FromString("cherry")
        ]);
        Assert.Equal("apple", result.AsString());
    }

    [Fact]
    public void Least_Strings_WithNull_SkipsNull()
    {
        LeastFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.String),
            DataValue.FromString("beta"),
            DataValue.FromString("alpha")
        ]);
        Assert.Equal("alpha", result.AsString());
    }

    [Fact]
    public void Least_Strings_AllNull_ReturnsNull()
    {
        LeastFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.String), DataValue.Null(DataKind.String)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Least_Strings_Validate_ReturnsString()
    {
        DataKind result = new LeastFunction().ValidateArguments([DataKind.String, DataKind.String]);
        Assert.Equal(DataKind.String, result);
    }

    // ── iif ────────────────────────────────────────────────

    [Fact]
    public void Iif_ScalarTruthy_ReturnsThenValue()
    {
        IifFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromFloat32(1),
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
            DataValue.FromFloat32(0),
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
            DataValue.FromFloat32(10),
            DataValue.FromFloat32(20)
        ]);
        Assert.Equal(10f, result.AsFloat32());
    }

    [Fact]
    public void Iif_BooleanFalse_ReturnsElseValue()
    {
        IifFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromBoolean(false),
            DataValue.FromFloat32(10),
            DataValue.FromFloat32(20)
        ]);
        Assert.Equal(20f, result.AsFloat32());
    }

    [Fact]
    public void Iif_NullCondition_ReturnsElseValue()
    {
        IifFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.Float32),
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
            DataValue.FromFloat32(-5),
            DataValue.FromFloat32(100),
            DataValue.FromFloat32(200)
        ]);
        Assert.Equal(100f, result.AsFloat32());
    }

    [Fact]
    public void Iif_Validate_WrongArgCount_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new IifFunction().ValidateArguments([DataKind.Float32, DataKind.String]));
    }

    [Fact]
    public void Iif_Validate_NonScalarCondition_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new IifFunction().ValidateArguments([DataKind.String, DataKind.Float32, DataKind.Float32]));
    }

    [Fact]
    public void Iif_Validate_MismatchedThenElse_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new IifFunction().ValidateArguments([DataKind.Float32, DataKind.String, DataKind.Float32]));
    }

    [Fact]
    public void Iif_Validate_ReturnsSecondArgKind()
    {
        DataKind result = new IifFunction().ValidateArguments([DataKind.Float32, DataKind.Vector, DataKind.Vector]);
        Assert.Equal(DataKind.Vector, result);
    }

    // ── choose ─────────────────────────────────────────────

    [Fact]
    public void Choose_Index1_ReturnsFirstValue()
    {
        ChooseFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromFloat32(1),
            DataValue.FromString("alpha"),
            DataValue.FromString("beta"),
            DataValue.FromString("gamma")
        ]);
        Assert.Equal("alpha", result.AsString());
    }

    [Fact]
    public void Choose_Index3_ReturnsThirdValue()
    {
        ChooseFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromFloat32(3),
            DataValue.FromString("alpha"),
            DataValue.FromString("beta"),
            DataValue.FromString("gamma")
        ]);
        Assert.Equal("gamma", result.AsString());
    }

    [Fact]
    public void Choose_IndexZero_ReturnsNull()
    {
        ChooseFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromFloat32(0),
            DataValue.FromFloat32(10),
            DataValue.FromFloat32(20)
        ]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Choose_IndexBeyondRange_ReturnsNull()
    {
        ChooseFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromFloat32(5),
            DataValue.FromFloat32(10),
            DataValue.FromFloat32(20)
        ]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Choose_NullIndex_ReturnsNull()
    {
        ChooseFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.Float32),
            DataValue.FromFloat32(10),
            DataValue.FromFloat32(20)
        ]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Choose_SelectedValueIsNull_ReturnsNull()
    {
        ChooseFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromFloat32(2),
            DataValue.FromString("alpha"),
            DataValue.Null(DataKind.String),
            DataValue.FromString("gamma")
        ]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Choose_UInt8Index_Works()
    {
        ChooseFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromUInt8(1),
            DataValue.FromFloat32(100),
            DataValue.FromFloat32(200)
        ]);
        Assert.Equal(100f, result.AsFloat32());
    }

    [Fact]
    public void Choose_Validate_TooFewArgs_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new ChooseFunction().ValidateArguments([DataKind.Float32]));
    }

    [Fact]
    public void Choose_Validate_NonNumericIndex_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new ChooseFunction().ValidateArguments([DataKind.String, DataKind.String, DataKind.String]));
    }

    [Fact]
    public void Choose_Validate_MismatchedValueKinds_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new ChooseFunction().ValidateArguments([DataKind.Float32, DataKind.String, DataKind.Float32]));
    }

    [Fact]
    public void Choose_Validate_ReturnsValueKind()
    {
        DataKind result = new ChooseFunction().ValidateArguments([DataKind.Float32, DataKind.String, DataKind.String]);
        Assert.Equal(DataKind.String, result);
    }
}
