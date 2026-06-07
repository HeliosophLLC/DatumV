using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// PostgreSQL <c>substring(string, start [, length]) → text</c>. Extracts a
/// substring beginning at 1-based <c>start</c>. When <c>length</c> is given,
/// the result is at most that many characters; the inclusive end position is
/// <c>start + length - 1</c>. When <c>start</c> is less than 1, the effective
/// start clamps to position 1 but the inclusive end position is still
/// computed from the original <c>start</c>, so a portion of the requested
/// length may fall before the string and be dropped. A negative <c>length</c>
/// is rejected. Null in any argument propagates to null. Aliased as
/// <c>substr</c>.
/// </summary>
public sealed class SubstringFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "substring";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Extracts a substring beginning at 1-based start, optionally limited to length characters.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("start", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value",  DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("start",  DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("length", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<SubstringFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].IsNull) return new ValueTask<ValueRef>(ValueRef.Null(DataKind.String));
        }

        string value = args[0].AsString();
        if (!args[1].TryToInt32(out int start))
        {
            throw new FunctionArgumentException(Name, $"argument 'start' of kind {args[1].Kind} is out of range for Int32.");
        }

        int? length = null;
        if (args.Length == 3)
        {
            if (!args[2].TryToInt32(out int lengthValue))
            {
                throw new FunctionArgumentException(Name, $"argument 'length' of kind {args[2].Kind} is out of range for Int32.");
            }
            if (lengthValue < 0)
            {
                throw new FunctionArgumentException(Name, "negative substring length is not allowed.");
            }
            length = lengthValue;
        }

        return new ValueTask<ValueRef>(ValueRef.FromString(SubstringCore(value, start, length)));
    }

    /// <summary>
    /// PG semantics: end is the inclusive 1-based position computed from the
    /// original (unclamped) start, then both endpoints are clipped to the
    /// string. The conversion to 0-based half-open form is
    /// <c>zeroStart = clamp(start - 1, 0, length); zeroEnd = clamp(end, 0, length)</c>
    /// with <c>end = start - 1 + length</c>.
    /// </summary>
    internal static string SubstringCore(string value, int start, int? length)
    {
        int zeroStart = System.Math.Max(0, start - 1);
        if (zeroStart >= value.Length)
        {
            return "";
        }
        if (length is not int len)
        {
            return value[zeroStart..];
        }
        // PG computes end relative to the original start, so a sub-1 start
        // consumes part of the requested length.
        long endExclusive = (long)start - 1L + len;
        int zeroEnd = (int)System.Math.Clamp(endExclusive, 0, value.Length);
        if (zeroEnd <= zeroStart)
        {
            return "";
        }
        return value[zeroStart..zeroEnd];
    }
}
