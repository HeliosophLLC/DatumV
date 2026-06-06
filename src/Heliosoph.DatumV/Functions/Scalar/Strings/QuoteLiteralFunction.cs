using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// PostgreSQL <c>quote_literal(text) → text</c>. Returns the argument as a
/// SQL string literal — single-quoted, with internal single quotes doubled.
/// Strings containing backslashes use the PostgreSQL <c>E'…'</c> form with
/// proper backslash escaping. Null input propagates to null; use
/// <see cref="QuoteNullableFunction"/> if you want <c>'NULL'</c> back for
/// nulls.
/// </summary>
public sealed class QuoteLiteralFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "quote_literal";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Returns the argument as a single-quoted SQL string literal.";

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
        FunctionMetadata.Validate<QuoteLiteralFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.String));
        }
        return new ValueTask<ValueRef>(ValueRef.FromString(SqlQuoting.QuoteLiteral(arg.AsString())));
    }
}
