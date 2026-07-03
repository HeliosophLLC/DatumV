using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Vector;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Vector;

/// <summary>
/// Tests for <see cref="NearestCentroidFunction"/> — <c>nearest_centroid(centroids, vec)</c>.
/// Verifies 1-based nearest-by-Euclidean-distance assignment, first-index tie
/// breaking, null propagation, and dimension / shape error handling.
/// </summary>
public sealed class NearestCentroidFunctionTests : ServiceTestBase
{
    [Fact]
    public void Metadata_Exposes()
    {
        Assert.Equal("nearest_centroid", NearestCentroidFunction.Name);
        Assert.Equal(FunctionCategory.Vector, NearestCentroidFunction.Category);
    }

    private static ValueRef Centroids(float[] flat, int k, int d) =>
        ValueRef.FromPrimitiveMultiDimArray(flat, [k, d], DataKind.Float32);

    private static ValueRef Vec(params float[] values) =>
        ValueRef.FromPrimitiveArray(values, DataKind.Float32);

    private async Task<ValueRef> Invoke(ValueRef centroids, ValueRef vec)
    {
        NearestCentroidFunction fn = new();
        return await fn.ExecuteAsync(new[] { centroids, vec }, CreateEvaluationFrame(), default);
    }

    [Fact]
    public async Task AssignsNearestCentroid_OneBased()
    {
        ValueRef centroids = Centroids([0f, 0f, 10f, 10f], k: 2, d: 2);

        Assert.Equal(1, (await Invoke(centroids, Vec(1f, 1f))).ToInt32());
        Assert.Equal(2, (await Invoke(centroids, Vec(9f, 9f))).ToInt32());
    }

    [Fact]
    public async Task Tie_BreaksToLowestIndex()
    {
        ValueRef centroids = Centroids([0f, 0f, 10f, 0f], k: 2, d: 2);

        // (5, 0) is exactly equidistant from both centroids.
        Assert.Equal(1, (await Invoke(centroids, Vec(5f, 0f))).ToInt32());
    }

    [Fact]
    public async Task FlatCentroidArray_InfersKFromVectorLength()
    {
        // Plain flat Float32[] (no multi-dim shape) — k derived from vec length.
        ValueRef centroids = Vec(0f, 0f, 10f, 10f);

        Assert.Equal(2, (await Invoke(centroids, Vec(8f, 8f))).ToInt32());
    }

    [Fact]
    public async Task NullInputs_ReturnNull()
    {
        ValueRef centroids = Centroids([0f, 0f, 10f, 10f], k: 2, d: 2);

        Assert.True((await Invoke(ValueRef.NullArray(DataKind.Float32), Vec(1f, 1f))).IsNull);
        Assert.True((await Invoke(centroids, ValueRef.NullArray(DataKind.Float32))).IsNull);
    }

    [Fact]
    public async Task DimensionMismatch_Throws()
    {
        // 2×2 centroid matrix vs a 3-dim vector: 4 % 3 != 0.
        ValueRef centroids = Centroids([0f, 0f, 10f, 10f], k: 2, d: 2);

        await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await Invoke(centroids, Vec(1f, 1f, 1f)));
    }

    [Fact]
    public async Task EmptyCentroids_Throws()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await Invoke(Vec(), Vec(1f, 1f)));
    }
}
