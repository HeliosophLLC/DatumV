using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Assertion;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Scalar.Assertion;

/// <summary>
/// Behavioural tests for the <c>assert_*</c> scalar function family. Each
/// assert is covered with: pass-through on success, throw on failure,
/// pass-through on null, and custom-message override. Registration in
/// <see cref="FunctionRegistry.CreateDefault"/> is covered once per
/// function near the bottom.
/// </summary>
public sealed class AssertionFunctionTests
{
    // ─────────────────────────── assert_not_null ───────────────────────────

    [Fact]
    public void AssertNotNull_NonNull_ReturnsInput()
    {
        ValueRef result = Invoke<AssertNotNullFunction>(ValueRef.FromInt32(42));
        Assert.Equal(42, result.AsInt32());
    }

    [Fact]
    public void AssertNotNull_Null_ThrowsDefaultMessage()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => Invoke<AssertNotNullFunction>(ValueRef.Null(DataKind.Int32)));
        Assert.Contains("null", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AssertNotNull_Null_UsesCustomMessage()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => Invoke<AssertNotNullFunction>(
                ValueRef.Null(DataKind.Int32),
                ValueRef.FromString("name is required")));
        Assert.Equal("name is required", ex.Message);
    }

    // ───────────────────────────── assert_equal ────────────────────────────

    [Fact]
    public void AssertEqual_Equal_ReturnsInput()
    {
        ValueRef result = Invoke<AssertEqualFunction>(
            ValueRef.FromInt32(5), ValueRef.FromInt32(5));
        Assert.Equal(5, result.AsInt32());
    }

    [Fact]
    public void AssertEqual_NotEqual_Throws()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => Invoke<AssertEqualFunction>(
                ValueRef.FromInt32(5), ValueRef.FromInt32(10)));
        Assert.Contains("5", ex.Message);
        Assert.Contains("10", ex.Message);
    }

    [Fact]
    public void AssertEqual_CrossNumericKinds_ComparesByValue()
    {
        // Int32 5 == Int64 5 via the same cross-kind coercion SQL = uses.
        ValueRef result = Invoke<AssertEqualFunction>(
            ValueRef.FromInt32(5), ValueRef.FromInt64(5L));
        Assert.Equal(5, result.AsInt32());
    }

    [Fact]
    public void AssertEqual_NullValue_PassesThrough()
    {
        ValueRef result = Invoke<AssertEqualFunction>(
            ValueRef.Null(DataKind.Int32), ValueRef.FromInt32(5));
        Assert.True(result.IsNull);
    }

    [Fact]
    public void AssertEqual_CustomMessage_Overrides()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => Invoke<AssertEqualFunction>(
                ValueRef.FromInt32(5),
                ValueRef.FromInt32(10),
                ValueRef.FromString("expected exact match")));
        Assert.Equal("expected exact match", ex.Message);
    }

    // ─────────────────────────── assert_not_equal ──────────────────────────

    [Fact]
    public void AssertNotEqual_Different_ReturnsInput()
    {
        ValueRef result = Invoke<AssertNotEqualFunction>(
            ValueRef.FromInt32(5), ValueRef.FromInt32(10));
        Assert.Equal(5, result.AsInt32());
    }

    [Fact]
    public void AssertNotEqual_Equal_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => Invoke<AssertNotEqualFunction>(
                ValueRef.FromInt32(5), ValueRef.FromInt32(5)));
    }

    // ────────────────────── assert_greater_than & family ───────────────────

    [Fact]
    public void AssertGreaterThan_Greater_ReturnsInput()
    {
        ValueRef result = Invoke<AssertGreaterThanFunction>(
            ValueRef.FromInt32(10), ValueRef.FromInt32(5));
        Assert.Equal(10, result.AsInt32());
    }

    [Fact]
    public void AssertGreaterThan_Equal_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => Invoke<AssertGreaterThanFunction>(
                ValueRef.FromInt32(5), ValueRef.FromInt32(5)));
    }

    [Fact]
    public void AssertGreaterThan_NullValue_PassesThrough()
    {
        ValueRef result = Invoke<AssertGreaterThanFunction>(
            ValueRef.Null(DataKind.Int32), ValueRef.FromInt32(5));
        Assert.True(result.IsNull);
    }

    [Fact]
    public void AssertGreaterOrEqual_Equal_ReturnsInput()
    {
        ValueRef result = Invoke<AssertGreaterOrEqualFunction>(
            ValueRef.FromInt32(5), ValueRef.FromInt32(5));
        Assert.Equal(5, result.AsInt32());
    }

    [Fact]
    public void AssertGreaterOrEqual_Less_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => Invoke<AssertGreaterOrEqualFunction>(
                ValueRef.FromInt32(4), ValueRef.FromInt32(5)));
    }

    [Fact]
    public void AssertLessThan_Less_ReturnsInput()
    {
        ValueRef result = Invoke<AssertLessThanFunction>(
            ValueRef.FromInt32(3), ValueRef.FromInt32(5));
        Assert.Equal(3, result.AsInt32());
    }

    [Fact]
    public void AssertLessThan_Greater_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => Invoke<AssertLessThanFunction>(
                ValueRef.FromInt32(10), ValueRef.FromInt32(5)));
    }

    [Fact]
    public void AssertLessOrEqual_Equal_ReturnsInput()
    {
        ValueRef result = Invoke<AssertLessOrEqualFunction>(
            ValueRef.FromInt32(5), ValueRef.FromInt32(5));
        Assert.Equal(5, result.AsInt32());
    }

    [Fact]
    public void AssertLessOrEqual_Greater_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => Invoke<AssertLessOrEqualFunction>(
                ValueRef.FromInt32(10), ValueRef.FromInt32(5)));
    }

    // ──────────────────────────── assert_between ───────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    public void AssertBetween_InRangeInclusive_ReturnsInput(int value)
    {
        ValueRef result = Invoke<AssertBetweenFunction>(
            ValueRef.FromInt32(value), ValueRef.FromInt32(1), ValueRef.FromInt32(10));
        Assert.Equal(value, result.AsInt32());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void AssertBetween_OutOfRange_Throws(int value)
    {
        Assert.Throws<InvalidOperationException>(
            () => Invoke<AssertBetweenFunction>(
                ValueRef.FromInt32(value), ValueRef.FromInt32(1), ValueRef.FromInt32(10)));
    }

    // ────────────────────── assert_true / assert_false ─────────────────────

    [Fact]
    public void AssertTrue_True_ReturnsInput()
    {
        ValueRef result = Invoke<AssertTrueFunction>(ValueRef.FromBoolean(true));
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void AssertTrue_False_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => Invoke<AssertTrueFunction>(ValueRef.FromBoolean(false)));
    }

    [Fact]
    public void AssertFalse_False_ReturnsInput()
    {
        ValueRef result = Invoke<AssertFalseFunction>(ValueRef.FromBoolean(false));
        Assert.False(result.AsBoolean());
    }

    [Fact]
    public void AssertFalse_True_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => Invoke<AssertFalseFunction>(ValueRef.FromBoolean(true)));
    }

    // ──────────────────────────── assert_finite ────────────────────────────

    [Fact]
    public void AssertFinite_Finite_ReturnsInput()
    {
        ValueRef result = Invoke<AssertFiniteFunction>(ValueRef.FromFloat64(3.14));
        Assert.Equal(3.14, result.AsFloat64());
    }

    [Fact]
    public void AssertFinite_NaN_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => Invoke<AssertFiniteFunction>(ValueRef.FromFloat64(double.NaN)));
    }

    [Fact]
    public void AssertFinite_PositiveInfinity_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => Invoke<AssertFiniteFunction>(ValueRef.FromFloat64(double.PositiveInfinity)));
    }

    [Fact]
    public void AssertFinite_NegativeInfinity_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => Invoke<AssertFiniteFunction>(ValueRef.FromFloat64(double.NegativeInfinity)));
    }

    [Fact]
    public void AssertFinite_RejectsInt32_AtValidation()
    {
        // Int kinds aren't part of the assert_finite signature; the assertion
        // would be meaningless. Reject at metadata-validation time.
        Assert.Throws<FunctionArgumentException>(
            () => new AssertFiniteFunction().ValidateArguments([DataKind.Int32]));
    }

    // ────────────────── assert_positive / assert_non_negative ──────────────

    [Fact]
    public void AssertPositive_Positive_ReturnsInput()
    {
        ValueRef result = Invoke<AssertPositiveFunction>(ValueRef.FromInt32(5));
        Assert.Equal(5, result.AsInt32());
    }

    [Fact]
    public void AssertPositive_Zero_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => Invoke<AssertPositiveFunction>(ValueRef.FromInt32(0)));
    }

    [Fact]
    public void AssertPositive_Negative_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => Invoke<AssertPositiveFunction>(ValueRef.FromInt32(-5)));
    }

    [Fact]
    public void AssertNonNegative_Zero_ReturnsInput()
    {
        ValueRef result = Invoke<AssertNonNegativeFunction>(ValueRef.FromInt32(0));
        Assert.Equal(0, result.AsInt32());
    }

    [Fact]
    public void AssertNonNegative_Negative_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => Invoke<AssertNonNegativeFunction>(ValueRef.FromInt32(-1)));
    }

    [Fact]
    public void AssertPositive_UnsignedZero_Throws()
    {
        // Unsigned kinds can only fail at zero, not negative — but they still
        // fail at zero so the assert is meaningful.
        Assert.Throws<InvalidOperationException>(
            () => Invoke<AssertPositiveFunction>(ValueRef.FromUInt32(0)));
    }

    // ──────────────────────────── assert_matches ───────────────────────────

    [Fact]
    public void AssertMatches_Match_ReturnsInput()
    {
        ValueRef result = Invoke<AssertMatchesFunction>(
            ValueRef.FromString("foo123"), ValueRef.FromString(@"\d+"));
        Assert.Equal("foo123", result.AsString());
    }

    [Fact]
    public void AssertMatches_NoMatch_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => Invoke<AssertMatchesFunction>(
                ValueRef.FromString("foo"), ValueRef.FromString(@"^\d+$")));
    }

    // ───────────────────── assert_starts_with / ends_with ──────────────────

    [Fact]
    public void AssertStartsWith_Match_ReturnsInput()
    {
        ValueRef result = Invoke<AssertStartsWithFunction>(
            ValueRef.FromString("hello world"), ValueRef.FromString("hello"));
        Assert.Equal("hello world", result.AsString());
    }

    [Fact]
    public void AssertStartsWith_NoMatch_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => Invoke<AssertStartsWithFunction>(
                ValueRef.FromString("hello world"), ValueRef.FromString("world")));
    }

    [Fact]
    public void AssertEndsWith_Match_ReturnsInput()
    {
        ValueRef result = Invoke<AssertEndsWithFunction>(
            ValueRef.FromString("hello world"), ValueRef.FromString("world"));
        Assert.Equal("hello world", result.AsString());
    }

    [Fact]
    public void AssertEndsWith_NoMatch_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => Invoke<AssertEndsWithFunction>(
                ValueRef.FromString("hello world"), ValueRef.FromString("hello")));
    }

    // ─────────────────────────── assert_non_empty ──────────────────────────

    [Fact]
    public void AssertNonEmpty_NonEmptyString_ReturnsInput()
    {
        ValueRef result = Invoke<AssertNonEmptyFunction>(ValueRef.FromString("x"));
        Assert.Equal("x", result.AsString());
    }

    [Fact]
    public void AssertNonEmpty_EmptyString_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => Invoke<AssertNonEmptyFunction>(ValueRef.FromString(string.Empty)));
    }

    [Fact]
    public void AssertNonEmpty_NonEmptyArray_ReturnsInput()
    {
        ValueRef array = ValueRef.FromArray(DataKind.Int32, [ValueRef.FromInt32(1)]);
        ValueRef result = Invoke<AssertNonEmptyFunction>(array);
        Assert.Equal(1, result.GetArrayLength());
    }

    [Fact]
    public void AssertNonEmpty_EmptyArray_Throws()
    {
        ValueRef array = ValueRef.FromArray(DataKind.Int32, []);
        Assert.Throws<InvalidOperationException>(
            () => Invoke<AssertNonEmptyFunction>(array));
    }

    // ──────────────────────────── assert_length ────────────────────────────

    [Fact]
    public void AssertLength_StringMatch_ReturnsInput()
    {
        ValueRef result = Invoke<AssertLengthFunction>(
            ValueRef.FromString("abc"), ValueRef.FromInt32(3));
        Assert.Equal("abc", result.AsString());
    }

    [Fact]
    public void AssertLength_StringMismatch_Throws()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => Invoke<AssertLengthFunction>(
                ValueRef.FromString("abc"), ValueRef.FromInt32(5)));
        Assert.Contains("3", ex.Message);
        Assert.Contains("5", ex.Message);
    }

    [Fact]
    public void AssertLength_ArrayMatch_ReturnsInput()
    {
        ValueRef array = ValueRef.FromArray(
            DataKind.Int32,
            [ValueRef.FromInt32(1), ValueRef.FromInt32(2)]);
        ValueRef result = Invoke<AssertLengthFunction>(array, ValueRef.FromInt32(2));
        Assert.Equal(2, result.GetArrayLength());
    }

    // ─────────────────────── assert_in / assert_not_in ─────────────────────

    [Fact]
    public void AssertIn_Member_ReturnsInput()
    {
        ValueRef choices = ValueRef.FromArray(
            DataKind.Int32,
            [ValueRef.FromInt32(1), ValueRef.FromInt32(2), ValueRef.FromInt32(3)]);
        ValueRef result = Invoke<AssertInFunction>(ValueRef.FromInt32(2), choices);
        Assert.Equal(2, result.AsInt32());
    }

    [Fact]
    public void AssertIn_NotMember_Throws()
    {
        ValueRef choices = ValueRef.FromArray(
            DataKind.Int32,
            [ValueRef.FromInt32(1), ValueRef.FromInt32(2), ValueRef.FromInt32(3)]);
        Assert.Throws<InvalidOperationException>(
            () => Invoke<AssertInFunction>(ValueRef.FromInt32(99), choices));
    }

    [Fact]
    public void AssertNotIn_NotMember_ReturnsInput()
    {
        ValueRef forbidden = ValueRef.FromArray(
            DataKind.Int32,
            [ValueRef.FromInt32(1), ValueRef.FromInt32(2)]);
        ValueRef result = Invoke<AssertNotInFunction>(ValueRef.FromInt32(3), forbidden);
        Assert.Equal(3, result.AsInt32());
    }

    [Fact]
    public void AssertNotIn_Member_Throws()
    {
        ValueRef forbidden = ValueRef.FromArray(
            DataKind.Int32,
            [ValueRef.FromInt32(1), ValueRef.FromInt32(2)]);
        Assert.Throws<InvalidOperationException>(
            () => Invoke<AssertNotInFunction>(ValueRef.FromInt32(1), forbidden));
    }

    // ─────────────────────────── Registry coverage ─────────────────────────

    [Theory]
    [InlineData("assert_not_null")]
    [InlineData("assert_equal")]
    [InlineData("assert_not_equal")]
    [InlineData("assert_greater_than")]
    [InlineData("assert_greater_or_equal")]
    [InlineData("assert_less_than")]
    [InlineData("assert_less_or_equal")]
    [InlineData("assert_between")]
    [InlineData("assert_true")]
    [InlineData("assert_false")]
    [InlineData("assert_finite")]
    [InlineData("assert_positive")]
    [InlineData("assert_non_negative")]
    [InlineData("assert_matches")]
    [InlineData("assert_starts_with")]
    [InlineData("assert_ends_with")]
    [InlineData("assert_non_empty")]
    [InlineData("assert_length")]
    [InlineData("assert_in")]
    [InlineData("assert_not_in")]
    public void RegisteredInDefaultRegistry(string name)
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        IScalarFunction? function = registry.TryGetScalar(name);
        Assert.NotNull(function);

        FunctionDescriptor? descriptor = registry.TryGetScalarDescriptor(name);
        Assert.NotNull(descriptor);
        Assert.Equal(FunctionCategory.Assertion, descriptor.Category);
    }

    private static ValueRef Invoke<T>(params ValueRef[] arguments)
        where T : IFunction, IScalarFunction, new()
    {
        T function = new();
        EvaluationFrame frame = default;
        return function.ExecuteAsync(arguments, frame, default).GetAwaiter().GetResult();
    }
}
