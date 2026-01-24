using System.Text;
using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// PostgreSQL-compatible <c>encode(bytes, format)</c>. Renders a byte array
/// (<see cref="DataKind.UInt8"/>[]) as text using one of three formats:
/// <list type="bullet">
///   <item><c>'base64'</c> — standard base64 with <c>+</c>, <c>/</c>, <c>=</c>
///   padding. (Postgres breaks output at 76 characters with newlines; this
///   implementation emits the digest unbroken, which round-trips through
///   <c>decode</c> without ambiguity.)</item>
///   <item><c>'hex'</c> — lowercase hexadecimal, two characters per byte.</item>
///   <item><c>'escape'</c> — Postgres bytea escape format: zero bytes and
///   high-bit bytes become <c>\nnn</c> (octal), backslashes double to
///   <c>\\</c>, other bytes pass through as ASCII.</item>
/// </list>
/// </summary>
public sealed class EncodeFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "encode";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Encoding;

    /// <inheritdoc />
    public static string Description =>
        "Encodes a UInt8 array as text. Supported formats: 'base64', 'hex', 'escape'.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("bytes", DataKindMatcher.Exact(DataKind.UInt8), IsArray: ArrayMatch.Array),
                new ParameterSpec("format", DataKindMatcher.Exact(DataKind.String), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<EncodeFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef bytes = args[0];
        ValueRef format = args[1];

        if (bytes.IsNull || format.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.String));
        }

        ReadOnlySpan<byte> data = bytes.AsByteSpan();
        string fmt = format.AsString();

        string encoded = fmt.ToLowerInvariant() switch
        {
            "base64" => Convert.ToBase64String(data),
            "hex" => Convert.ToHexStringLower(data),
            "escape" => EncodeEscape(data),
            _ => throw new FunctionArgumentException(
                Name,
                $"unknown encoding format '{fmt}'. Supported formats: 'base64', 'hex', 'escape'."),
        };

        return new ValueTask<ValueRef>(ValueRef.FromString(encoded));
    }

    private static string EncodeEscape(ReadOnlySpan<byte> data)
    {
        StringBuilder builder = new(data.Length);
        foreach (byte b in data)
        {
            if (b == 0 || b >= 0x80)
            {
                builder.Append('\\');
                builder.Append((char)('0' + ((b >> 6) & 0x7)));
                builder.Append((char)('0' + ((b >> 3) & 0x7)));
                builder.Append((char)('0' + (b & 0x7)));
            }
            else if (b == (byte)'\\')
            {
                builder.Append('\\');
                builder.Append('\\');
            }
            else
            {
                builder.Append((char)b);
            }
        }
        return builder.ToString();
    }
}
