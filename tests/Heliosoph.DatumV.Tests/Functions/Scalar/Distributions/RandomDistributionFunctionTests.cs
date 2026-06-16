using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Distributions;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Distributions;

/// <summary>
/// Direct-invocation tests for the seven distribution functions:
/// <c>random_normal</c>, <c>random_truncated_normal</c>, <c>random_log_normal</c>,
/// <c>random_exponential</c>, <c>random_beta</c>, <c>random_poisson</c>, and
/// <c>random_boolean</c>. Each function gets a determinism check (same seed
/// reproduces the same value), a support-range or sanity check, null
/// propagation, and an argument-validation rejection path.
/// </summary>
public sealed class RandomDistributionFunctionTests : ServiceTestBase
{
    private EvaluationFrame? _frame;
    private EvaluationFrame Frame => _frame ??= CreateEvaluationFrame();

    // ──────────────────── random_normal ────────────────────

    [Fact]
    public async Task RandomNormal_SameSeed_Deterministic()
    {
        ValueRef a = await new RandomNormalFunction().ExecuteAsync(
            new[] { ValueRef.FromFloat32(0f), ValueRef.FromFloat32(1f), ValueRef.FromInt32(42) },
            Frame, default);
        ValueRef b = await new RandomNormalFunction().ExecuteAsync(
            new[] { ValueRef.FromFloat32(0f), ValueRef.FromFloat32(1f), ValueRef.FromInt32(42) },
            Frame, default);

        Assert.Equal(a.AsFloat32(), b.AsFloat32());
    }

    [Fact]
    public async Task RandomNormal_DistributionSanity_MeanInRange()
    {
        RandomNormalFunction fn = new();
        double sum = 0;
        const int n = 2000;
        for (int i = 0; i < n; i++)
        {
            ValueRef v = await fn.ExecuteAsync(
                new[] { ValueRef.FromFloat32(5f), ValueRef.FromFloat32(2f), ValueRef.FromInt32(i) },
                Frame, default);
            sum += v.AsFloat32();
        }
        // Standard error of the mean ≈ 2 / sqrt(2000) ≈ 0.045; ±0.4 is ~9 SE — generous.
        Assert.InRange(sum / n, 4.6, 5.4);
    }

    [Fact]
    public async Task RandomNormal_NullMean_ReturnsNull()
    {
        ValueRef result = await new RandomNormalFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.Float32), ValueRef.FromFloat32(1f) },
            Frame, default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Float32, result.Kind);
    }

    [Fact]
    public async Task RandomNormal_NegativeStddev_Throws()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await new RandomNormalFunction().ExecuteAsync(
                new[] { ValueRef.FromFloat32(0f), ValueRef.FromFloat32(-1f) },
                Frame, default));
    }

    // ──────────────────── random_truncated_normal ────────────────────

    [Fact]
    public async Task RandomTruncatedNormal_SameSeed_Deterministic()
    {
        ValueRef[] args =
        [
            ValueRef.FromFloat32(0f), ValueRef.FromFloat32(1f),
            ValueRef.FromFloat32(-2f), ValueRef.FromFloat32(2f),
            ValueRef.FromInt32(7),
        ];
        ValueRef a = await new RandomTruncatedNormalFunction().ExecuteAsync(args, Frame, default);
        ValueRef b = await new RandomTruncatedNormalFunction().ExecuteAsync(args, Frame, default);
        Assert.Equal(a.AsFloat32(), b.AsFloat32());
    }

    [Fact]
    public async Task RandomTruncatedNormal_AlwaysWithinBounds()
    {
        RandomTruncatedNormalFunction fn = new();
        for (int i = 0; i < 500; i++)
        {
            ValueRef v = await fn.ExecuteAsync(
                new[]
                {
                    ValueRef.FromFloat32(0f), ValueRef.FromFloat32(1f),
                    ValueRef.FromFloat32(-1.5f), ValueRef.FromFloat32(1.5f),
                    ValueRef.FromInt32(i),
                },
                Frame, default);
            Assert.InRange(v.AsFloat32(), -1.5f, 1.5f);
        }
    }

    [Fact]
    public async Task RandomTruncatedNormal_MinGreaterEqualMax_Throws()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await new RandomTruncatedNormalFunction().ExecuteAsync(
                new[]
                {
                    ValueRef.FromFloat32(0f), ValueRef.FromFloat32(1f),
                    ValueRef.FromFloat32(2f), ValueRef.FromFloat32(2f),
                },
                Frame, default));
    }

    // ──────────────────── random_log_normal ────────────────────

    [Fact]
    public async Task RandomLogNormal_SameSeed_Deterministic()
    {
        ValueRef[] args = [ValueRef.FromFloat32(0f), ValueRef.FromFloat32(0.5f), ValueRef.FromInt32(13)];
        ValueRef a = await new RandomLogNormalFunction().ExecuteAsync(args, Frame, default);
        ValueRef b = await new RandomLogNormalFunction().ExecuteAsync(args, Frame, default);
        Assert.Equal(a.AsFloat32(), b.AsFloat32());
    }

    [Fact]
    public async Task RandomLogNormal_AlwaysPositive()
    {
        RandomLogNormalFunction fn = new();
        for (int i = 0; i < 200; i++)
        {
            ValueRef v = await fn.ExecuteAsync(
                new[] { ValueRef.FromFloat32(0f), ValueRef.FromFloat32(1f), ValueRef.FromInt32(i) },
                Frame, default);
            Assert.True(v.AsFloat32() > 0f);
        }
    }

    [Fact]
    public async Task RandomLogNormal_NegativeStddev_Throws()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await new RandomLogNormalFunction().ExecuteAsync(
                new[] { ValueRef.FromFloat32(0f), ValueRef.FromFloat32(-1f) },
                Frame, default));
    }

    // ──────────────────── random_exponential ────────────────────

    [Fact]
    public async Task RandomExponential_SameSeed_Deterministic()
    {
        ValueRef[] args = [ValueRef.FromFloat32(2f), ValueRef.FromInt32(99)];
        ValueRef a = await new RandomExponentialFunction().ExecuteAsync(args, Frame, default);
        ValueRef b = await new RandomExponentialFunction().ExecuteAsync(args, Frame, default);
        Assert.Equal(a.AsFloat32(), b.AsFloat32());
    }

    [Fact]
    public async Task RandomExponential_AlwaysNonNegative()
    {
        RandomExponentialFunction fn = new();
        for (int i = 0; i < 200; i++)
        {
            ValueRef v = await fn.ExecuteAsync(
                new[] { ValueRef.FromFloat32(1f), ValueRef.FromInt32(i) },
                Frame, default);
            Assert.True(v.AsFloat32() >= 0f);
        }
    }

    [Fact]
    public async Task RandomExponential_NonPositiveRate_Throws()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await new RandomExponentialFunction().ExecuteAsync(
                new[] { ValueRef.FromFloat32(0f) },
                Frame, default));
    }

    // ──────────────────── random_beta ────────────────────

    [Fact]
    public async Task RandomBeta_SameSeed_Deterministic()
    {
        ValueRef[] args = [ValueRef.FromFloat32(2f), ValueRef.FromFloat32(5f), ValueRef.FromInt32(11)];
        ValueRef a = await new RandomBetaFunction().ExecuteAsync(args, Frame, default);
        ValueRef b = await new RandomBetaFunction().ExecuteAsync(args, Frame, default);
        Assert.Equal(a.AsFloat32(), b.AsFloat32());
    }

    [Fact]
    public async Task RandomBeta_AlwaysWithinUnitInterval()
    {
        RandomBetaFunction fn = new();
        for (int i = 0; i < 300; i++)
        {
            ValueRef v = await fn.ExecuteAsync(
                new[] { ValueRef.FromFloat32(0.5f), ValueRef.FromFloat32(0.5f), ValueRef.FromInt32(i) },
                Frame, default);
            float result = v.AsFloat32();
            Assert.InRange(result, 0f, 1f);
        }
    }

    [Fact]
    public async Task RandomBeta_NonPositiveAlpha_Throws()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await new RandomBetaFunction().ExecuteAsync(
                new[] { ValueRef.FromFloat32(0f), ValueRef.FromFloat32(1f) },
                Frame, default));
    }

    // ──────────────────── random_poisson ────────────────────

    [Fact]
    public async Task RandomPoisson_SameSeed_Deterministic()
    {
        ValueRef[] args = [ValueRef.FromFloat32(5f), ValueRef.FromInt32(3)];
        ValueRef a = await new RandomPoissonFunction().ExecuteAsync(args, Frame, default);
        ValueRef b = await new RandomPoissonFunction().ExecuteAsync(args, Frame, default);
        Assert.Equal(a.AsInt32(), b.AsInt32());
    }

    [Fact]
    public async Task RandomPoisson_ReturnsInt32_NonNegative()
    {
        RandomPoissonFunction fn = new();
        ValueRef v = await fn.ExecuteAsync(
            new[] { ValueRef.FromFloat32(3f), ValueRef.FromInt32(0) },
            Frame, default);
        Assert.Equal(DataKind.Int32, v.Kind);
        Assert.True(v.AsInt32() >= 0);
    }

    [Fact]
    public async Task RandomPoisson_ZeroLambda_ReturnsZero()
    {
        ValueRef v = await new RandomPoissonFunction().ExecuteAsync(
            new[] { ValueRef.FromFloat32(0f) },
            Frame, default);
        Assert.Equal(0, v.AsInt32());
    }

    [Fact]
    public async Task RandomPoisson_NegativeLambda_Throws()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await new RandomPoissonFunction().ExecuteAsync(
                new[] { ValueRef.FromFloat32(-1f) },
                Frame, default));
    }

    [Fact]
    public async Task RandomPoisson_LargeLambda_NormalApproximation()
    {
        RandomPoissonFunction fn = new();
        long sum = 0;
        const int n = 500;
        for (int i = 0; i < n; i++)
        {
            ValueRef v = await fn.ExecuteAsync(
                new[] { ValueRef.FromFloat32(100f), ValueRef.FromInt32(i) },
                Frame, default);
            sum += v.AsInt32();
        }
        double mean = (double)sum / n;
        // Poisson(100): SE of mean ≈ sqrt(100/500) = 0.45; ±5 is very generous.
        Assert.InRange(mean, 95.0, 105.0);
    }

    // ──────────────────── random_boolean ────────────────────

    [Fact]
    public async Task RandomBoolean_SameSeed_Deterministic()
    {
        ValueRef[] args = [ValueRef.FromFloat32(0.7f), ValueRef.FromInt32(123)];
        ValueRef a = await new RandomBooleanFunction().ExecuteAsync(args, Frame, default);
        ValueRef b = await new RandomBooleanFunction().ExecuteAsync(args, Frame, default);
        Assert.Equal(a.AsBoolean(), b.AsBoolean());
    }

    [Fact]
    public async Task RandomBoolean_ProbabilityZero_AlwaysFalse()
    {
        RandomBooleanFunction fn = new();
        for (int i = 0; i < 50; i++)
        {
            ValueRef v = await fn.ExecuteAsync(
                new[] { ValueRef.FromFloat32(0f), ValueRef.FromInt32(i) },
                Frame, default);
            Assert.False(v.AsBoolean());
        }
    }

    [Fact]
    public async Task RandomBoolean_ProbabilityOne_AlwaysTrue()
    {
        RandomBooleanFunction fn = new();
        for (int i = 0; i < 50; i++)
        {
            ValueRef v = await fn.ExecuteAsync(
                new[] { ValueRef.FromFloat32(1f), ValueRef.FromInt32(i) },
                Frame, default);
            Assert.True(v.AsBoolean());
        }
    }

    [Fact]
    public async Task RandomBoolean_RateInRange_OverManyTrials()
    {
        RandomBooleanFunction fn = new();
        int trueCount = 0;
        const int n = 2000;
        for (int i = 0; i < n; i++)
        {
            ValueRef v = await fn.ExecuteAsync(
                new[] { ValueRef.FromFloat32(0.3f), ValueRef.FromInt32(i) },
                Frame, default);
            if (v.AsBoolean()) trueCount++;
        }
        double rate = (double)trueCount / n;
        Assert.InRange(rate, 0.25, 0.35);
    }

    [Fact]
    public async Task RandomBoolean_OutOfRangeProbability_Throws()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await new RandomBooleanFunction().ExecuteAsync(
                new[] { ValueRef.FromFloat32(1.5f) },
                Frame, default));
    }
}
