using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// PostgreSQL <c>lpad(string, length [, fill]) → text</c>. Pads <c>value</c>
/// on the left with the <c>fill</c> string (default single space) until it
/// reaches <c>length</c> characters. If <c>value</c> is already longer than
/// <c>length</c>, the right side is truncated. An empty <c>fill</c> means no
/// padding is added — the result is just <c>value</c> truncated to
/// <c>length</c>. Null in any argument propagates to null.
/// </summary>
public sealed class LpadFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "lpad";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Left-pads value with fill (default space) until length is reached; truncates from the right if longer.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value",  DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("length", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value",  DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("length", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("fill",   DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<LpadFunction>(argumentKinds);

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
        return new ValueTask<ValueRef>(ValueRef.FromString(PadStrings.Pad(args, leftPad: true, Name)));
    }
}
