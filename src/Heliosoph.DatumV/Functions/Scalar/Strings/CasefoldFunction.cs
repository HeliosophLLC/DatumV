using System.Globalization;
using System.Text;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// PostgreSQL <c>casefold(text) → text</c>. Returns a case-folded form of
/// <c>value</c> suitable for case-insensitive comparison. Most characters
/// are equivalent to <see cref="UpperFunction"/>'s inverse —
/// <c>ToLowerInvariant</c> — but a handful of characters expand under the
/// Unicode full case-folding rules. Notably, German sharp-s (<c>ß</c>, U+00DF)
/// folds to <c>ss</c>, and the long-s (<c>ſ</c>, U+017F) folds to <c>s</c>.
/// Null input propagates to null.
/// </summary>
/// <remarks>
/// Only the most common expanding case-folds are implemented explicitly;
/// other characters fall back to invariant lowercasing. This covers the
/// PG-documented examples and the cases that matter in practice for Latin
/// text. Adding full Unicode CaseFolding.txt coverage is a separate effort.
/// </remarks>
public sealed class CasefoldFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "casefold";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Returns a case-folded form of the string for case-insensitive comparison (ß → ss, etc.).";

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
        FunctionMetadata.Validate<CasefoldFunction>(argumentKinds);

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
        StringBuilder sb = new(value.Length);
        foreach (Rune rune in value.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case 0x00DF:                // ß → ss
                    sb.Append("ss");
                    break;
                case 0x017F:                // ſ (long s) → s
                    sb.Append('s');
                    break;
                case 0x0130:                // İ (Latin capital with dot above) → i + combining dot
                    sb.Append('i').Append('̇');
                    break;
                case 0x0149:                // ŉ → ʼn  (canonical decomposition + lowercase)
                    sb.Append('ʼ').Append('n');
                    break;
                case 0x1E9E:                // ẞ → ss
                    sb.Append("ss");
                    break;
                case 0xFB00: sb.Append("ff"); break;   // ﬀ
                case 0xFB01: sb.Append("fi"); break;   // ﬁ
                case 0xFB02: sb.Append("fl"); break;   // ﬂ
                case 0xFB03: sb.Append("ffi"); break;  // ﬃ
                case 0xFB04: sb.Append("ffl"); break;  // ﬄ
                case 0xFB05: sb.Append("ft"); break;   // ﬅ
                case 0xFB06: sb.Append("st"); break;   // ﬆ
                default:
                    sb.Append(Rune.ToLower(rune, CultureInfo.InvariantCulture).ToString());
                    break;
            }
        }
        return new ValueTask<ValueRef>(ValueRef.FromString(sb.ToString()));
    }
}
