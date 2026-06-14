using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions.Scalar.Activation;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Math;

/// <summary>
/// <c>clamp(value FLOAT32, min FLOAT32, max FLOAT32) → FLOAT32</c> /
/// <c>clamp(value FLOAT32[], min FLOAT32, max FLOAT32) → FLOAT32[]</c>.
/// Clamps each element into <c>[min, max]</c>. Null value propagates to a
/// typed null. Multi-dim array inputs preserve their shape.
/// </summary>
/// <remarks>
/// Register alias <c>clip</c> via <see cref="FunctionRegistry.RegisterScalarAlias{T}"/>.
/// Throws <see cref="FunctionArgumentException"/> when <c>min &gt; max</c>;
/// <c>NaN</c> in either bound also throws since the half-open comparison
/// would silently let everything through.
/// </remarks>
public sealed class ClampFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "clamp";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Numeric;

    /// <inheritdoc />
    public static string Description =>
        "Clamps a Float32 scalar or vector elementwise into [min, max]: " +
        "clamp(value FLOAT32[, min, max) → FLOAT32. Multi-dim shape is preserved.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("min",   DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("max",   DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("min",   DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("max",   DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ClampFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef value = args[0];
        ValueRef minArg = args[1];
        ValueRef maxArg = args[2];

        if (minArg.IsNull || maxArg.IsNull)
        {
            return new(value.IsArray ? ValueRef.NullArray(DataKind.Float32) : ValueRef.Null(DataKind.Float32));
        }

        float min = minArg.AsFloat32();
        float max = maxArg.AsFloat32();
        if (float.IsNaN(min) || float.IsNaN(max) || min > max)
        {
            throw new FunctionArgumentException(Name,
                $"requires min <= max with both finite; got min={min}, max={max}.");
        }

        return new(ActivationOps.Apply(value, frame, x => System.Math.Clamp(x, min, max)));
    }
}

/// <summary>
/// <c>quantize(value FLOAT32, step FLOAT32) → FLOAT32</c> /
/// <c>quantize(value FLOAT32[], step FLOAT32) → FLOAT32[]</c>. Rounds each
/// element to the nearest multiple of <c>step</c>. Null value
/// propagates to a typed null.
/// </summary>
/// <remarks>
/// Throws when <c>step</c> is non-positive or NaN. Uses
/// midpoint-rounding away from zero, matching <see cref="ClampFunction"/>'s
/// numeric conventions.
/// </remarks>
public sealed class QuantizeFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "quantize";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Numeric;

    /// <inheritdoc />
    public static string Description =>
        "Rounds a Float32 scalar or vector elementwise to the nearest multiple of step.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("step",  DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("step",  DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<QuantizeFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef value = args[0];
        ValueRef stepArg = args[1];

        if (stepArg.IsNull)
        {
            return new(value.IsArray ? ValueRef.NullArray(DataKind.Float32) : ValueRef.Null(DataKind.Float32));
        }

        float step = stepArg.AsFloat32();
        if (float.IsNaN(step) || step <= 0f)
        {
            throw new FunctionArgumentException(Name,
                $"requires step to be a positive finite Float32; got {step}.");
        }

        return new(ActivationOps.Apply(
            value,
            frame,
            x => (float)(System.Math.Round(x / step, MidpointRounding.AwayFromZero) * step)));
    }
}

/// <summary>
/// <c>bucketize(value FLOAT32, boundaries FLOAT32[]) → INT32</c>. Returns the
/// index of the bucket that <c>value</c> falls into given a
/// strictly-ascending boundary vector. The returned index ranges from
/// <c>0</c> (below the first boundary) through <c>boundaries.Length</c>
/// (above the last). Half-open: a value equal to a boundary lands in the
/// bucket to its right. Null value propagates to null.
/// </summary>
/// <remarks>
/// Throws when boundaries are not strictly ascending, are empty, or contain
/// <c>NaN</c>. Mirrors PostgreSQL <c>width_bucket</c> for the "left edge
/// inclusive" half of the convention without requiring a min/max box.
/// </remarks>
public sealed class BucketizeFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "bucketize";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Numeric;

    /// <inheritdoc />
    public static string Description =>
        "Returns the bucket index for a Float32 value given a strictly-ascending Float32 boundary vector.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value",      DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("boundaries", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Int32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<BucketizeFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef value = args[0];
        ValueRef boundariesArg = args[1];

        if (value.IsNull || boundariesArg.IsNull)
        {
            return new(ValueRef.Null(DataKind.Int32));
        }

        float[] boundaries = ActivationOps.ReadFloat32Array(boundariesArg);
        if (boundaries.Length == 0)
        {
            throw new FunctionArgumentException(Name,
                "boundaries vector must contain at least one value.");
        }
        for (int i = 0; i < boundaries.Length; i++)
        {
            if (float.IsNaN(boundaries[i]))
            {
                throw new FunctionArgumentException(Name,
                    "boundaries vector must not contain NaN.");
            }
            if (i > 0 && boundaries[i] <= boundaries[i - 1])
            {
                throw new FunctionArgumentException(Name,
                    $"boundaries must be strictly ascending; element {i} ({boundaries[i]}) " +
                    $"is not greater than element {i - 1} ({boundaries[i - 1]}).");
            }
        }

        float v = value.AsFloat32();
        // Array.BinarySearch returns either the index of the matching boundary
        // (which we want to send to the right-hand bucket — i.e. bucket index
        // i+1) or ~insertionPoint (which already gives the right-hand bucket
        // for the strict-less-than half). Normalising both into the same
        // "next bucket index" gives the half-open convention.
        int hit = Array.BinarySearch(boundaries, v);
        int bucket = hit >= 0 ? hit + 1 : ~hit;
        return new(ValueRef.FromInt32(bucket));
    }
}
