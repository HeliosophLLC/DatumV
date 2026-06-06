using System.Text;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// PostgreSQL <c>overlay(string, new, start [, count]) → text</c>. Replaces
/// <c>count</c> characters of <c>value</c> beginning at 1-based <c>start</c>
/// with <c>new</c>. When <c>count</c> is omitted it defaults to the length
/// of <c>new</c>. A <c>start</c> beyond the end of <c>value</c> appends; a
/// <c>start</c> of zero or negative is clamped to position 1. A negative
/// <c>count</c> is rejected. Null in any argument propagates to null.
/// </summary>
public sealed class OverlayFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "overlay";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Replaces count characters of value starting at 1-based start with new (count defaults to length(new)).";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("new",   DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("start", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("new",   DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("start", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("count", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<OverlayFunction>(argumentKinds);

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
        string replacement = args[1].AsString();
        if (!args[2].TryToInt32(out int start))
        {
            throw new FunctionArgumentException(Name, $"argument 'start' of kind {args[2].Kind} is out of range for Int32.");
        }

        int count = replacement.Length;
        if (args.Length == 4)
        {
            if (!args[3].TryToInt32(out count))
            {
                throw new FunctionArgumentException(Name, $"argument 'count' of kind {args[3].Kind} is out of range for Int32.");
            }
            if (count < 0)
            {
                throw new FunctionArgumentException(Name, "negative overlay count is not allowed.");
            }
        }

        int zeroStart = System.Math.Clamp(start - 1, 0, value.Length);
        int zeroEnd = (int)System.Math.Clamp((long)zeroStart + count, zeroStart, value.Length);

        StringBuilder sb = new(value.Length - (zeroEnd - zeroStart) + replacement.Length);
        sb.Append(value, 0, zeroStart);
        sb.Append(replacement);
        sb.Append(value, zeroEnd, value.Length - zeroEnd);
        return new ValueTask<ValueRef>(ValueRef.FromString(sb.ToString()));
    }
}
