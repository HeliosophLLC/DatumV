using DatumIngest.Functions;
using DatumIngest.Functions.Scalar;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions;

/// <summary>
/// Tests for the array scalar functions: <see cref="ArrayLengthFunction"/>,
/// <see cref="ArrayJoinFunction"/>, <see cref="ArrayContainsFunction"/>,
/// <see cref="ArrayPositionFunction"/>, <see cref="ArrayConstructorFunction"/>,
/// <see cref="ArraySortFunction"/>, <see cref="ArrayReverseFunction"/>,
/// <see cref="ArrayDistinctFunction"/>, <see cref="ArraySliceFunction"/>,
/// and <see cref="ArrayConcatFunction"/>.
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

    // ───────────────── ARRAY_SORT ─────────────────

    [Fact]
    public void ArraySort_ScalarValues_SortsAscending()
    {
        ArraySortFunction function = new();
        DataValue result = function.Execute([MakeScalarArray(3f, 1f, 2f)]);

        DataValue[] elements = result.AsArray();
        Assert.Equal(1f, elements[0].AsScalar());
        Assert.Equal(2f, elements[1].AsScalar());
        Assert.Equal(3f, elements[2].AsScalar());
    }

    [Fact]
    public void ArraySort_StringValues_SortsLexicographically()
    {
        ArraySortFunction function = new();
        DataValue result = function.Execute([MakeStringArray("cherry", "apple", "banana")]);

        DataValue[] elements = result.AsArray();
        Assert.Equal("apple", elements[0].AsString());
        Assert.Equal("banana", elements[1].AsString());
        Assert.Equal("cherry", elements[2].AsString());
    }

    [Fact]
    public void ArraySort_NullsLast()
    {
        ArraySortFunction function = new();
        DataValue array = DataValue.FromArray(DataKind.Scalar, [
            DataValue.FromScalar(3f),
            DataValue.Null(DataKind.Scalar),
            DataValue.FromScalar(1f)]);
        DataValue result = function.Execute([array]);

        DataValue[] elements = result.AsArray();
        Assert.Equal(1f, elements[0].AsScalar());
        Assert.Equal(3f, elements[1].AsScalar());
        Assert.True(elements[2].IsNull);
    }

    [Fact]
    public void ArraySort_NullInput_ReturnsNull()
    {
        ArraySortFunction function = new();
        DataValue result = function.Execute([DataValue.NullArray(DataKind.Scalar)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void ArraySort_DoesNotMutateOriginal()
    {
        ArraySortFunction function = new();
        DataValue original = MakeScalarArray(3f, 1f, 2f);
        function.Execute([original]);

        DataValue[] originalElements = original.AsArray();
        Assert.Equal(3f, originalElements[0].AsScalar());
    }

    [Fact]
    public void ArraySort_PreservesElementKind()
    {
        ArraySortFunction function = new();
        DataValue result = function.Execute([MakeStringArray("b", "a")]);
        Assert.Equal(DataKind.String, result.ArrayElementKind);
    }

    [Fact]
    public void ArraySort_WrongKind_Throws()
    {
        ArraySortFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.String]));
    }

    // ───────────────── ARRAY_REVERSE ─────────────────

    [Fact]
    public void ArrayReverse_ReversesOrder()
    {
        ArrayReverseFunction function = new();
        DataValue result = function.Execute([MakeScalarArray(1f, 2f, 3f)]);

        DataValue[] elements = result.AsArray();
        Assert.Equal(3f, elements[0].AsScalar());
        Assert.Equal(2f, elements[1].AsScalar());
        Assert.Equal(1f, elements[2].AsScalar());
    }

    [Fact]
    public void ArrayReverse_SingleElement_ReturnsSame()
    {
        ArrayReverseFunction function = new();
        DataValue result = function.Execute([MakeStringArray("only")]);

        DataValue[] elements = result.AsArray();
        Assert.Single(elements);
        Assert.Equal("only", elements[0].AsString());
    }

    [Fact]
    public void ArrayReverse_NullInput_ReturnsNull()
    {
        ArrayReverseFunction function = new();
        DataValue result = function.Execute([DataValue.NullArray(DataKind.Scalar)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void ArrayReverse_DoesNotMutateOriginal()
    {
        ArrayReverseFunction function = new();
        DataValue original = MakeScalarArray(1f, 2f, 3f);
        function.Execute([original]);

        Assert.Equal(1f, original.AsArray()[0].AsScalar());
    }

    [Fact]
    public void ArrayReverse_WrongKind_Throws()
    {
        ArrayReverseFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Scalar]));
    }

    // ───────────────── ARRAY_DISTINCT ─────────────────

    [Fact]
    public void ArrayDistinct_RemovesDuplicates()
    {
        ArrayDistinctFunction function = new();
        DataValue result = function.Execute([MakeStringArray("a", "b", "a", "c", "b")]);

        DataValue[] elements = result.AsArray();
        Assert.Equal(3, elements.Length);
        Assert.Equal("a", elements[0].AsString());
        Assert.Equal("b", elements[1].AsString());
        Assert.Equal("c", elements[2].AsString());
    }

    [Fact]
    public void ArrayDistinct_NoDuplicates_ReturnsUnchanged()
    {
        ArrayDistinctFunction function = new();
        DataValue result = function.Execute([MakeScalarArray(1f, 2f, 3f)]);

        Assert.Equal(3, result.AsArray().Length);
    }

    [Fact]
    public void ArrayDistinct_AllSame_ReturnsSingle()
    {
        ArrayDistinctFunction function = new();
        DataValue result = function.Execute([MakeStringArray("x", "x", "x")]);

        DataValue[] elements = result.AsArray();
        Assert.Single(elements);
        Assert.Equal("x", elements[0].AsString());
    }

    [Fact]
    public void ArrayDistinct_PreservesFirstOccurrenceOrder()
    {
        ArrayDistinctFunction function = new();
        DataValue result = function.Execute([MakeStringArray("c", "a", "b", "a", "c")]);

        DataValue[] elements = result.AsArray();
        Assert.Equal("c", elements[0].AsString());
        Assert.Equal("a", elements[1].AsString());
        Assert.Equal("b", elements[2].AsString());
    }

    [Fact]
    public void ArrayDistinct_NullInput_ReturnsNull()
    {
        ArrayDistinctFunction function = new();
        DataValue result = function.Execute([DataValue.NullArray(DataKind.String)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void ArrayDistinct_WrongKind_Throws()
    {
        ArrayDistinctFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Scalar]));
    }

    // ───────────────── ARRAY_SLICE ─────────────────

    [Fact]
    public void ArraySlice_MiddleElements()
    {
        ArraySliceFunction function = new();
        DataValue result = function.Execute([
            MakeScalarArray(10f, 20f, 30f, 40f, 50f),
            DataValue.FromScalar(2f),
            DataValue.FromScalar(3f)]);

        DataValue[] elements = result.AsArray();
        Assert.Equal(3, elements.Length);
        Assert.Equal(20f, elements[0].AsScalar());
        Assert.Equal(30f, elements[1].AsScalar());
        Assert.Equal(40f, elements[2].AsScalar());
    }

    [Fact]
    public void ArraySlice_FromStart()
    {
        ArraySliceFunction function = new();
        DataValue result = function.Execute([
            MakeStringArray("a", "b", "c", "d"),
            DataValue.FromScalar(1f),
            DataValue.FromScalar(2f)]);

        DataValue[] elements = result.AsArray();
        Assert.Equal(2, elements.Length);
        Assert.Equal("a", elements[0].AsString());
        Assert.Equal("b", elements[1].AsString());
    }

    [Fact]
    public void ArraySlice_LengthExceedsBounds_Clamped()
    {
        ArraySliceFunction function = new();
        DataValue result = function.Execute([
            MakeScalarArray(1f, 2f, 3f),
            DataValue.FromScalar(2f),
            DataValue.FromScalar(100f)]);

        DataValue[] elements = result.AsArray();
        Assert.Equal(2, elements.Length);
        Assert.Equal(2f, elements[0].AsScalar());
        Assert.Equal(3f, elements[1].AsScalar());
    }

    [Fact]
    public void ArraySlice_StartBeyondBounds_ReturnsEmpty()
    {
        ArraySliceFunction function = new();
        DataValue result = function.Execute([
            MakeScalarArray(1f, 2f),
            DataValue.FromScalar(10f),
            DataValue.FromScalar(5f)]);

        Assert.Empty(result.AsArray());
    }

    [Fact]
    public void ArraySlice_NullArray_ReturnsNull()
    {
        ArraySliceFunction function = new();
        DataValue result = function.Execute([
            DataValue.NullArray(DataKind.Scalar),
            DataValue.FromScalar(1f),
            DataValue.FromScalar(2f)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void ArraySlice_PreservesElementKind()
    {
        ArraySliceFunction function = new();
        DataValue result = function.Execute([
            MakeStringArray("a", "b", "c"),
            DataValue.FromScalar(1f),
            DataValue.FromScalar(2f)]);
        Assert.Equal(DataKind.String, result.ArrayElementKind);
    }

    [Fact]
    public void ArraySlice_WrongArgCount_Throws()
    {
        ArraySliceFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Array, DataKind.Scalar]));
    }

    [Fact]
    public void ArraySlice_WrongFirstArgKind_Throws()
    {
        ArraySliceFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.String, DataKind.Scalar, DataKind.Scalar]));
    }

    // ───────────────── ARRAY_CONCAT ─────────────────

    [Fact]
    public void ArrayConcat_CombinesTwoArrays()
    {
        ArrayConcatFunction function = new();
        DataValue result = function.Execute([
            MakeScalarArray(1f, 2f),
            MakeScalarArray(3f, 4f)]);

        DataValue[] elements = result.AsArray();
        Assert.Equal(4, elements.Length);
        Assert.Equal(1f, elements[0].AsScalar());
        Assert.Equal(2f, elements[1].AsScalar());
        Assert.Equal(3f, elements[2].AsScalar());
        Assert.Equal(4f, elements[3].AsScalar());
    }

    [Fact]
    public void ArrayConcat_WithEmptyArray()
    {
        ArrayConcatFunction function = new();
        DataValue result = function.Execute([
            MakeStringArray("a", "b"),
            DataValue.FromArray(DataKind.String, [])]);

        DataValue[] elements = result.AsArray();
        Assert.Equal(2, elements.Length);
        Assert.Equal("a", elements[0].AsString());
        Assert.Equal("b", elements[1].AsString());
    }

    [Fact]
    public void ArrayConcat_NullLeft_ReturnsNull()
    {
        ArrayConcatFunction function = new();
        DataValue result = function.Execute([
            DataValue.NullArray(DataKind.Scalar),
            MakeScalarArray(1f)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void ArrayConcat_NullRight_ReturnsNull()
    {
        ArrayConcatFunction function = new();
        DataValue result = function.Execute([
            MakeScalarArray(1f),
            DataValue.NullArray(DataKind.Scalar)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void ArrayConcat_PreservesElementKind()
    {
        ArrayConcatFunction function = new();
        DataValue result = function.Execute([
            MakeStringArray("a"),
            MakeStringArray("b")]);
        Assert.Equal(DataKind.String, result.ArrayElementKind);
    }

    [Fact]
    public void ArrayConcat_WrongArgCount_Throws()
    {
        ArrayConcatFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Array]));
    }

    [Fact]
    public void ArrayConcat_WrongFirstArgKind_Throws()
    {
        ArrayConcatFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.String, DataKind.Array]));
    }

    [Fact]
    public void ArrayConcat_WrongSecondArgKind_Throws()
    {
        ArrayConcatFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Array, DataKind.String]));
    }

    // ───────────────── REGISTRY ─────────────────

    [Theory]
    [InlineData("array_length")]
    [InlineData("array_join")]
    [InlineData("array_contains")]
    [InlineData("array_position")]
    [InlineData("array")]
    [InlineData("array_sort")]
    [InlineData("array_reverse")]
    [InlineData("array_distinct")]
    [InlineData("array_slice")]
    [InlineData("array_concat")]
    public void Registry_ContainsArrayFunction(string functionName)
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        IScalarFunction? function = registry.TryGetScalar(functionName);

        Assert.NotNull(function);
        Assert.Equal(functionName, function.Name);
    }
}
