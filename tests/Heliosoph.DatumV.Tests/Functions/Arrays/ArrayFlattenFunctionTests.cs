using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Arrays;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Arrays;

/// <summary>
/// Unit tests for <see cref="ArrayFlattenFunction"/> — the explicit
/// shape-dropping counterpart to a shaped cast. Bare array casts now preserve
/// shape, so flattening is only ever this function's job.
/// </summary>
public sealed class ArrayFlattenFunctionTests
{
    private static readonly EvaluationFrame Frame = default;

    [Fact]
    public async Task ArrayFlatten_MultiDim_DropsShapeKeepsElements()
    {
        float[] data = [1f, 2f, 3f, 4f, 5f, 6f];
        ValueRef multi = ValueRef.FromPrimitiveMultiDimArray(data, [2, 3], DataKind.Float32);
        Assert.True(multi.IsMultiDim, "fixture precondition: source must be multi-dim");

        ValueRef result = await new ArrayFlattenFunction().ExecuteAsync(
            new[] { multi }, Frame, default);

        Assert.True(result.IsArray);
        Assert.False(result.IsMultiDim,
            "array_flatten must drop the shape so single-index / array_slice consumers accept it.");
        Assert.Equal(6, result.GetArrayLength());
    }

    [Fact]
    public async Task ArrayFlatten_FlatInput_PassesThrough()
    {
        // A 1-D source carries no shape — flatten is a no-op. This is what makes
        // it safe to wrap any infer() output (`array_flatten(infer(...))`)
        // regardless of whether the ONNX output came back rank-1 or rank-2.
        float[] data = [10f, 20f, 30f];
        ValueRef flat = ValueRef.FromPrimitiveArray(data, DataKind.Float32);
        Assert.False(flat.IsMultiDim);

        ValueRef result = await new ArrayFlattenFunction().ExecuteAsync(
            new[] { flat }, Frame, default);

        Assert.True(result.IsArray);
        Assert.False(result.IsMultiDim);
        Assert.Equal(3, result.GetArrayLength());
    }

    [Fact]
    public async Task ArrayFlatten_Null_ReturnsTypedNullArray()
    {
        ValueRef result = await new ArrayFlattenFunction().ExecuteAsync(
            new[] { ValueRef.NullArray(DataKind.Float32) }, Frame, default);

        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Float32, result.Kind);
    }
}
