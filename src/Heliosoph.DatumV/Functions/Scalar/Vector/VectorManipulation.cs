using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions.Scalar.Activation;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Vector;

/// <summary>
/// Helpers shared by the 1-D <c>vec_*</c> manipulation functions over
/// <c>FLOAT32[]</c>. Multi-dim inputs are rejected with a clear error so
/// the caller flattens deliberately rather than silently reinterpreting
/// the row-major buffer.
/// </summary>
internal static class VecManipulationOps
{
    internal static IReadOnlyList<FunctionSignatureVariant> UnaryFloat32ToVector { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("vec", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    internal static void RejectMultiDim(ValueRef arg, EvaluationFrame frame, string fnName)
    {
        if (!arg.IsMultiDim) return;
        DataValue source = arg.ToDataValue(frame.Source);
        ReadOnlySpan<int> shape = source.GetShape(frame.Source, frame.SidecarRegistry);
        throw new FunctionArgumentException(fnName,
            $"{fnName} operates on 1-D vectors only; got shape [{string.Join(", ", shape.ToArray())}]. "
            + "Flatten with the appropriate axis primitive first.");
    }
}

/// <summary>
/// <c>vec(a, b, ...) → FLOAT32[]</c>. Builds a Float32 vector by
/// flattening its arguments in order. Each Float32 scalar contributes one
/// element; each Float32 array contributes its elements. Requires at
/// least one argument.
/// </summary>
/// <remarks>
/// Any null argument yields a null result — there is no defensible
/// position for a "null element" in a packed Float32 buffer. Multi-dim
/// array arguments are flattened in row-major order (the natural
/// interpretation of "elements in order").
/// </remarks>
public sealed class VecFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "vec";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;
    /// <inheritdoc />
    public static string Description =>
        "Constructs a Float32 vector from one or more arguments: vec(a, b, ...) → FLOAT32[]. "
        + "Scalars contribute one element; arrays are flattened in order. Null arg → null result.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [],
            VariadicTrailing: new VariadicSpec(
                "elements",
                DataKindMatcher.Exact(DataKind.Float32),
                MinOccurrences: 1,
                IsArray: ArrayMatch.Either),
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<VecFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;

        int totalLength = 0;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].IsNull) return new(ValueRef.NullArray(DataKind.Float32));
            totalLength += args[i].IsArray ? args[i].GetArrayLength() : 1;
        }

        float[] result = new float[totalLength];
        int offset = 0;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].IsArray)
            {
                float[] src = ActivationOps.ReadFloat32Array(args[i]);
                Array.Copy(src, 0, result, offset, src.Length);
                offset += src.Length;
            }
            else
            {
                result[offset++] = args[i].AsFloat32();
            }
        }
        return new(ValueRef.FromPrimitiveArray(result, DataKind.Float32));
    }
}

/// <summary>
/// <c>vec_concat(v1, v2, ...) → FLOAT32[]</c>. Concatenates two or more
/// Float32 vectors in order. Any null arg → null result.
/// </summary>
public sealed class VecConcatFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "vec_concat";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;
    /// <inheritdoc />
    public static string Description =>
        "Concatenates two or more Float32 vectors in order: "
        + "vec_concat(v1 FLOAT32[], v2 FLOAT32[], ...) → FLOAT32[]. Null arg → null result.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [],
            VariadicTrailing: new VariadicSpec(
                "vectors",
                DataKindMatcher.Exact(DataKind.Float32),
                MinOccurrences: 2,
                IsArray: ArrayMatch.Array),
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<VecConcatFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;

        int totalLength = 0;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].IsNull) return new(ValueRef.NullArray(DataKind.Float32));
            totalLength += args[i].GetArrayLength();
        }

        float[] result = new float[totalLength];
        int offset = 0;
        for (int i = 0; i < args.Length; i++)
        {
            float[] src = ActivationOps.ReadFloat32Array(args[i]);
            Array.Copy(src, 0, result, offset, src.Length);
            offset += src.Length;
        }
        return new(ValueRef.FromPrimitiveArray(result, DataKind.Float32));
    }
}

/// <summary>
/// <c>vec_reverse(vec FLOAT32[]) → FLOAT32[]</c>. Returns a copy with
/// element order reversed.
/// </summary>
public sealed class VecReverseFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "vec_reverse";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;
    /// <inheritdoc />
    public static string Description =>
        "Reverses a Float32 vector: vec_reverse(vec FLOAT32[]) → FLOAT32[]. 1-D only.";
    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures => VecManipulationOps.UnaryFloat32ToVector;
    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<VecReverseFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull) return new(ValueRef.NullArray(DataKind.Float32));
        VecManipulationOps.RejectMultiDim(arg, frame, Name);

        float[] src = ActivationOps.ReadFloat32Array(arg);
        float[] result = new float[src.Length];
        for (int i = 0; i < src.Length; i++) result[i] = src[src.Length - 1 - i];
        return new(ValueRef.FromPrimitiveArray(result, DataKind.Float32));
    }
}

/// <summary>
/// <c>vec_sort(vec FLOAT32[]) → FLOAT32[]</c>. Returns a copy sorted in
/// ascending order. NaN ordering follows <c>Array.Sort</c>'s
/// implementation-defined placement.
/// </summary>
public sealed class VecSortFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "vec_sort";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;
    /// <inheritdoc />
    public static string Description =>
        "Sorts a Float32 vector ascending (returns a copy): "
        + "vec_sort(vec FLOAT32[]) → FLOAT32[]. 1-D only.";
    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures => VecManipulationOps.UnaryFloat32ToVector;
    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<VecSortFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull) return new(ValueRef.NullArray(DataKind.Float32));
        VecManipulationOps.RejectMultiDim(arg, frame, Name);

        float[] result = (float[])ActivationOps.ReadFloat32Array(arg).Clone();
        Array.Sort(result);
        return new(ValueRef.FromPrimitiveArray(result, DataKind.Float32));
    }
}

/// <summary>
/// <c>vec_unique(vec FLOAT32[]) → FLOAT32[]</c>. Returns the distinct
/// elements in first-occurrence order. Bit-exact equality (so
/// <c>-0f == 0f</c> as one entry, but two distinct NaN bit-patterns are
/// distinct).
/// </summary>
public sealed class VecUniqueFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "vec_unique";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;
    /// <inheritdoc />
    public static string Description =>
        "Returns the distinct elements of a Float32 vector in first-occurrence order: "
        + "vec_unique(vec FLOAT32[]) → FLOAT32[]. 1-D only.";
    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures => VecManipulationOps.UnaryFloat32ToVector;
    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<VecUniqueFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull) return new(ValueRef.NullArray(DataKind.Float32));
        VecManipulationOps.RejectMultiDim(arg, frame, Name);

        float[] src = ActivationOps.ReadFloat32Array(arg);
        HashSet<float> seen = new(src.Length);
        List<float> kept = new(src.Length);
        for (int i = 0; i < src.Length; i++)
        {
            if (seen.Add(src[i])) kept.Add(src[i]);
        }
        return new(ValueRef.FromPrimitiveArray(kept.ToArray(), DataKind.Float32));
    }
}

/// <summary>
/// <c>vec_pad(vec FLOAT32[], len INT, fill FLOAT32) → FLOAT32[]</c>.
/// Returns a vector of length <c>len</c> with the source elements followed
/// by enough copies of <c>fill</c> to reach <c>len</c>. If the source is
/// already at least <c>len</c>, it is returned unchanged (no truncation).
/// Negative <c>len</c> raises.
/// </summary>
public sealed class VecPadFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "vec_pad";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;
    /// <inheritdoc />
    public static string Description =>
        "Pads a Float32 vector to length `len` with copies of `fill`. "
        + "Source length ≥ len returns the source unchanged (no truncation). "
        + "1-D only; negative len raises.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("vec",  DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("len",  DataKindMatcher.Family(DataKindFamily.IntegerFamily), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("fill", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<VecPadFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull)
        {
            return new(ValueRef.NullArray(DataKind.Float32));
        }
        VecManipulationOps.RejectMultiDim(args[0], frame, Name);

        int len = args[1].ToInt32();
        if (len < 0)
        {
            throw new FunctionArgumentException(Name, $"len must be ≥ 0, got {len}.");
        }
        float fill = args[2].AsFloat32();

        float[] src = ActivationOps.ReadFloat32Array(args[0]);
        if (src.Length >= len)
        {
            float[] passthrough = (float[])src.Clone();
            return new(ValueRef.FromPrimitiveArray(passthrough, DataKind.Float32));
        }
        float[] result = new float[len];
        Array.Copy(src, 0, result, 0, src.Length);
        for (int i = src.Length; i < len; i++) result[i] = fill;
        return new(ValueRef.FromPrimitiveArray(result, DataKind.Float32));
    }
}

/// <summary>
/// <c>vec_repeat(vec FLOAT32[], count INT) → FLOAT32[]</c>. Returns the
/// source vector tiled <c>count</c> times. <c>count = 0</c> yields an
/// empty vector; negative <c>count</c> raises.
/// </summary>
public sealed class VecRepeatFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "vec_repeat";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;
    /// <inheritdoc />
    public static string Description =>
        "Tiles a Float32 vector `count` times: vec_repeat(vec FLOAT32[], count INT) → FLOAT32[]. "
        + "1-D only; negative count raises.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("vec",   DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("count", DataKindMatcher.Family(DataKindFamily.IntegerFamily), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<VecRepeatFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull) return new(ValueRef.NullArray(DataKind.Float32));
        VecManipulationOps.RejectMultiDim(args[0], frame, Name);

        int count = args[1].ToInt32();
        if (count < 0)
        {
            throw new FunctionArgumentException(Name, $"count must be ≥ 0, got {count}.");
        }

        float[] src = ActivationOps.ReadFloat32Array(args[0]);
        if (count == 0 || src.Length == 0)
        {
            return new(ValueRef.FromPrimitiveArray(Array.Empty<float>(), DataKind.Float32));
        }
        long total = (long)src.Length * count;
        if (total > int.MaxValue)
        {
            throw new FunctionArgumentException(Name,
                $"result would exceed Int32.MaxValue elements ({total}).");
        }
        float[] result = new float[(int)total];
        for (int i = 0; i < count; i++)
        {
            Array.Copy(src, 0, result, i * src.Length, src.Length);
        }
        return new(ValueRef.FromPrimitiveArray(result, DataKind.Float32));
    }
}

/// <summary>
/// <c>linspace(start, stop, n) → FLOAT32[]</c>. Returns <c>n</c> evenly-
/// spaced values from <c>start</c> to <c>stop</c> inclusive. <c>n = 0</c>
/// yields an empty vector; <c>n = 1</c> yields <c>[start]</c> (NumPy
/// convention). Negative <c>n</c> raises.
/// </summary>
public sealed class LinspaceFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "linspace";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;
    /// <inheritdoc />
    public static string Description =>
        "Generates `n` evenly-spaced Float32 values from `start` to `stop` (inclusive): "
        + "linspace(start, stop, n) → FLOAT32[]. n=0 → empty; n=1 → [start].";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("start", DataKindMatcher.Family(DataKindFamily.NumericScalar), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("stop",  DataKindMatcher.Family(DataKindFamily.NumericScalar), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("n",     DataKindMatcher.Family(DataKindFamily.IntegerFamily), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<LinspaceFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull)
        {
            return new(ValueRef.NullArray(DataKind.Float32));
        }

        int n = args[2].ToInt32();
        if (n < 0)
        {
            throw new FunctionArgumentException(Name, $"n must be ≥ 0, got {n}.");
        }
        if (n == 0)
        {
            return new(ValueRef.FromPrimitiveArray(Array.Empty<float>(), DataKind.Float32));
        }

        if (!args[0].TryToFloat(out float start) || !args[1].TryToFloat(out float stop))
        {
            throw new FunctionArgumentException(Name, "start and stop must be numeric.");
        }

        float[] result = new float[n];
        if (n == 1)
        {
            result[0] = start;
            return new(ValueRef.FromPrimitiveArray(result, DataKind.Float32));
        }

        double dstart = start;
        double step = ((double)stop - start) / (n - 1);
        for (int i = 0; i < n; i++)
        {
            result[i] = (float)(dstart + step * i);
        }
        // Anchor the last sample exactly at `stop` so float drift doesn't
        // leave a 1-ulp gap (matches NumPy).
        result[n - 1] = stop;
        return new(ValueRef.FromPrimitiveArray(result, DataKind.Float32));
    }
}

/// <summary>
/// <c>arange(start, stop, step) → FLOAT32[]</c>. Returns values
/// <c>start, start+step, start+2*step, ...</c> while strictly less than
/// (or strictly greater than, for negative <c>step</c>) <c>stop</c>.
/// <c>step = 0</c> raises. Empty result when the range doesn't cross
/// <c>stop</c> in the indicated direction.
/// </summary>
public sealed class ArangeFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "arange";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;
    /// <inheritdoc />
    public static string Description =>
        "Generates values with a fixed step, excluding stop: "
        + "arange(start, stop, step) → FLOAT32[]. step=0 raises; negative step descends.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("start", DataKindMatcher.Family(DataKindFamily.NumericScalar), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("stop",  DataKindMatcher.Family(DataKindFamily.NumericScalar), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("step",  DataKindMatcher.Family(DataKindFamily.NumericScalar), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ArangeFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull)
        {
            return new(ValueRef.NullArray(DataKind.Float32));
        }

        if (!args[0].TryToFloat(out float startF)
            || !args[1].TryToFloat(out float stopF)
            || !args[2].TryToFloat(out float stepF))
        {
            throw new FunctionArgumentException(Name, "start, stop, step must be numeric.");
        }

        double start = startF, stop = stopF, step = stepF;
        if (step == 0.0)
        {
            throw new FunctionArgumentException(Name, "step must be non-zero.");
        }

        double rawCount = System.Math.Ceiling((stop - start) / step);
        if (rawCount <= 0)
        {
            return new(ValueRef.FromPrimitiveArray(Array.Empty<float>(), DataKind.Float32));
        }
        if (rawCount > int.MaxValue)
        {
            throw new FunctionArgumentException(Name,
                $"result would exceed Int32.MaxValue elements ({rawCount}).");
        }
        int count = (int)rawCount;
        float[] result = new float[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = (float)(start + step * i);
        }
        return new(ValueRef.FromPrimitiveArray(result, DataKind.Float32));
    }
}
