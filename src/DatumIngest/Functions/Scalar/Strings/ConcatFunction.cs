using System.Text;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// Concatenates two or more strings into a single string.
/// </summary>
/// <remarks>
/// <para>
/// Variadic over <see cref="DataKind.String"/> with a minimum of 2 arguments.
/// Null arguments are skipped (matches PostgreSQL <c>concat()</c> semantics):
/// <c>concat('a', NULL, 'b')</c> returns <c>'ab'</c>. The strict
/// null-propagating form (which backs the <c>||</c> operator) is
/// <see cref="ConcatStrictFunction"/>.
/// </para>
/// </remarks>
public sealed class ConcatFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "concat";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Concatenates two or more strings. Null arguments are skipped.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [],
            VariadicTrailing: new VariadicSpec(
                "values",
                DataKindMatcher.Exact(DataKind.String),
                MinOccurrences: 2),
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ConcatFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        StringBuilder builder = new();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].IsNull)
            {
                continue;
            }
            builder.Append(args[i].AsString());
        }
        return new ValueTask<ValueRef>(ValueRef.FromString(builder.ToString()));
    }
}
