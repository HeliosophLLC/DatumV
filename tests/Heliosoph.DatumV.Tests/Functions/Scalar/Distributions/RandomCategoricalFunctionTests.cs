using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Distributions;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Distributions;

/// <summary>
/// Direct-invocation tests for <see cref="RandomCategoricalFunction"/>:
/// same seed reproduces the same index, the empirical histogram tracks
/// the supplied weights, degenerate weights (single nonzero entry) always
/// return that index, and invalid inputs (null, empty, negative, all-zero)
/// surface the right behaviour.
/// </summary>
public sealed class RandomCategoricalFunctionTests : ServiceTestBase
{
    private EvaluationFrame? _frame;
    private EvaluationFrame Frame => _frame ??= CreateEvaluationFrame();

    private static ValueRef Weights(params float[] values) =>
        ValueRef.FromPrimitiveArray(values, DataKind.Float32);

    [Fact]
    public async Task RandomCategorical_SameSeed_Deterministic()
    {
        ValueRef[] args = [Weights(1f, 2f, 3f, 4f), ValueRef.FromInt32(42)];
        ValueRef a = await new RandomCategoricalFunction().ExecuteAsync(args, Frame, default);
        ValueRef b = await new RandomCategoricalFunction().ExecuteAsync(args, Frame, default);
        Assert.Equal(a.AsInt32(), b.AsInt32());
    }

    [Fact]
    public async Task RandomCategorical_ReturnsInt32_InBounds()
    {
        RandomCategoricalFunction fn = new();
        for (int i = 0; i < 200; i++)
        {
            ValueRef v = await fn.ExecuteAsync(
                new[] { Weights(1f, 1f, 1f, 1f), ValueRef.FromInt32(i) },
                Frame, default);
            Assert.Equal(DataKind.Int32, v.Kind);
            Assert.InRange(v.AsInt32(), 0, 3);
        }
    }

    [Fact]
    public async Task RandomCategorical_SingleNonzeroWeight_AlwaysPicksThatIndex()
    {
        RandomCategoricalFunction fn = new();
        for (int i = 0; i < 50; i++)
        {
            ValueRef v = await fn.ExecuteAsync(
                new[] { Weights(0f, 0f, 1f, 0f), ValueRef.FromInt32(i) },
                Frame, default);
            Assert.Equal(2, v.AsInt32());
        }
    }

    [Fact]
    public async Task RandomCategorical_EmpiricalHistogram_TracksWeights()
    {
        // weights [3, 1, 1] → expected p ≈ [0.6, 0.2, 0.2]
        RandomCategoricalFunction fn = new();
        int[] counts = new int[3];
        const int n = 3000;
        for (int i = 0; i < n; i++)
        {
            ValueRef v = await fn.ExecuteAsync(
                new[] { Weights(3f, 1f, 1f), ValueRef.FromInt32(i) },
                Frame, default);
            counts[v.AsInt32()]++;
        }
        double p0 = (double)counts[0] / n;
        double p1 = (double)counts[1] / n;
        double p2 = (double)counts[2] / n;
        // SE ≈ sqrt(p*(1-p)/n) ≈ 0.009; ±0.05 is ~5 SE — generous.
        Assert.InRange(p0, 0.55, 0.65);
        Assert.InRange(p1, 0.15, 0.25);
        Assert.InRange(p2, 0.15, 0.25);
    }

    [Fact]
    public async Task RandomCategorical_NullWeights_ReturnsNull()
    {
        ValueRef result = await new RandomCategoricalFunction().ExecuteAsync(
            new[] { ValueRef.NullArray(DataKind.Float32) },
            Frame, default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Int32, result.Kind);
    }

    [Fact]
    public async Task RandomCategorical_NullSeed_ReturnsNull()
    {
        ValueRef result = await new RandomCategoricalFunction().ExecuteAsync(
            new[] { Weights(1f, 1f), ValueRef.Null(DataKind.Int32) },
            Frame, default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Int32, result.Kind);
    }

    [Fact]
    public async Task RandomCategorical_EmptyWeights_Throws()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await new RandomCategoricalFunction().ExecuteAsync(
                new[] { Weights() },
                Frame, default));
    }

    [Fact]
    public async Task RandomCategorical_NegativeWeight_Throws()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await new RandomCategoricalFunction().ExecuteAsync(
                new[] { Weights(1f, -0.5f, 1f) },
                Frame, default));
    }

    [Fact]
    public async Task RandomCategorical_AllZeroWeights_Throws()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await new RandomCategoricalFunction().ExecuteAsync(
                new[] { Weights(0f, 0f, 0f) },
                Frame, default));
    }
}
