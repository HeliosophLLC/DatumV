using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Arrays;

/// <summary>
/// <c>array_filter(arr, x -&gt; predicate)</c>: invokes the predicate lambda
/// once per element of the input array and returns a new array containing
/// only the elements for which the predicate returned Boolean true. NULL
/// and false drop; any non-Boolean lambda result is rejected.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Input element shape.</strong> Like
/// <see cref="ArrayTransformFunction"/>, v1 accepts flat (1-D) arrays only.
/// Multi-dim arrays are rejected at signature match — per-axis filtering
/// has no well-defined semantic on a tensor.
/// </para>
/// <para>
/// <strong>Null handling.</strong> A null array returns a null array of
/// the same element kind. A null lambda is a runtime error — there is no
/// meaningful identity for "no predicate". Individual null array elements
/// are still passed to the predicate (as typed-null
/// <see cref="ValueRef"/>s); the predicate decides whether to keep them.
/// </para>
/// <para>
/// <strong>Result element kind.</strong> Same as the input element kind.
/// Filter only removes elements; it never changes their kind.
/// </para>
/// </remarks>
public sealed class ArrayFilterFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "array_filter";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Array;

    /// <inheritdoc />
    public static string Description =>
        "Invokes the predicate lambda once per element of the input array "
        + "and returns a new array containing only the elements for which "
        + "the predicate returned Boolean true. v1 accepts flat (1-D) "
        + "input arrays.";

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
            // Filter never changes element kind — result is Array<input-element-kind>.
            ReturnType: ReturnTypeRule.SameAs(0)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ArrayFilterFunction>(argumentKinds);

    /// <inheritdoc />
    public async ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arrayArg;
        ValueRef lambdaArg;
        {
            ReadOnlySpan<ValueRef> args = arguments.Span;
            arrayArg = args[0];
            lambdaArg = args[1];
        }

        DataKind elementKind = arrayArg.ArrayElementKind;

        if (arrayArg.IsNull)
        {
            return ValueRef.NullArray(elementKind);
        }

        if (lambdaArg.IsNull)
        {
            throw new FunctionArgumentException(Name,
                "lambda argument must not be NULL.");
        }

        if (frame.LambdaInvoker is null)
        {
            throw new InvalidOperationException(
                "array_filter requires an ILambdaInvoker on the evaluation frame. "
                + "The query pipeline auto-attaches one via ExpressionEvaluator; "
                + "this error indicates a frame built outside that pipeline.");
        }

        int length = arrayArg.GetArrayLength();
        List<ValueRef> kept = new(length);
        ValueRef[] lambdaArgs = new ValueRef[1];

        // Two payload shapes mirror ArrayTransformFunction: the ValueRef[]
        // form built by SQL literals / reference-kind arrays, and the typed
        // primitive form built by FromPrimitiveArray<T>.
        ValueRef[]? refElements = arrayArg.Materialized as ValueRef[];

        for (int i = 0; i < length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ValueRef element = refElements is not null
                ? refElements[i]
                : ReadPrimitiveElement(arrayArg, i, elementKind);

            lambdaArgs[0] = element;
            ValueRef predicateResult = await frame.LambdaInvoker.InvokeLambdaAsync(
                lambdaArg, lambdaArgs, frame, cancellationToken).ConfigureAwait(false);

            if (predicateResult.IsNull)
            {
                continue;
            }
            if (predicateResult.Kind != DataKind.Boolean)
            {
                throw new FunctionArgumentException(Name,
                    $"predicate lambda must return Boolean; got {predicateResult.Kind}.");
            }
            if (predicateResult.AsBoolean())
            {
                kept.Add(element);
            }
        }

        return ValueRef.FromArray(elementKind, kept.ToArray());
    }

    /// <summary>
    /// Reads element <paramref name="index"/> from a typed primitive-array
    /// payload as a freshly-allocated <see cref="ValueRef"/> of the input
    /// element kind. Mirrors the per-kind dispatch in
    /// <see cref="ArrayTransformFunction"/>; kept local because the two
    /// callers may diverge as more element kinds become reachable from each.
    /// </summary>
    private static ValueRef ReadPrimitiveElement(ValueRef arr, int index, DataKind kind)
    {
        object? payload = arr.Materialized;

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
                $"does not yet support element kind {kind} in the primitive-array payload path."),
        };
    }
}
