using System.Globalization;
using System.Text;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// PostgreSQL <c>initcap(text) → text</c>. Uppercases the first letter of
/// every word and lowercases the rest; word boundaries are runs of
/// non-alphanumeric characters. Uses invariant casing (consistent with
/// <see cref="UpperFunction"/> / <see cref="LowerFunction"/>). Null input
/// propagates to null.
/// </summary>
public sealed class InitcapFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "initcap";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Uppercases the first letter of every word and lowercases the rest (word boundaries are non-alphanumeric runs).";

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
        FunctionMetadata.Validate<InitcapFunction>(argumentKinds);

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
        if (value.Length == 0)
        {
            return new ValueTask<ValueRef>(ValueRef.FromString(""));
        }

        // PG semantics: a word is a maximal run of alphanumeric characters.
        // Within each word the first ALPHABETIC character is uppercased; any
        // subsequent alphabetic characters are lowercased. Digits are left
        // untouched and do not consume the "first-letter" opportunity, so
        // `initcap('123abc')` is `'123Abc'`.
        StringBuilder sb = new(value.Length);
        bool firstLetterSeen = false;
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (!Rune.IsLetterOrDigit(rune))
            {
                sb.Append(rune.ToString());
                firstLetterSeen = false;
                continue;
            }

            if (Rune.IsLetter(rune))
            {
                Rune shifted = firstLetterSeen
                    ? Rune.ToLower(rune, CultureInfo.InvariantCulture)
                    : Rune.ToUpper(rune, CultureInfo.InvariantCulture);
                sb.Append(shifted.ToString());
                firstLetterSeen = true;
            }
            else
            {
                sb.Append(rune.ToString());
            }
        }
        return new ValueTask<ValueRef>(ValueRef.FromString(sb.ToString()));
    }
}
