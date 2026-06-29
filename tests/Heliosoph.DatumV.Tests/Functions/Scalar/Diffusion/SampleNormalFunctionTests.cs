using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Diffusion;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Diffusion;

/// <summary>
/// Direct-invocation tests for <see cref="SampleNormalFunction"/>: result
/// length and element kind, seeded reproducibility, the NULL-seed fallback to
/// the shared RNG (distinct from the distribution functions, which null out on
/// a null seed), and the argument validation paths.
/// </summary>
public sealed class SampleNormalFunctionTests : ServiceTestBase
{
    private EvaluationFrame? _frame;
    private EvaluationFrame Frame => _frame ??= CreateEvaluationFrame();

    [Fact]
    public async Task SampleNormal_LengthAndKind()
    {
        ValueRef v = await new SampleNormalFunction().ExecuteAsync(
            new[] { ValueRef.FromInt32(8) }, Frame, default);

        Assert.True(v.IsArray);
        Assert.Equal(DataKind.Float32, v.ArrayElementKind);
        Assert.Equal(8, ((float[])v.Materialized!).Length);
    }

    [Fact]
    public async Task SampleNormal_SameSeed_Deterministic()
    {
        ValueRef[] args = [ValueRef.FromInt32(64), ValueRef.FromInt64(42)];
        ValueRef a = await new SampleNormalFunction().ExecuteAsync(args, Frame, default);
        ValueRef b = await new SampleNormalFunction().ExecuteAsync(args, Frame, default);

        Assert.Equal((float[])a.Materialized!, (float[])b.Materialized!);
    }

    [Fact]
    public async Task SampleNormal_DifferentSeed_Differs()
    {
        ValueRef a = await new SampleNormalFunction().ExecuteAsync(
            new[] { ValueRef.FromInt32(64), ValueRef.FromInt64(1) }, Frame, default);
        ValueRef b = await new SampleNormalFunction().ExecuteAsync(
            new[] { ValueRef.FromInt32(64), ValueRef.FromInt64(2) }, Frame, default);

        Assert.NotEqual((float[])a.Materialized!, (float[])b.Materialized!);
    }

    [Fact]
    public async Task SampleNormal_NullSeed_FallsBackToSharedRng()
    {
        // Unlike the distribution functions, a null seed does NOT null out the
        // result — it falls through to Random.Shared so the model body can pass
        // its NULL-defaulted seed parameter unconditionally.
        ValueRef v = await new SampleNormalFunction().ExecuteAsync(
            new[] { ValueRef.FromInt32(8), ValueRef.Null(DataKind.Int64) }, Frame, default);

        Assert.False(v.IsNull);
        Assert.True(v.IsArray);
        Assert.Equal(8, ((float[])v.Materialized!).Length);
    }

    [Fact]
    public async Task SampleNormal_NotPure()
    {
        Assert.False(new SampleNormalFunction().IsPure);
    }

    [Fact]
    public async Task SampleNormal_ZeroCount_ReturnsEmpty()
    {
        ValueRef v = await new SampleNormalFunction().ExecuteAsync(
            new[] { ValueRef.FromInt32(0) }, Frame, default);

        Assert.True(v.IsArray);
        Assert.Empty((float[])v.Materialized!);
    }

    [Fact]
    public async Task SampleNormal_NegativeCount_Throws()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await new SampleNormalFunction().ExecuteAsync(
                new[] { ValueRef.FromInt32(-1) }, Frame, default));
    }

    [Fact]
    public async Task SampleNormal_NullCount_ReturnsNullArray()
    {
        ValueRef v = await new SampleNormalFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.Int32) }, Frame, default);

        Assert.True(v.IsNull);
        Assert.True(v.IsArray);
        Assert.Equal(DataKind.Float32, v.ArrayElementKind);
    }
}
