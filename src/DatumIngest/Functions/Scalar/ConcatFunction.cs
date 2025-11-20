using System.Text;
using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Concatenates two or more strings into a single string.
/// </summary>
/// <remarks>
/// <para>
/// Variadic over <see cref="DataKind.String"/> with a minimum of 2 arguments.
/// Null arguments are skipped (matches PostgreSQL <c>concat()</c> semantics):
/// <c>concat('a', NULL, 'b')</c> returns <c>'ab'</c>. The strict
/// null-propagating form is the <c>||</c> operator, not this function.
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
    public ValueRef Execute(ReadOnlySpan<ValueRef> arguments, in EvaluationFrame frame)
    {
        StringBuilder builder = new();
        for (int i = 0; i < arguments.Length; i++)
        {
            if (arguments[i].IsNull)
            {
                continue;
            }
            builder.Append(arguments[i].AsString());
        }
        return ValueRef.FromString(builder.ToString());
    }
}
