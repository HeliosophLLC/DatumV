using System.Globalization;
using System.Text;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// PostgreSQL <c>to_ascii(text) → text</c>. Transliterates Latin-script text
/// to ASCII by decomposing each character into base + combining marks
/// (Unicode NFD) and discarding the non-spacing marks. Letters that have no
/// ASCII equivalent (e.g. CJK, Cyrillic, mathematical symbols) are left as
/// they are. Null input propagates to null.
/// </summary>
/// <remarks>
/// PG's <c>to_ascii</c> historically operated on a single source encoding
/// (LATIN1, LATIN2, etc.) — we instead always treat the input as Unicode and
/// use NFD + non-spacing-mark stripping, which gives the right answer for
/// the common case of removing diacritics from Latin text.
/// </remarks>
public sealed class ToAsciiFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "to_ascii";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Transliterates text to ASCII by removing diacritical marks (Unicode NFD + drop non-spacing marks).";

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
        FunctionMetadata.Validate<ToAsciiFunction>(argumentKinds);

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
        string decomposed = value.Normalize(NormalizationForm.FormD);
        StringBuilder sb = new(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }
        return new ValueTask<ValueRef>(ValueRef.FromString(sb.ToString().Normalize(NormalizationForm.FormC)));
    }
}
