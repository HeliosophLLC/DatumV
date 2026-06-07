using System.Text;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// PostgreSQL <c>translate(string, from, to) → text</c>. Performs a
/// character-by-character substitution: each character in <c>value</c> that
/// appears in <c>from</c> is replaced by the character at the same position
/// in <c>to</c>, or removed entirely if <c>from</c> is longer than
/// <c>to</c>. Null in any argument propagates to null.
/// </summary>
public sealed class TranslateFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "translate";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Performs character-by-character substitution; chars in `from` without a `to` counterpart are deleted.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("from",  DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("to",    DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<TranslateFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.String));
        }

        // PG operates per character (code point in UTF-8). We do the same by
        // enumerating runes; this preserves emoji/non-BMP behaviour where a
        // single conceptual character occupies two UTF-16 code units.
        string value = args[0].AsString();
        string from = args[1].AsString();
        string to = args[2].AsString();
        if (from.Length == 0)
        {
            return new ValueTask<ValueRef>(ValueRef.FromString(value));
        }

        Rune[] fromRunes = from.EnumerateRunes().ToArray();
        Rune[] toRunes = to.EnumerateRunes().ToArray();

        StringBuilder sb = new(value.Length);
        foreach (Rune rune in value.EnumerateRunes())
        {
            int idx = Array.IndexOf(fromRunes, rune);
            if (idx < 0)
            {
                sb.Append(rune.ToString());
            }
            else if (idx < toRunes.Length)
            {
                sb.Append(toRunes[idx].ToString());
            }
            // else: idx >= toRunes.Length → delete this character.
        }
        return new ValueTask<ValueRef>(ValueRef.FromString(sb.ToString()));
    }
}
