using System.Text;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// Strict, null-propagating string concatenation. Returns <see langword="NULL"/>
/// when any argument is null; otherwise concatenates all arguments. The
/// <c>||</c> operator parses to this function so its semantics match the
/// SQL-standard string-concatenation operator (SQL-92 §6.27).
/// </summary>
/// <remarks>
/// <para>
/// Variadic over <see cref="DataKind.String"/> with a minimum of 2 arguments —
/// same shape as <see cref="ConcatFunction"/>, but where <c>concat()</c> skips
/// nulls (PostgreSQL convention), <c>concat_strict()</c> propagates them
/// (SQL-92 convention). Pick the one that matches your intent:
/// <c>concat('a', NULL, 'b')</c> returns <c>'ab'</c>, while
/// <c>concat_strict('a', NULL, 'b')</c> returns <c>NULL</c>.
/// </para>
/// </remarks>
public sealed class ConcatStrictFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "concat_strict";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Concatenates two or more strings with strict null propagation: any null argument yields NULL. "
        + "Backs the SQL-standard `||` operator.";

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
        FunctionMetadata.Validate<ConcatStrictFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].IsNull)
            {
                return new ValueTask<ValueRef>(ValueRef.Null(DataKind.String));
            }
        }
        StringBuilder builder = new();
        for (int i = 0; i < args.Length; i++)
        {
            builder.Append(args[i].AsString());
        }
        return new ValueTask<ValueRef>(ValueRef.FromString(builder.ToString()));
    }
}
