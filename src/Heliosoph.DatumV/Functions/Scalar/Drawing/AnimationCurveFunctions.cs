using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Execution.Contexts;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Drawing;

/// <summary>
/// Phase E animation curves: pure functions of the animation lambda's
/// time parameter <c>t ∈ [0, 1)</c> that produce numeric values for
/// driving Drawing parameters (positions, sizes, opacities, …).
/// </summary>
/// <remarks>
/// <para>
/// Every curve is restricted to <see cref="AnimationContext"/> via
/// <c>Contexts =&gt; [AnimationContext.Name]</c>. They are pure functions
/// of their arguments, but their meaning is tied to the per-frame time
/// passed in by <c>animate_frames</c>; scoping them here keeps SQL
/// completions outside an animation lambda from being polluted by
/// these names.
/// </para>
/// <para>
/// All curves return <see cref="DataKind.Float32"/>. Null inputs in any
/// position return null (standard PG semantics). Range validation
/// throws <see cref="FunctionArgumentException"/> with the offending
/// value in the message.
/// </para>
/// </remarks>
internal static class AnimationCurveHelpers
{
    /// <summary>
    /// Reads any numeric scalar as a <see cref="float"/>. Mirrors the
    /// promotion path used by colour-component readers — concentrating
    /// the "what's a Float32?" decision in one place keeps the curve
    /// functions free of duplicated cast code.
    /// </summary>
    public static float ReadFloat(ValueRef arg, string functionName, string paramName)
    {
        if (!arg.TryToFloat(out float v))
        {
            throw new FunctionArgumentException(functionName,
                $"{paramName} of kind {arg.Kind} could not be widened to Float32.");
        }
        return v;
    }

    public static int ReadInt32(ValueRef arg, string functionName, string paramName)
    {
        if (!arg.TryToInt32(out int v))
        {
            throw new FunctionArgumentException(functionName,
                $"{paramName} of kind {arg.Kind} could not be widened to Int32.");
        }
        return v;
    }

    /// <summary>
    /// Deterministic hash from <c>(seed, index)</c> to <c>[0, 1)</c>. Used
    /// by <c>wobble</c> for phase offsets and by <c>random_walk</c> for
    /// per-step values. A SplitMix-style mix on a 64-bit state gives
    /// reasonable distribution without depending on a stateful RNG —
    /// curves must be pure per <c>t</c> so the same call site produces
    /// the same value across replays of the animation.
    /// </summary>
    public static float HashToUnitFloat(int seed, int index)
    {
        ulong s = (ulong)(uint)seed * 0x9E3779B97F4A7C15UL + (ulong)(uint)index * 0xBF58476D1CE4E5B9UL;
        s ^= s >> 30;
        s *= 0xBF58476D1CE4E5B9UL;
        s ^= s >> 27;
        s *= 0x94D049BB133111EBUL;
        s ^= s >> 31;
        // Mantissa-precision normalisation: top 24 bits → [0, 1).
        return (float)((s >> 40) / (double)(1 << 24));
    }

}

// ---------- lerp ----------

/// <summary>
/// <c>lerp(t, low, high)</c> → Float32. Linear interpolation between
/// <c>low</c> and <c>high</c>. <c>t</c> outside <c>[0, 1]</c>
/// extrapolates linearly — no implicit clamp.
/// </summary>
public sealed class LerpFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "lerp";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Drawing;
    /// <inheritdoc />
    public static string Description =>
        "Linear interpolation: returns low + (high - low) * t. Pure function of its "
        + "args; t outside [0, 1] extrapolates rather than clamping.";

    /// <inheritdoc />
    public static IReadOnlyList<string> Contexts => [AnimationContext.Name];

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("t",    DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("low",  DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("high", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<LerpFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float32));
        }
        float t = AnimationCurveHelpers.ReadFloat(args[0], Name, "t");
        float low = AnimationCurveHelpers.ReadFloat(args[1], Name, "low");
        float high = AnimationCurveHelpers.ReadFloat(args[2], Name, "high");
        return new ValueTask<ValueRef>(ValueRef.FromFloat32(low + (high - low) * t));
    }
}

// ---------- oscillate ----------

/// <summary>
/// <c>oscillate(t, low, high)</c> → Float32 — one full sine cycle over <c>t ∈ [0, 1]</c>.
/// <c>oscillate(t, low, high, frequency)</c> → Float32 — <c>frequency</c> cycles in <c>[0, 1]</c>.
/// </summary>
/// <remarks>
/// At <c>t = 0</c> the value is the midpoint of <c>[low, high]</c>;
/// reaches <c>high</c> at <c>t = 0.25 / frequency</c>, returns to
/// midpoint at <c>0.5 / frequency</c>, reaches <c>low</c> at
/// <c>0.75 / frequency</c>, returns to midpoint at <c>1 / frequency</c>.
/// </remarks>
public sealed class OscillateFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "oscillate";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Drawing;
    /// <inheritdoc />
    public static string Description =>
        "Sine oscillation between low and high over t ∈ [0, 1]. Defaults to one full "
        + "cycle; pass an explicit frequency for multiple cycles per animation.";

    /// <inheritdoc />
    public static IReadOnlyList<string> Contexts => [AnimationContext.Name];

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("t",    DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("low",  DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("high", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("t",         DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("low",       DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("high",      DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("frequency", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<OscillateFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].IsNull) return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float32));
        }
        float t = AnimationCurveHelpers.ReadFloat(args[0], Name, "t");
        float low = AnimationCurveHelpers.ReadFloat(args[1], Name, "low");
        float high = AnimationCurveHelpers.ReadFloat(args[2], Name, "high");
        float frequency = args.Length >= 4 ? AnimationCurveHelpers.ReadFloat(args[3], Name, "frequency") : 1f;

        if (frequency <= 0)
        {
            throw new FunctionArgumentException(Name,
                $"frequency must be positive; got {frequency}.");
        }

        float mid = (low + high) * 0.5f;
        float amp = (high - low) * 0.5f;
        float result = mid + amp * MathF.Sin(2f * MathF.PI * frequency * t);
        return new ValueTask<ValueRef>(ValueRef.FromFloat32(result));
    }
}

// ---------- fade_in ----------

/// <summary>
/// <c>fade_in(t)</c> → Float32 — opacity ramp 0 → 1 over the first
/// 25% of the animation. <c>fade_in(t, duration)</c> uses a custom
/// duration in <c>(0, 1]</c>.
/// </summary>
/// <remarks>
/// Returns a value clamped to <c>[0, 1]</c> suitable for the
/// <c>opacity</c> argument of <c>draw_transformed</c>. The ramp is
/// linear, not eased — compose with <c>oscillate</c> or arithmetic for
/// fancier transitions.
/// </remarks>
public sealed class FadeInFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "fade_in";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Drawing;
    /// <inheritdoc />
    public static string Description =>
        "Linear opacity ramp from 0 to 1 over [0, duration] (default 0.25). "
        + "Useful for the opacity argument of draw_transformed.";

    /// <inheritdoc />
    public static IReadOnlyList<string> Contexts => [AnimationContext.Name];

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("t", DataKindMatcher.Family(DataKindFamily.NumericScalar))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("t",        DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("duration", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<FadeInFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].IsNull) return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float32));
        }
        float t = AnimationCurveHelpers.ReadFloat(args[0], Name, "t");
        float duration = args.Length >= 2 ? AnimationCurveHelpers.ReadFloat(args[1], Name, "duration") : 0.25f;
        if (duration <= 0)
        {
            throw new FunctionArgumentException(Name,
                $"duration must be positive; got {duration}.");
        }
        float opacity = System.Math.Clamp(t / duration, 0f, 1f);
        return new ValueTask<ValueRef>(ValueRef.FromFloat32(opacity));
    }
}

// ---------- fade_out ----------

/// <summary>
/// <c>fade_out(t)</c> → Float32 — opacity ramp 1 → 0 over the last 25%
/// of the animation. <c>fade_out(t, duration)</c> uses a custom
/// duration in <c>(0, 1]</c>.
/// </summary>
public sealed class FadeOutFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "fade_out";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Drawing;
    /// <inheritdoc />
    public static string Description =>
        "Linear opacity ramp from 1 to 0 over [1 - duration, 1] (default 0.25). "
        + "Mirror of fade_in for animation tails.";

    /// <inheritdoc />
    public static IReadOnlyList<string> Contexts => [AnimationContext.Name];

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("t", DataKindMatcher.Family(DataKindFamily.NumericScalar))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("t",        DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("duration", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<FadeOutFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].IsNull) return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float32));
        }
        float t = AnimationCurveHelpers.ReadFloat(args[0], Name, "t");
        float duration = args.Length >= 2 ? AnimationCurveHelpers.ReadFloat(args[1], Name, "duration") : 0.25f;
        if (duration <= 0)
        {
            throw new FunctionArgumentException(Name,
                $"duration must be positive; got {duration}.");
        }
        float opacity = System.Math.Clamp((1f - t) / duration, 0f, 1f);
        return new ValueTask<ValueRef>(ValueRef.FromFloat32(opacity));
    }
}

// ---------- bounce ----------

/// <summary>
/// <c>bounce(t, low, high)</c> → Float32 — ease-out bouncing motion
/// from <c>low</c> (at <c>t = 0</c>) to <c>high</c> (at <c>t = 1</c>),
/// with diminishing bounces along the way. <c>bounce(t, low, high, bounces)</c>
/// controls the bounce count (default 3).
/// </summary>
/// <remarks>
/// Uses the standard CSS easeOutBounce curve, lerped between
/// <c>low</c> and <c>high</c>. The function reaches <c>high</c>
/// exactly at <c>t = 1</c> regardless of <c>bounces</c>.
/// </remarks>
public sealed class BounceFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "bounce";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Drawing;
    /// <inheritdoc />
    public static string Description =>
        "Ease-out bouncing motion from low to high over t ∈ [0, 1] with diminishing "
        + "bounces. Standard CSS easeOutBounce shape; optional bounce count (default 3).";

    /// <inheritdoc />
    public static IReadOnlyList<string> Contexts => [AnimationContext.Name];

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("t",    DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("low",  DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("high", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("t",       DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("low",     DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("high",    DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("bounces", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<BounceFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].IsNull) return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float32));
        }
        float t = AnimationCurveHelpers.ReadFloat(args[0], Name, "t");
        float low = AnimationCurveHelpers.ReadFloat(args[1], Name, "low");
        float high = AnimationCurveHelpers.ReadFloat(args[2], Name, "high");
        int bounces = args.Length >= 4 ? AnimationCurveHelpers.ReadInt32(args[3], Name, "bounces") : 3;
        if (bounces <= 0)
        {
            throw new FunctionArgumentException(Name,
                $"bounces must be positive; got {bounces}.");
        }

        // CSS easeOutBounce, but parameterised by bounce count. The default
        // 3-bounce shape uses the CSS-standard segment boundaries; for other
        // bounce counts we synthesise a damped-sine envelope. Same overall
        // 0→1 trajectory in both, just different bumpiness.
        float factor = bounces == 3 ? CssEaseOutBounce(t) : DampedBounce(t, bounces);
        float result = low + (high - low) * factor;
        return new ValueTask<ValueRef>(ValueRef.FromFloat32(result));
    }

    private static float CssEaseOutBounce(float t)
    {
        const float n1 = 7.5625f;
        const float d1 = 2.75f;
        if (t < 1f / d1) { return n1 * t * t; }
        if (t < 2f / d1) { t -= 1.5f / d1; return n1 * t * t + 0.75f; }
        if (t < 2.5f / d1) { t -= 2.25f / d1; return n1 * t * t + 0.9375f; }
        t -= 2.625f / d1;
        return n1 * t * t + 0.984375f;
    }

    private static float DampedBounce(float t, int bounces)
    {
        // 1 − |cos(π·bounces·t)| · (1 − t)  produces N bounces between 0 and 1
        // with linearly-decaying amplitude. Hits 1 exactly at t=1.
        float amplitude = 1f - t;
        float wave = MathF.Abs(MathF.Cos(MathF.PI * bounces * t));
        return 1f - wave * amplitude;
    }
}

// ---------- wobble ----------

/// <summary>
/// <c>wobble(t, low, high)</c> → Float32 — irregular organic oscillation
/// between <c>low</c> and <c>high</c>. <c>wobble(t, low, high, seed)</c>
/// chooses a deterministic variant.
/// </summary>
/// <remarks>
/// Sum-of-sines at three frequencies (1×, 2×, 4×) with seed-derived
/// phase offsets — produces a more natural "alive" feel than a plain
/// <c>oscillate</c> while remaining a pure function of <c>t</c>.
/// </remarks>
public sealed class WobbleFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "wobble";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Drawing;
    /// <inheritdoc />
    public static string Description =>
        "Irregular organic oscillation between low and high. Sum-of-sines with "
        + "seed-derived phase offsets — same seed produces the same wobble.";

    /// <inheritdoc />
    public static IReadOnlyList<string> Contexts => [AnimationContext.Name];

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("t",    DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("low",  DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("high", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("t",    DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("low",  DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("high", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("seed", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<WobbleFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].IsNull) return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float32));
        }
        float t = AnimationCurveHelpers.ReadFloat(args[0], Name, "t");
        float low = AnimationCurveHelpers.ReadFloat(args[1], Name, "low");
        float high = AnimationCurveHelpers.ReadFloat(args[2], Name, "high");
        int seed = args.Length >= 4 ? AnimationCurveHelpers.ReadInt32(args[3], Name, "seed") : 0;

        // Phase offsets in [0, 2π) derived from seed. Three octaves so the
        // wobble has a recognisable rhythm without dissolving into noise.
        float p1 = AnimationCurveHelpers.HashToUnitFloat(seed, 0) * 2f * MathF.PI;
        float p2 = AnimationCurveHelpers.HashToUnitFloat(seed, 1) * 2f * MathF.PI;
        float p3 = AnimationCurveHelpers.HashToUnitFloat(seed, 2) * 2f * MathF.PI;

        float wave =
            MathF.Sin(2f * MathF.PI * 1f * t + p1) +
            0.5f * MathF.Sin(2f * MathF.PI * 2f * t + p2) +
            0.25f * MathF.Sin(2f * MathF.PI * 4f * t + p3);
        // Normalise from approximate range [-1.75, 1.75] to [-1, 1].
        wave /= 1.75f;

        float mid = (low + high) * 0.5f;
        float amp = (high - low) * 0.5f;
        return new ValueTask<ValueRef>(ValueRef.FromFloat32(mid + amp * wave));
    }
}

// ---------- random_walk ----------

/// <summary>
/// <c>random_walk(t, low, high)</c> → Float32 — deterministic random
/// walk between <c>low</c> and <c>high</c>, sampling 10 steps across
/// <c>[0, 1]</c>.
/// <c>random_walk(t, low, high, steps)</c> changes the step count.
/// <c>random_walk(t, low, high, steps, seed)</c> chooses a specific walk.
/// </summary>
/// <remarks>
/// Per-step values are <see cref="AnimationCurveHelpers.HashToUnitFloat(int, int)"/>
/// of <c>(seed, step_index)</c>, lerped between adjacent steps to keep
/// the curve continuous. Same <c>seed</c> gives the same walk shape;
/// changing <c>steps</c> changes only the sample density, not the
/// underlying randomness.
/// </remarks>
public sealed class RandomWalkFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "random_walk";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Drawing;
    /// <inheritdoc />
    public static string Description =>
        "Deterministic random walk between low and high. The walk's shape is fixed by seed "
        + "(default 0) and sampled at steps points (default 10); intermediate values lerp.";

    /// <inheritdoc />
    public static IReadOnlyList<string> Contexts => [AnimationContext.Name];

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("t",    DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("low",  DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("high", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("t",     DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("low",   DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("high",  DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("steps", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("t",     DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("low",   DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("high",  DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("steps", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("seed",  DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<RandomWalkFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].IsNull) return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float32));
        }
        float t = AnimationCurveHelpers.ReadFloat(args[0], Name, "t");
        float low = AnimationCurveHelpers.ReadFloat(args[1], Name, "low");
        float high = AnimationCurveHelpers.ReadFloat(args[2], Name, "high");
        int steps = args.Length >= 4 ? AnimationCurveHelpers.ReadInt32(args[3], Name, "steps") : 10;
        int seed = args.Length >= 5 ? AnimationCurveHelpers.ReadInt32(args[4], Name, "seed") : 0;
        if (steps <= 0)
        {
            throw new FunctionArgumentException(Name,
                $"steps must be positive; got {steps}.");
        }

        // Map t ∈ [0, 1] to a step index + local interpolant. Clamp t to
        // [0, 1] so extrapolation outside the animation range doesn't shoot
        // into unseeded territory.
        float scaled = System.Math.Clamp(t, 0f, 1f) * steps;
        int stepIdx = (int)MathF.Floor(scaled);
        if (stepIdx >= steps) { stepIdx = steps - 1; }
        float local = scaled - stepIdx;

        float u0 = AnimationCurveHelpers.HashToUnitFloat(seed, stepIdx);
        float u1 = AnimationCurveHelpers.HashToUnitFloat(seed, stepIdx + 1);
        float interp = u0 + (u1 - u0) * local;
        return new ValueTask<ValueRef>(ValueRef.FromFloat32(low + (high - low) * interp));
    }
}
