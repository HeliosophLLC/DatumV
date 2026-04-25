using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Arrays;

/// <summary>
/// <c>array_transform(arr, x -&gt; expr)</c>: invokes the lambda once per
/// element of the input array and returns a new array of the
/// per-element results. The lambda is unscoped — its body resolves against
/// the standard SQL function surface plus the captured row, and may return
/// any kind. The result's element kind is the lambda body's actual return
/// kind (probed from the first non-null invocation).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Input element shape.</strong> v1 accepts flat (1-D) arrays of
/// any element kind. Multi-dimensional arrays are rejected at signature
/// match (use a sibling unnest path if a flattened iteration is required).
/// </para>
/// <para>
/// <strong>Null handling.</strong> A null array returns a null array of
/// the same element kind; the lambda is not invoked. A null lambda is a
/// runtime error — there is no meaningful identity element for "no
/// lambda". Individual null array elements <em>are</em> passed to the
/// lambda (as typed-null <see cref="ValueRef"/>s); it's the lambda's job
/// to decide whether to coalesce.
/// </para>
/// <para>
/// <strong>Result element kind.</strong> Determined at runtime from the
/// first non-null lambda result. For an empty input or an all-null result,
/// the input's element kind is used as a fallback so the output array
/// still carries a meaningful typed-null shape.
/// </para>
/// </remarks>
public sealed class ArrayTransformFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "array_transform";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Array;

    /// <inheritdoc />
    public static string Description =>
        "Invokes the lambda once per element of the input array and returns "
        + "a new array of the per-element results. The lambda is unscoped: "
        + "it may use any SQL function and may return any kind. v1 accepts "
        + "flat (1-D) input arrays.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("array",  DataKindMatcher.Any,                              IsArray: ArrayMatch.FlatArray),
                new ParameterSpec("lambda", DataKindMatcher.Lambda(null, DataKindMatcher.Any), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            // Best-effort static hint: result element kind = input element kind.
            // The runtime probes the lambda's actual return kind from the first
            // non-null invocation; the static rule only matters for callers that
            // can't see the lambda body (e.g. schema reporters that don't
            // currently type-resolve through lambda args anyway).
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.SameAs(0))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ArrayTransformFunction>(argumentKinds);

    /// <inheritdoc />
    public async ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        // Snapshot args outside the await loop — ReadOnlySpan can't cross awaits.
        ValueRef arrayArg;
        ValueRef lambdaArg;
        {
            ReadOnlySpan<ValueRef> args = arguments.Span;
            arrayArg = args[0];
            lambdaArg = args[1];
        }

        DataKind inputElementKind = arrayArg.ArrayElementKind;

        if (arrayArg.IsNull)
        {
            return ValueRef.NullArray(inputElementKind);
        }

        if (lambdaArg.IsNull)
        {
            throw new FunctionArgumentException(Name,
                "lambda argument must not be NULL.");
        }

        if (frame.LambdaInvoker is null)
        {
            throw new InvalidOperationException(
                "array_transform requires an ILambdaInvoker on the evaluation frame. "
                + "The query pipeline auto-attaches one via ExpressionEvaluator; "
                + "this error indicates a frame built outside that pipeline.");
        }

        int length = arrayArg.GetArrayLength();
        ValueRef[] results = new ValueRef[length];
        ValueRef[] lambdaArgs = new ValueRef[1];
        DataKind? resultElementKind = null;

        // Two storage shapes for the input array payload: a recursive
        // ValueRef[] (built by SQL literals and reference-kind arrays) and
        // a typed primitive array (built by FromPrimitiveArray<T>). The
        // ValueRef[] form gives us per-element ValueRefs for free; the
        // primitive form needs a per-kind unpacking step.
        ValueRef[]? refElements = arrayArg.Materialized as ValueRef[];

        for (int i = 0; i < length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ValueRef elementInput = refElements is not null
                ? refElements[i]
                : ReadPrimitiveElement(arrayArg, i, inputElementKind);

            lambdaArgs[0] = elementInput;
            ValueRef result = await frame.LambdaInvoker.InvokeLambdaAsync(
                lambdaArg, lambdaArgs, frame, cancellationToken).ConfigureAwait(false);

            results[i] = result;
            if (resultElementKind is null && !result.IsNull)
            {
                resultElementKind = result.Kind;
            }
        }

        return ValueRef.FromArray(resultElementKind ?? inputElementKind, results);
    }

    /// <summary>
    /// Reads element <paramref name="index"/> from a typed primitive-array
    /// payload as a freshly-allocated <see cref="ValueRef"/> of the input
    /// element kind. Mirrors the per-kind dispatch in
    /// <c>ArrayGetFunction.ReadElement</c>; kept local because the two
    /// callers diverge enough (this one needs every supported element kind,
    /// that one only needs the numeric ones it currently exercises) that a
    /// shared helper would carry a confusing union of behaviours.
    /// </summary>
    private static ValueRef ReadPrimitiveElement(ValueRef arr, int index, DataKind kind)
    {
        object? payload = arr.Materialized;

        // Boolean is the one kind whose primitive payload can be either bool[]
        // or byte[] (the canonical FromPrimitiveArray form); accept both.
        if (kind == DataKind.Boolean)
        {
            return payload switch
            {
                bool[] bools => ValueRef.FromBoolean(bools[index]),
                byte[] bytes => ValueRef.FromBoolean(bytes[index] != 0),
                _ => throw new FunctionArgumentException(Name,
                    $"Boolean array payload is of unsupported runtime type "
                    + $"'{payload?.GetType().Name ?? "<null>"}'."),
            };
        }

        return kind switch
        {
            DataKind.Int8    => ValueRef.FromInt8(((sbyte[])payload!)[index]),
            DataKind.UInt8   => ValueRef.FromUInt8(((byte[])payload!)[index]),
            DataKind.Int16   => ValueRef.FromInt16(((short[])payload!)[index]),
            DataKind.UInt16  => ValueRef.FromUInt16(((ushort[])payload!)[index]),
            DataKind.Float16 => ValueRef.FromFloat16(((Half[])payload!)[index]),
            DataKind.Int32   => ValueRef.FromInt32(((int[])payload!)[index]),
            DataKind.UInt32  => ValueRef.FromUInt32(((uint[])payload!)[index]),
            DataKind.Float32 => ValueRef.FromFloat32(((float[])payload!)[index]),
            DataKind.Int64   => ValueRef.FromInt64(((long[])payload!)[index]),
            DataKind.UInt64  => ValueRef.FromUInt64(((ulong[])payload!)[index]),
            DataKind.Float64 => ValueRef.FromFloat64(((double[])payload!)[index]),
            DataKind.String  => ValueRef.FromString(((string[])payload!)[index]),
            _ => throw new FunctionArgumentException(Name,
                $"array_transform does not yet support element kind {kind} "
                + $"in the primitive-array payload path."),
        };
    }
}
