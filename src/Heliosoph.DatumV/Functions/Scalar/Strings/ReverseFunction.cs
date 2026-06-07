using System.Text;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// PostgreSQL <c>reverse(text) → text</c>. Reverses the order of characters
/// in <c>value</c>. Surrogate pairs are reversed as a unit so emoji and
/// ancient-script characters survive intact. Combining sequences are NOT
/// preserved as units (matches PG behaviour). Null input propagates to null.
/// </summary>
public sealed class ReverseFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "reverse";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Reverses the order of characters in value (surrogate-pair safe).";

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
        FunctionMetadata.Validate<ReverseFunction>(argumentKinds);

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

        string value = arg.AsString();
        if (value.Length <= 1)
        {
            return new ValueTask<ValueRef>(ValueRef.FromString(value));
        }

        // Walk Runes (code points) and prepend each. Keeping surrogate pairs
        // together is the only thing that distinguishes this from
        // string.Create + char.Reverse; the cost is identical for ASCII.
        StringBuilder sb = new(value.Length);
        int i = value.Length;
        while (i > 0)
        {
            int prev = i;
            // Detect a low surrogate at position i-1; if its partner is at i-2,
            // append both as a pair.
            char c = value[i - 1];
            if (char.IsLowSurrogate(c) && i >= 2 && char.IsHighSurrogate(value[i - 2]))
            {
                sb.Append(value[i - 2]);
                sb.Append(c);
                i -= 2;
            }
            else
            {
                sb.Append(c);
                i--;
            }
            if (i == prev) break;
        }
        return new ValueTask<ValueRef>(ValueRef.FromString(sb.ToString()));
    }
}
