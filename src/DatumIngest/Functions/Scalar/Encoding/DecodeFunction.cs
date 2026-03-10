using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Encoding;

/// <summary>
/// PostgreSQL-compatible <c>decode(text, format)</c>. Parses a text value
/// back into a <see cref="DataKind.UInt8"/>[] using the inverse of
/// <see cref="EncodeFunction"/>: <c>'base64'</c>, <c>'hex'</c>, or
/// <c>'escape'</c>. Malformed input throws <see cref="FunctionArgumentException"/>;
/// null inputs propagate to null output.
/// </summary>
public sealed class DecodeFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "decode";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Encoding;

    /// <inheritdoc />
    public static string Description =>
        "Decodes a text value into a UInt8 array using the named format. "
        + "Inverse of `encode`. Supported formats: 'base64', 'hex', 'escape'.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("text", DataKindMatcher.Exact(DataKind.String), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("format", DataKindMatcher.Exact(DataKind.String), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.UInt8))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<DecodeFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef text = args[0];
        ValueRef format = args[1];

        if (text.IsNull || format.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.UInt8));
        }

        string source = text.AsString();
        string fmt = format.AsString();

        byte[] decoded;
        switch (fmt.ToLowerInvariant())
        {
            case "base64":
                try
                {
                    decoded = Convert.FromBase64String(source);
                }
                catch (FormatException ex)
                {
                    throw new FunctionArgumentException(
                        Name,
                        $"input is not valid base64: {ex.Message}");
                }
                break;
            case "hex":
                try
                {
                    decoded = Convert.FromHexString(source);
                }
                catch (FormatException ex)
                {
                    throw new FunctionArgumentException(
                        Name,
                        $"input is not valid hex: {ex.Message}");
                }
                break;
            case "escape":
                decoded = DecodeEscape(source);
                break;
            default:
                throw new FunctionArgumentException(
                    Name,
                    $"unknown encoding format '{fmt}'. Supported formats: 'base64', 'hex', 'escape'.");
        }

        return new ValueTask<ValueRef>(ValueRef.FromBytes(DataKind.UInt8, decoded, isArray: true));
    }

    private static byte[] DecodeEscape(string source)
    {
        byte[] buffer = new byte[source.Length];
        int written = 0;
        int i = 0;
        while (i < source.Length)
        {
            char c = source[i];
            if (c == '\\')
            {
                if (i + 1 >= source.Length)
                {
                    throw new FunctionArgumentException(
                        Name,
                        $"trailing backslash in escape input at position {i}.");
                }
                char next = source[i + 1];
                if (next == '\\')
                {
                    buffer[written++] = (byte)'\\';
                    i += 2;
                }
                else if (IsOctalDigit(next) && i + 3 < source.Length
                    && IsOctalDigit(source[i + 2]) && IsOctalDigit(source[i + 3]))
                {
                    int value = ((next - '0') << 6) | ((source[i + 2] - '0') << 3) | (source[i + 3] - '0');
                    buffer[written++] = (byte)value;
                    i += 4;
                }
                else
                {
                    throw new FunctionArgumentException(
                        Name,
                        $"invalid escape sequence at position {i}; expected '\\\\' or '\\nnn' (three octal digits).");
                }
            }
            else
            {
                if (c > 0x7f)
                {
                    throw new FunctionArgumentException(
                        Name,
                        $"non-ASCII character 0x{(int)c:X4} at position {i}; high-bit bytes must be escaped as '\\nnn'.");
                }
                buffer[written++] = (byte)c;
                i++;
            }
        }
        if (written == buffer.Length)
        {
            return buffer;
        }
        byte[] trimmed = new byte[written];
        Array.Copy(buffer, trimmed, written);
        return trimmed;
    }

    private static bool IsOctalDigit(char c) => c >= '0' && c <= '7';
}
