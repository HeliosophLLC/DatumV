using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// PostgreSQL <c>quote_nullable(text) → text</c>. Like
/// <see cref="QuoteLiteralFunction"/>, but returns the bare string
/// <c>'NULL'</c> when the input is null instead of propagating null. Useful
/// for building dynamic SQL where a null operand should serialise as the
/// keyword <c>NULL</c> rather than terminating the statement string.
/// </summary>
public sealed class QuoteNullableFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "quote_nullable";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Like quote_literal, but returns the string 'NULL' for a null input.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("value", DataKindMatcher.Exact(DataKind.String))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<QuoteNullableFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.FromString("NULL"));
        }
        return new ValueTask<ValueRef>(ValueRef.FromString(SqlQuoting.QuoteLiteral(arg.AsString())));
    }
}
