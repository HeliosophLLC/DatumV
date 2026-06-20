using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Distributions;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Distributions;

/// <summary>
/// Direct-invocation tests for <see cref="RandomVectorFunction"/> and
/// <see cref="RandomNormalVectorFunction"/>: result length and element kind,
/// support range, determinism with seed, null propagation, and argument
/// validation rejection paths.
/// </summary>
public sealed class RandomVectorFunctionTests : ServiceTestBase
{
    private EvaluationFrame? _frame;
    private EvaluationFrame Frame => _frame ??= CreateEvaluationFrame();

    // ──────────────────── random_vector ────────────────────

    [Fact]
    public async Task RandomVector_LengthAndKind()
    {
        ValueRef v = await new RandomVectorFunction().ExecuteAsync(
            new[] { ValueRef.FromInt32(8) }, Frame, default);

        Assert.True(v.IsArray);
        Assert.Equal(DataKind.Float32, v.ArrayElementKind);
        float[] arr = (float[])v.Materialized!;
        Assert.Equal(8, arr.Length);
    }

    [Fact]
    public async Task RandomVector_AllValuesInUnitInterval()
    {
        ValueRef v = await new RandomVectorFunction().ExecuteAsync(
            new[] { ValueRef.FromInt32(500), ValueRef.FromInt32(7) }, Frame, default);

        float[] arr = (float[])v.Materialized!;
        for (int i = 0; i < arr.Length; i++)
        {
            Assert.InRange(arr[i], 0f, 1f);
            Assert.True(arr[i] < 1f, "random_vector must yield values strictly less than 1.");
        }
    }

    [Fact]
    public async Task RandomVector_SameSeed_Deterministic()
    {
        ValueRef[] args = [ValueRef.FromInt32(16), ValueRef.FromInt32(42)];
        ValueRef a = await new RandomVectorFunction().ExecuteAsync(args, Frame, default);
        ValueRef b = await new RandomVectorFunction().ExecuteAsync(args, Frame, default);

        Assert.Equal((float[])a.Materialized!, (float[])b.Materialized!);
    }

    [Fact]
    public async Task RandomVector_ZeroLength_ReturnsEmpty()
    {
        ValueRef v = await new RandomVectorFunction().ExecuteAsync(
            new[] { ValueRef.FromInt32(0) }, Frame, default);

        Assert.True(v.IsArray);
        Assert.Empty((float[])v.Materialized!);
    }

    [Fact]
    public async Task RandomVector_NegativeLength_Throws()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await new RandomVectorFunction().ExecuteAsync(
                new[] { ValueRef.FromInt32(-1) }, Frame, default));
    }

    [Fact]
    public async Task RandomVector_NullLength_ReturnsNullArray()
    {
        ValueRef v = await new RandomVectorFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.Int32) }, Frame, default);

        Assert.True(v.IsNull);
        Assert.True(v.IsArray);
        Assert.Equal(DataKind.Float32, v.ArrayElementKind);
    }

    [Fact]
    public async Task RandomVector_NullSeed_ReturnsNullArray()
    {
        ValueRef v = await new RandomVectorFunction().ExecuteAsync(
            new[] { ValueRef.FromInt32(8), ValueRef.Null(DataKind.Int32) }, Frame, default);

        Assert.True(v.IsNull);
        Assert.True(v.IsArray);
        Assert.Equal(DataKind.Float32, v.ArrayElementKind);
    }

    // ──────────────────── random_normal_vector ────────────────────

    [Fact]
    public async Task RandomNormalVector_LengthAndKind()
    {
        ValueRef v = await new RandomNormalVectorFunction().ExecuteAsync(
            new[] { ValueRef.FromInt32(10), ValueRef.FromFloat32(0f), ValueRef.FromFloat32(1f) },
            Frame, default);

        Assert.True(v.IsArray);
        Assert.Equal(DataKind.Float32, v.ArrayElementKind);
        float[] arr = (float[])v.Materialized!;
        Assert.Equal(10, arr.Length);
    }

    [Fact]
    public async Task RandomNormalVector_SameSeed_Deterministic()
    {
        ValueRef[] args =
        [
            ValueRef.FromInt32(32),
            ValueRef.FromFloat32(0f),
            ValueRef.FromFloat32(1f),
            ValueRef.FromInt32(5),
        ];
        ValueRef a = await new RandomNormalVectorFunction().ExecuteAsync(args, Frame, default);
        ValueRef b = await new RandomNormalVectorFunction().ExecuteAsync(args, Frame, default);

        Assert.Equal((float[])a.Materialized!, (float[])b.Materialized!);
    }

    [Fact]
    public async Task RandomNormalVector_DistributionSanity_MeanInRange()
    {
        ValueRef v = await new RandomNormalVectorFunction().ExecuteAsync(
            new[] { ValueRef.FromInt32(4000), ValueRef.FromFloat32(5f), ValueRef.FromFloat32(2f), ValueRef.FromInt32(1) },
            Frame, default);

        float[] arr = (float[])v.Materialized!;
        double sum = 0;
        for (int i = 0; i < arr.Length; i++) sum += arr[i];
        double mean = sum / arr.Length;
        // SE ≈ 2 / sqrt(4000) ≈ 0.032; ±0.3 is ~9 SE — generous.
        Assert.InRange(mean, 4.7, 5.3);
    }

    [Fact]
    public async Task RandomNormalVector_ZeroLength_ReturnsEmpty()
    {
        ValueRef v = await new RandomNormalVectorFunction().ExecuteAsync(
            new[] { ValueRef.FromInt32(0), ValueRef.FromFloat32(0f), ValueRef.FromFloat32(1f) },
            Frame, default);

        Assert.True(v.IsArray);
        Assert.Empty((float[])v.Materialized!);
    }

    [Fact]
    public async Task RandomNormalVector_NegativeStddev_Throws()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await new RandomNormalVectorFunction().ExecuteAsync(
                new[] { ValueRef.FromInt32(4), ValueRef.FromFloat32(0f), ValueRef.FromFloat32(-1f) },
                Frame, default));
    }

    [Fact]
    public async Task RandomNormalVector_NullMean_ReturnsNullArray()
    {
        ValueRef v = await new RandomNormalVectorFunction().ExecuteAsync(
            new[] { ValueRef.FromInt32(4), ValueRef.Null(DataKind.Float32), ValueRef.FromFloat32(1f) },
            Frame, default);

        Assert.True(v.IsNull);
        Assert.True(v.IsArray);
        Assert.Equal(DataKind.Float32, v.ArrayElementKind);
    }
}
