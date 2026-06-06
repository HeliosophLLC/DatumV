using System.Text;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// PostgreSQL <c>ascii(text) → int</c>. Returns the Unicode code point of
/// the first character of <c>value</c>, or 0 for the empty string. Null
/// input propagates to null. Surrogate-pair characters are returned as their
/// full code point (matching PG semantics, which use the database encoding —
/// UTF-8 here).
/// </summary>
public sealed class AsciiFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "ascii";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Returns the Unicode code point of the first character of value (0 for the empty string).";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("value", DataKindMatcher.Exact(DataKind.String))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Int32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<AsciiFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Int32));
        }
        string value = arg.AsString();
        if (value.Length == 0)
        {
            return new ValueTask<ValueRef>(ValueRef.FromInt32(0));
        }
        Rune first = value.EnumerateRunes().First();
        return new ValueTask<ValueRef>(ValueRef.FromInt32(first.Value));
    }
}
