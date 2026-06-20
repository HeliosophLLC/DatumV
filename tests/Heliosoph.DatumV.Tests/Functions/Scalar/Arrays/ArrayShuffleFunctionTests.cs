using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Arrays;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Arrays;

/// <summary>
/// Direct-invocation tests for <see cref="ArrayShuffleFunction"/>: same
/// seed → same permutation, all input elements are preserved, multi-dim
/// arrays are rejected, and null / empty arrays round-trip the element
/// kind.
/// </summary>
public sealed class ArrayShuffleFunctionTests : ServiceTestBase
{
    private EvaluationFrame? _frame;
    private EvaluationFrame Frame => _frame ??= CreateEvaluationFrame();

    [Fact]
    public async Task ArrayShuffle_SameSeed_Deterministic()
    {
        ValueRef input = ValueRef.FromPrimitiveArray(new[] { 1, 2, 3, 4, 5, 6, 7, 8 }, DataKind.Int32);
        ValueRef seed = ValueRef.FromInt32(42);

        ValueRef a = await new ArrayShuffleFunction().ExecuteAsync(new[] { input, seed }, Frame, default);
        ValueRef b = await new ArrayShuffleFunction().ExecuteAsync(new[] { input, seed }, Frame, default);

        int[] arrA = (int[])a.Materialized!;
        int[] arrB = (int[])b.Materialized!;
        Assert.Equal(arrA, arrB);
    }

    [Fact]
    public async Task ArrayShuffle_DifferentSeeds_TypicallyDifferOrder()
    {
        ValueRef input = ValueRef.FromPrimitiveArray(new[] { 1, 2, 3, 4, 5, 6, 7, 8 }, DataKind.Int32);
        ValueRef a = await new ArrayShuffleFunction().ExecuteAsync(
            new[] { input, ValueRef.FromInt32(1) }, Frame, default);
        ValueRef b = await new ArrayShuffleFunction().ExecuteAsync(
            new[] { input, ValueRef.FromInt32(99) }, Frame, default);

        int[] arrA = (int[])a.Materialized!;
        int[] arrB = (int[])b.Materialized!;
        Assert.NotEqual(arrA, arrB);
    }

    [Fact]
    public async Task ArrayShuffle_PreservesMultiset_Int32()
    {
        int[] source = { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };
        ValueRef input = ValueRef.FromPrimitiveArray(source, DataKind.Int32);

        ValueRef result = await new ArrayShuffleFunction().ExecuteAsync(
            new[] { input, ValueRef.FromInt32(7) }, Frame, default);

        int[] shuffled = (int[])result.Materialized!;
        Assert.Equal(source.Length, shuffled.Length);

        int[] copy = (int[])shuffled.Clone();
        Array.Sort(copy);
        Assert.Equal(source, copy);
    }

    [Fact]
    public async Task ArrayShuffle_DoesNotMutateInput()
    {
        int[] source = { 1, 2, 3, 4, 5, 6, 7, 8 };
        ValueRef input = ValueRef.FromPrimitiveArray(source, DataKind.Int32);

        _ = await new ArrayShuffleFunction().ExecuteAsync(
            new[] { input, ValueRef.FromInt32(42) }, Frame, default);

        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8 }, source);
    }

    [Fact]
    public async Task ArrayShuffle_String_PreservesElements()
    {
        ValueRef[] strings =
        [
            ValueRef.FromString("alpha"),
            ValueRef.FromString("beta"),
            ValueRef.FromString("gamma"),
            ValueRef.FromString("delta"),
        ];
        ValueRef input = ValueRef.FromArray(DataKind.String, strings);

        ValueRef result = await new ArrayShuffleFunction().ExecuteAsync(
            new[] { input, ValueRef.FromInt32(3) }, Frame, default);

        ReadOnlySpan<ValueRef> elements = result.GetArrayElements();
        Assert.Equal(DataKind.String, result.ArrayElementKind);
        Assert.Equal(4, elements.Length);

        HashSet<string> got = new();
        for (int i = 0; i < elements.Length; i++)
            got.Add(elements[i].AsString());
        Assert.Equal(new HashSet<string> { "alpha", "beta", "gamma", "delta" }, got);
    }

    [Fact]
    public async Task ArrayShuffle_EmptyArray_ReturnsEmpty()
    {
        ValueRef input = ValueRef.FromPrimitiveArray(Array.Empty<int>(), DataKind.Int32);

        ValueRef result = await new ArrayShuffleFunction().ExecuteAsync(
            new[] { input }, Frame, default);

        Assert.True(result.IsArray);
        Assert.Equal(DataKind.Int32, result.ArrayElementKind);
        Assert.Equal(0, result.GetArrayLength());
    }

    [Fact]
    public async Task ArrayShuffle_NullArray_ReturnsNullArray()
    {
        ValueRef input = ValueRef.NullArray(DataKind.Float32);

        ValueRef result = await new ArrayShuffleFunction().ExecuteAsync(
            new[] { input }, Frame, default);

        Assert.True(result.IsNull);
        Assert.True(result.IsArray);
        Assert.Equal(DataKind.Float32, result.ArrayElementKind);
    }

    [Fact]
    public async Task ArrayShuffle_NullSeed_ReturnsNullArray()
    {
        ValueRef input = ValueRef.FromPrimitiveArray(new[] { 1, 2, 3 }, DataKind.Int32);

        ValueRef result = await new ArrayShuffleFunction().ExecuteAsync(
            new[] { input, ValueRef.Null(DataKind.Int32) }, Frame, default);

        Assert.True(result.IsNull);
        Assert.True(result.IsArray);
        Assert.Equal(DataKind.Int32, result.ArrayElementKind);
    }

    [Fact]
    public async Task ArrayShuffle_SingleElement_RoundTrips()
    {
        ValueRef input = ValueRef.FromPrimitiveArray(new[] { 42 }, DataKind.Int32);

        ValueRef result = await new ArrayShuffleFunction().ExecuteAsync(
            new[] { input, ValueRef.FromInt32(0) }, Frame, default);

        int[] shuffled = (int[])result.Materialized!;
        Assert.Equal(new[] { 42 }, shuffled);
    }
}
