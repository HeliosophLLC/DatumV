using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Drawing;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Scalar.Drawing;

/// <summary>
/// Phase E animation curves: <see cref="LerpFunction"/>,
/// <see cref="OscillateFunction"/>, <see cref="FadeInFunction"/>,
/// <see cref="FadeOutFunction"/>, <see cref="BounceFunction"/>,
/// <see cref="WobbleFunction"/>, <see cref="RandomWalkFunction"/>.
/// </summary>
public sealed class AnimationCurveFunctionTests : ServiceTestBase
{
    private async Task<float> Eval(IScalarFunction fn, params ValueRef[] args)
    {
        ValueRef result = await fn.ExecuteAsync(args, CreateEvaluationFrame(), default);
        Assert.Equal(DataKind.Float32, result.Kind);
        Assert.False(result.IsNull);
        return result.ToFloat();
    }

    private async Task<ValueRef> EvalRaw(IScalarFunction fn, params ValueRef[] args) =>
        await fn.ExecuteAsync(args, CreateEvaluationFrame(), default);

    private static ValueRef F(float v) => ValueRef.FromFloat32(v);
    private static ValueRef I(int v) => ValueRef.FromInt32(v);

    // ---------- lerp ----------

    [Fact]
    public async Task Lerp_Endpoints()
    {
        LerpFunction fn = new();
        Assert.Equal(10f, await Eval(fn, F(0f), F(10f), F(20f)));
        Assert.Equal(20f, await Eval(fn, F(1f), F(10f), F(20f)));
        Assert.Equal(15f, await Eval(fn, F(0.5f), F(10f), F(20f)));
    }

    [Fact]
    public async Task Lerp_Extrapolates_BeyondZeroOne()
    {
        LerpFunction fn = new();
        // lerp(-0.5, 10, 20) = 10 + 10 * -0.5 = 5; lerp(1.5, ...) = 25.
        Assert.Equal(5f, await Eval(fn, F(-0.5f), F(10f), F(20f)), precision: 4);
        Assert.Equal(25f, await Eval(fn, F(1.5f), F(10f), F(20f)), precision: 4);
    }

    [Fact]
    public async Task Lerp_NullPropagates()
    {
        LerpFunction fn = new();
        ValueRef r = await EvalRaw(fn, ValueRef.Null(DataKind.Float32), F(0f), F(1f));
        Assert.True(r.IsNull);
        Assert.Equal(DataKind.Float32, r.Kind);
    }

    // ---------- oscillate ----------

    [Fact]
    public async Task Oscillate_StartsAtMidpoint_PeaksAtQuarter()
    {
        OscillateFunction fn = new();
        // t=0 → mid; t=0.25 → high; t=0.5 → mid; t=0.75 → low.
        Assert.Equal(15f, await Eval(fn, F(0f), F(10f), F(20f)), precision: 3);
        Assert.Equal(20f, await Eval(fn, F(0.25f), F(10f), F(20f)), precision: 3);
        Assert.Equal(15f, await Eval(fn, F(0.5f), F(10f), F(20f)), precision: 3);
        Assert.Equal(10f, await Eval(fn, F(0.75f), F(10f), F(20f)), precision: 3);
    }

    [Fact]
    public async Task Oscillate_Frequency_MultipliesCycleCount()
    {
        OscillateFunction fn = new();
        // freq=2 ⇒ peaks at t=0.125 (and 0.625), troughs at t=0.375, 0.875.
        Assert.Equal(20f, await Eval(fn, F(0.125f), F(10f), F(20f), F(2f)), precision: 3);
        Assert.Equal(10f, await Eval(fn, F(0.375f), F(10f), F(20f), F(2f)), precision: 3);
    }

    [Fact]
    public async Task Oscillate_NegativeFrequency_Throws()
    {
        OscillateFunction fn = new();
        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await EvalRaw(fn, F(0f), F(0f), F(1f), F(-1f)));
    }

    // ---------- fade_in / fade_out ----------

    [Fact]
    public async Task FadeIn_ReachesOneAtDuration_StaysAtOneAfter()
    {
        FadeInFunction fn = new();
        Assert.Equal(0f, await Eval(fn, F(0f)));
        Assert.Equal(0.5f, await Eval(fn, F(0.125f)), precision: 4);   // default duration = 0.25
        Assert.Equal(1f, await Eval(fn, F(0.25f)), precision: 4);
        Assert.Equal(1f, await Eval(fn, F(0.5f)));                      // clamped
        Assert.Equal(1f, await Eval(fn, F(1f)));
    }

    [Fact]
    public async Task FadeIn_CustomDuration_OverridesDefault()
    {
        FadeInFunction fn = new();
        Assert.Equal(0.5f, await Eval(fn, F(0.25f), F(0.5f)), precision: 4);
        Assert.Equal(1f, await Eval(fn, F(0.5f), F(0.5f)), precision: 4);
    }

    [Fact]
    public async Task FadeOut_StartsAtOne_ReachesZeroAtEnd()
    {
        FadeOutFunction fn = new();
        // default duration = 0.25; fade begins at t = 0.75.
        Assert.Equal(1f, await Eval(fn, F(0f)));
        Assert.Equal(1f, await Eval(fn, F(0.5f)));      // still in steady period
        Assert.Equal(1f, await Eval(fn, F(0.75f)), precision: 4);
        Assert.Equal(0.5f, await Eval(fn, F(0.875f)), precision: 4);
        Assert.Equal(0f, await Eval(fn, F(1f)), precision: 4);
    }

    [Fact]
    public async Task FadeIn_ZeroDuration_Throws()
    {
        FadeInFunction fn = new();
        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await EvalRaw(fn, F(0.5f), F(0f)));
    }

    // ---------- bounce ----------

    [Fact]
    public async Task Bounce_StartsAtLow_EndsAtHigh()
    {
        BounceFunction fn = new();
        Assert.Equal(0f, await Eval(fn, F(0f), F(0f), F(10f)), precision: 3);
        Assert.Equal(10f, await Eval(fn, F(1f), F(0f), F(10f)), precision: 3);
    }

    [Fact]
    public async Task Bounce_OutputAlwaysWithinBounds()
    {
        BounceFunction fn = new();
        for (float t = 0f; t <= 1f; t += 0.05f)
        {
            float v = await Eval(fn, F(t), F(0f), F(10f));
            Assert.InRange(v, 0f, 10f);
        }
    }

    [Fact]
    public async Task Bounce_CustomBounceCount_EndsAtHigh()
    {
        BounceFunction fn = new();
        // Different bounce counts still reach `high` at t=1.
        Assert.Equal(10f, await Eval(fn, F(1f), F(0f), F(10f), F(5f)), precision: 3);
        Assert.Equal(10f, await Eval(fn, F(1f), F(0f), F(10f), F(7f)), precision: 3);
    }

    [Fact]
    public async Task Bounce_ZeroBounces_Throws()
    {
        BounceFunction fn = new();
        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await EvalRaw(fn, F(0.5f), F(0f), F(1f), F(0f)));
    }

    // ---------- wobble ----------

    [Fact]
    public async Task Wobble_StaysWithinBounds()
    {
        WobbleFunction fn = new();
        for (float t = 0f; t <= 1f; t += 0.02f)
        {
            float v = await Eval(fn, F(t), F(0f), F(10f), F(42f));
            Assert.InRange(v, -0.01f, 10.01f); // tiny epsilon for FP
        }
    }

    [Fact]
    public async Task Wobble_DeterministicForSeed()
    {
        WobbleFunction fn = new();
        float a = await Eval(fn, F(0.3f), F(0f), F(10f), F(7f));
        float b = await Eval(fn, F(0.3f), F(0f), F(10f), F(7f));
        Assert.Equal(a, b);
    }

    [Fact]
    public async Task Wobble_DifferentSeed_DifferentOutput()
    {
        WobbleFunction fn = new();
        // Same t, different seed → different wobble (almost certainly).
        float a = await Eval(fn, F(0.3f), F(0f), F(10f), F(1f));
        float b = await Eval(fn, F(0.3f), F(0f), F(10f), F(2f));
        Assert.NotEqual(a, b);
    }

    // ---------- random_walk ----------

    [Fact]
    public async Task RandomWalk_StaysWithinBounds()
    {
        RandomWalkFunction fn = new();
        for (float t = 0f; t <= 1f; t += 0.01f)
        {
            float v = await Eval(fn, F(t), F(0f), F(10f), I(20), I(42));
            Assert.InRange(v, 0f, 10f);
        }
    }

    [Fact]
    public async Task RandomWalk_Continuous_AdjacentSamplesAreClose()
    {
        // Within a step the function lerps linearly, so two t values close
        // together should map to values close together. Tests continuity.
        RandomWalkFunction fn = new();
        float a = await Eval(fn, F(0.10f), F(0f), F(100f), I(10), I(0));
        float b = await Eval(fn, F(0.105f), F(0f), F(100f), I(10), I(0));
        Assert.True(System.Math.Abs(a - b) < 5f,
            $"adjacent t values should give similar random_walk values; got {a} vs {b}.");
    }

    [Fact]
    public async Task RandomWalk_DifferentSeed_DifferentWalk()
    {
        RandomWalkFunction fn = new();
        float a = await Eval(fn, F(0.3f), F(0f), F(10f), I(10), I(1));
        float b = await Eval(fn, F(0.3f), F(0f), F(10f), I(10), I(2));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task RandomWalk_ZeroSteps_Throws()
    {
        RandomWalkFunction fn = new();
        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await EvalRaw(fn, F(0.5f), F(0f), F(1f), I(0)));
    }

    // ---------- context restriction ----------

    [Fact]
    public void AllCurves_AreScopedToAnimationContext()
    {
        Assert.Contains("animation", LerpFunction.Contexts);
        Assert.Contains("animation", OscillateFunction.Contexts);
        Assert.Contains("animation", FadeInFunction.Contexts);
        Assert.Contains("animation", FadeOutFunction.Contexts);
        Assert.Contains("animation", BounceFunction.Contexts);
        Assert.Contains("animation", WobbleFunction.Contexts);
        Assert.Contains("animation", RandomWalkFunction.Contexts);
    }
}
