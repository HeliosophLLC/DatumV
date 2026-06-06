using System.Text;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// PostgreSQL <c>unistr(text) → text</c>. Evaluates Unicode escape
/// sequences in <c>value</c> and substitutes their character. Supported
/// escapes:
/// <list type="bullet">
/// <item><description><c>\XXXX</c> — exactly four hex digits, BMP code point.</description></item>
/// <item><description><c>\+XXXXXX</c> — exactly six hex digits, any code point.</description></item>
/// <item><description><c>\uXXXX</c> — exactly four hex digits.</description></item>
/// <item><description><c>\UXXXXXXXX</c> — exactly eight hex digits.</description></item>
/// <item><description><c>\\</c> — literal backslash.</description></item>
/// </list>
/// Any other backslash sequence raises an error. Null input propagates to null.
/// </summary>
public sealed class UnistrFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "unistr";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Evaluates Unicode escapes: \\XXXX, \\+XXXXXX, \\uXXXX, \\UXXXXXXXX (use \\\\ for a literal backslash).";

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
        FunctionMetadata.Validate<UnistrFunction>(argumentKinds);

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

        string input = arg.AsString();
        StringBuilder sb = new(input.Length);
        int i = 0;
        while (i < input.Length)
        {
            char c = input[i];
            if (c != '\\')
            {
                sb.Append(c);
                i++;
                continue;
            }
            if (i + 1 >= input.Length)
            {
                throw new FunctionArgumentException(Name, "dangling backslash at end of string.");
            }
            char next = input[i + 1];
            if (next == '\\')
            {
                sb.Append('\\');
                i += 2;
            }
            else if (next == '+')
            {
                AppendCodePoint(input, i + 2, 6, sb);
                i += 2 + 6;
            }
            else if (next == 'u')
            {
                AppendCodePoint(input, i + 2, 4, sb);
                i += 2 + 4;
            }
            else if (next == 'U')
            {
                AppendCodePoint(input, i + 2, 8, sb);
                i += 2 + 8;
            }
            else if (IsHex(next))
            {
                AppendCodePoint(input, i + 1, 4, sb);
                i += 1 + 4;
            }
            else
            {
                throw new FunctionArgumentException(Name, $"invalid Unicode escape after '\\' (unexpected '{next}').");
            }
        }
        return new ValueTask<ValueRef>(ValueRef.FromString(sb.ToString()));
    }

    private static void AppendCodePoint(string input, int start, int hexLen, StringBuilder sb)
    {
        if (start + hexLen > input.Length)
        {
            throw new FunctionArgumentException(Name, $"Unicode escape requires {hexLen} hex digits, but only {input.Length - start} remain.");
        }
        ReadOnlySpan<char> hex = input.AsSpan(start, hexLen);
        if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int code))
        {
            throw new FunctionArgumentException(Name, $"invalid hex digits in Unicode escape: '{hex}'.");
        }
        if (code < 0 || code > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF))
        {
            throw new FunctionArgumentException(Name, $"Unicode code point U+{code:X} is not a valid scalar value.");
        }
        sb.Append(new Rune(code).ToString());
    }

    private static bool IsHex(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}
