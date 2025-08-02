using DatumIngest.Functions;
using DatumIngest.Functions.Scalar;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions;

/// <summary>
/// Tests for the array scalar functions: <see cref="ArrayLengthFunction"/>,
/// <see cref="ArrayJoinFunction"/>, <see cref="ArrayContainsFunction"/>,
/// <see cref="ArrayPositionFunction"/>, and <see cref="ArrayConstructorFunction"/>.
/// Also tests <see cref="LenFunction"/> Array support.
/// </summary>
public class ArrayFunctionTests
{
    // ───────── helper ─────────

    private static DataValue MakeScalarArray(params float[] values) =>
        DataValue.FromArray(DataKind.Scalar, values.Select(DataValue.FromScalar).ToArray());

    private static DataValue MakeStringArray(params string[] values) =>
        DataValue.FromArray(DataKind.String, values.Select(DataValue.FromString).ToArray());

    // ───────────────── ARRAY_LENGTH ─────────────────

    [Fact]
    public void ArrayLength_ReturnsElementCount()
    {
        ArrayLengthFunction function = new();
        DataValue result = function.Execute([MakeScalarArray(1f, 2f, 3f)]);
        Assert.Equal(3f, result.AsScalar());
    }

    [Fact]
    public void ArrayLength_EmptyArray_ReturnsZero()
    {
        ArrayLengthFunction function = new();
        DataValue result = function.Execute([DataValue.FromArray(DataKind.Scalar, [])]);
        Assert.Equal(0f, result.AsScalar());
    }

    [Fact]
    public void ArrayLength_NullInput_ReturnsNull()
    {
        ArrayLengthFunction function = new();
        DataValue result = function.Execute([DataValue.NullArray(DataKind.Scalar)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void ArrayLength_WrongKind_Throws()
    {
        ArrayLengthFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.String]));
    }

    [Fact]
    public void ArrayLength_WrongArgCount_Throws()
    {
        ArrayLengthFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Array, DataKind.Array]));
    }

    [Fact]
    public void ArrayLength_ValidateReturnsScalar()
    {
        ArrayLengthFunction function = new();
        Assert.Equal(DataKind.Scalar, function.ValidateArguments([DataKind.Array]));
    }

    // ───────────────── ARRAY_JOIN ─────────────────

    [Fact]
    public void ArrayJoin_StringElements_JoinsWithSeparator()
    {
        ArrayJoinFunction function = new();
        DataValue result = function.Execute([
            MakeStringArray("a", "b", "c"),
            DataValue.FromString(", ")]);
        Assert.Equal("a, b, c", result.AsString());
    }

    [Fact]
    public void ArrayJoin_ScalarElements_ConvertsToString()
    {
        ArrayJoinFunction function = new();
        DataValue result = function.Execute([
            MakeScalarArray(1f, 2f, 3f),
            DataValue.FromString("-")]);
        Assert.Equal("1-2-3", result.AsString());
    }

    [Fact]
    public void ArrayJoin_SkipsNullElements()
    {
        ArrayJoinFunction function = new();
        DataValue array = DataValue.FromArray(DataKind.String, [
            DataValue.FromString("a"),
            DataValue.Null(DataKind.String),
            DataValue.FromString("c")]);
        DataValue result = function.Execute([array, DataValue.FromString(", ")]);
        Assert.Equal("a, c", result.AsString());
    }

    [Fact]
    public void ArrayJoin_EmptyArray_ReturnsEmptyString()
    {
        ArrayJoinFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromArray(DataKind.String, []),
            DataValue.FromString(",")]);
        Assert.Equal("", result.AsString());
    }

    [Fact]
    public void ArrayJoin_NullArray_ReturnsNull()
    {
        ArrayJoinFunction function = new();
        DataValue result = function.Execute([
            DataValue.NullArray(DataKind.String),
            DataValue.FromString(",")]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void ArrayJoin_NullSeparator_ReturnsNull()
    {
        ArrayJoinFunction function = new();
        DataValue result = function.Execute([
            MakeStringArray("a", "b"),
            DataValue.Null(DataKind.String)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void ArrayJoin_SingleElement_NoSeparator()
    {
        ArrayJoinFunction function = new();
        DataValue result = function.Execute([
            MakeStringArray("only"),
            DataValue.FromString(", ")]);
        Assert.Equal("only", result.AsString());
    }

    [Fact]
    public void ArrayJoin_WrongFirstArgKind_Throws()
    {
        ArrayJoinFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.String, DataKind.String]));
    }

    [Fact]
    public void ArrayJoin_WrongSecondArgKind_Throws()
    {
        ArrayJoinFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Array, DataKind.Scalar]));
    }

    [Fact]
    public void ArrayJoin_WrongArgCount_Throws()
    {
        ArrayJoinFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Array]));
    }

    // ───────────────── ARRAY_CONTAINS ─────────────────

    [Fact]
    public void ArrayContains_ValuePresent_ReturnsTrue()
    {
        ArrayContainsFunction function = new();
        DataValue result = function.Execute([
            MakeStringArray("a", "b", "c"),
            DataValue.FromString("b")]);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ArrayContains_ValueAbsent_ReturnsFalse()
    {
        ArrayContainsFunction function = new();
        DataValue result = function.Execute([
            MakeStringArray("a", "b", "c"),
            DataValue.FromString("d")]);
        Assert.False(result.AsBoolean());
    }

    [Fact]
    public void ArrayContains_ScalarValues()
    {
        ArrayContainsFunction function = new();
        DataValue result = function.Execute([
            MakeScalarArray(10f, 20f, 30f),
            DataValue.FromScalar(20f)]);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ArrayContains_EmptyArray_ReturnsFalse()
    {
        ArrayContainsFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromArray(DataKind.String, []),
            DataValue.FromString("x")]);
        Assert.False(result.AsBoolean());
    }

    [Fact]
    public void ArrayContains_NullArray_ReturnsNull()
    {
        ArrayContainsFunction function = new();
        DataValue result = function.Execute([
            DataValue.NullArray(DataKind.String),
            DataValue.FromString("x")]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void ArrayContains_SearchForNull_FindsNullElement()
    {
        ArrayContainsFunction function = new();
        DataValue array = DataValue.FromArray(DataKind.String, [
            DataValue.FromString("a"),
            DataValue.Null(DataKind.String),
            DataValue.FromString("c")]);
        DataValue result = function.Execute([array, DataValue.Null(DataKind.String)]);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ArrayContains_WrongFirstArgKind_Throws()
    {
        ArrayContainsFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.String, DataKind.String]));
    }

    [Fact]
    public void ArrayContains_AcceptsAnySearchKind()
    {
        ArrayContainsFunction function = new();
        // Second argument can be any kind — checked at runtime
        DataKind result = function.ValidateArguments([DataKind.Array, DataKind.Scalar]);
        Assert.Equal(DataKind.Boolean, result);
    }

    // ───────────────── ARRAY_POSITION ─────────────────

    [Fact]
    public void ArrayPosition_ValuePresent_ReturnsOneBased()
    {
        ArrayPositionFunction function = new();
        DataValue result = function.Execute([
            MakeStringArray("a", "b", "c"),
            DataValue.FromString("b")]);
        Assert.Equal(2f, result.AsScalar());
    }

    [Fact]
    public void ArrayPosition_FirstElement_ReturnsOne()
    {
        ArrayPositionFunction function = new();
        DataValue result = function.Execute([
            MakeStringArray("a", "b", "c"),
            DataValue.FromString("a")]);
        Assert.Equal(1f, result.AsScalar());
    }

    [Fact]
    public void ArrayPosition_ValueAbsent_ReturnsNull()
    {
        ArrayPositionFunction function = new();
        DataValue result = function.Execute([
            MakeStringArray("a", "b", "c"),
            DataValue.FromString("d")]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void ArrayPosition_Duplicates_ReturnsFirst()
    {
        ArrayPositionFunction function = new();
        DataValue result = function.Execute([
            MakeStringArray("a", "b", "a"),
            DataValue.FromString("a")]);
        Assert.Equal(1f, result.AsScalar());
    }

    [Fact]
    public void ArrayPosition_NullArray_ReturnsNull()
    {
        ArrayPositionFunction function = new();
        DataValue result = function.Execute([
            DataValue.NullArray(DataKind.String),
            DataValue.FromString("x")]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void ArrayPosition_ScalarValues()
    {
        ArrayPositionFunction function = new();
        DataValue result = function.Execute([
            MakeScalarArray(10f, 20f, 30f),
            DataValue.FromScalar(30f)]);
        Assert.Equal(3f, result.AsScalar());
    }

    [Fact]
    public void ArrayPosition_WrongFirstArgKind_Throws()
    {
        ArrayPositionFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.String, DataKind.String]));
    }

    // ───────────────── ARRAY constructor ─────────────────

    [Fact]
    public void ArrayConstructor_ScalarValues_CreatesArray()
    {
        ArrayConstructorFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromScalar(1f),
            DataValue.FromScalar(2f),
            DataValue.FromScalar(3f)]);

        Assert.Equal(DataKind.Array, result.Kind);
        Assert.Equal(DataKind.Scalar, result.ArrayElementKind);
        DataValue[] elements = result.AsArray();
        Assert.Equal(3, elements.Length);
        Assert.Equal(1f, elements[0].AsScalar());
        Assert.Equal(2f, elements[1].AsScalar());
        Assert.Equal(3f, elements[2].AsScalar());
    }

    [Fact]
    public void ArrayConstructor_StringValues_CreatesArray()
    {
        ArrayConstructorFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("x"),
            DataValue.FromString("y")]);

        Assert.Equal(DataKind.String, result.ArrayElementKind);
        DataValue[] elements = result.AsArray();
        Assert.Equal(2, elements.Length);
        Assert.Equal("x", elements[0].AsString());
        Assert.Equal("y", elements[1].AsString());
    }

    [Fact]
    public void ArrayConstructor_SingleElement()
    {
        ArrayConstructorFunction function = new();
        DataValue result = function.Execute([DataValue.FromScalar(42f)]);

        DataValue[] elements = result.AsArray();
        Assert.Single(elements);
        Assert.Equal(42f, elements[0].AsScalar());
    }

    [Fact]
    public void ArrayConstructor_PreservesNullElements()
    {
        ArrayConstructorFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("a"),
            DataValue.Null(DataKind.String),
            DataValue.FromString("c")]);

        DataValue[] elements = result.AsArray();
        Assert.Equal(3, elements.Length);
        Assert.False(elements[0].IsNull);
        Assert.True(elements[1].IsNull);
        Assert.False(elements[2].IsNull);
    }

    [Fact]
    public void ArrayConstructor_MixedKinds_Throws()
    {
        ArrayConstructorFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Scalar, DataKind.String]));
    }

    [Fact]
    public void ArrayConstructor_NoArguments_Throws()
    {
        ArrayConstructorFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments(ReadOnlySpan<DataKind>.Empty));
    }

    [Fact]
    public void ArrayConstructor_ValidateReturnsArray()
    {
        ArrayConstructorFunction function = new();
        Assert.Equal(DataKind.Array, function.ValidateArguments([DataKind.String]));
    }

    // ───────────────── LEN with Array ─────────────────

    [Fact]
    public void Len_Array_ReturnsElementCount()
    {
        LenFunction function = new();
        DataValue result = function.Execute([MakeScalarArray(1f, 2f, 3f)]);
        Assert.Equal(3f, result.AsScalar());
    }

    [Fact]
    public void Len_NullArray_ReturnsNull()
    {
        LenFunction function = new();
        DataValue result = function.Execute([DataValue.NullArray(DataKind.Scalar)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Len_ArrayKind_Validates()
    {
        LenFunction function = new();
        Assert.Equal(DataKind.Scalar, function.ValidateArguments([DataKind.Array]));
    }

    // ───────────────── REGISTRY ─────────────────

    [Theory]
    [InlineData("array_length")]
    [InlineData("array_join")]
    [InlineData("array_contains")]
    [InlineData("array_position")]
    [InlineData("array")]
    public void Registry_ContainsArrayFunction(string functionName)
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        IScalarFunction? function = registry.TryGetScalar(functionName);

        Assert.NotNull(function);
        Assert.Equal(functionName, function.Name);
    }
}
