using System.Globalization;

using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Drawing;

/// <summary>
/// <c>color(r, g, b)</c> → Color (alpha = 255).
/// <c>color(r, g, b, a)</c> → Color (explicit alpha).
/// </summary>
/// <remarks>
/// Components accept any numeric scalar and are range-checked to
/// <c>[0, 255]</c>. Float values truncate to bytes. Out-of-range or null
/// components raise <see cref="FunctionArgumentException"/>.
/// </remarks>
public sealed class ColorFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "color";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Drawing;

    /// <inheritdoc />
    public static string Description =>
        "Constructs an RGBA color value. Three-arg form sets alpha = 255 (fully opaque); "
        + "four-arg form takes alpha explicitly. All components are in [0, 255] byte units.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("r", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("g", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("b", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Color)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("r", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("g", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("b", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("a", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Color)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ColorFunction>(argumentKinds);

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
                return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Color));
            }
        }
        byte r = ReadByteComponent(args[0], "r");
        byte g = ReadByteComponent(args[1], "g");
        byte b = ReadByteComponent(args[2], "b");
        byte a = args.Length >= 4 ? ReadByteComponent(args[3], "a") : (byte)255;
        return new ValueTask<ValueRef>(ValueRef.FromColor(r, g, b, a));
    }

    internal static byte ReadByteComponent(ValueRef arg, string paramName)
    {
        if (!arg.TryToInt32(out int value))
        {
            throw new FunctionArgumentException(Name,
                $"{paramName} of kind {arg.Kind} could not be widened to Int32.");
        }
        if (value < 0 || value > 255)
        {
            throw new FunctionArgumentException(Name,
                $"{paramName} must be in [0, 255]; got {value}.");
        }
        return (byte)value;
    }
}

/// <summary>
/// <c>color_hex(string)</c> → Color. Parses CSS-style hex strings
/// (<c>'#rgb'</c>, <c>'#rgba'</c>, <c>'#rrggbb'</c>, <c>'#rrggbbaa'</c>).
/// The leading <c>'#'</c> is optional. Hex digits are case-insensitive.
/// </summary>
public sealed class ColorHexFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "color_hex";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Drawing;

    /// <inheritdoc />
    public static string Description =>
        "Parses a CSS-style hex color string into a Color value. "
        + "Accepts '#rgb', '#rgba', '#rrggbb', '#rrggbbaa' (leading '#' optional). "
        + "Three-digit forms expand each digit to a byte by doubling.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("hex", DataKindMatcher.Exact(DataKind.String))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Color)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ColorHexFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Color));
        }
        string hex = arg.AsString().Trim();
        if (hex.StartsWith('#'))
        {
            hex = hex[1..];
        }
        if (!TryParseHex(hex, out byte r, out byte g, out byte b, out byte a))
        {
            throw new FunctionArgumentException(Name,
                $"could not parse '{arg.AsString()}' as a hex color. "
                + "Expected '#rgb', '#rgba', '#rrggbb', or '#rrggbbaa' "
                + "(leading '#' optional).");
        }
        return new ValueTask<ValueRef>(ValueRef.FromColor(r, g, b, a));
    }

    private static bool TryParseHex(string hex, out byte r, out byte g, out byte b, out byte a)
    {
        r = g = b = 0;
        a = 255;
        switch (hex.Length)
        {
            case 3:  // #rgb → expand
            case 4:  // #rgba
                {
                    if (!TryParseNibble(hex[0], out int rN) ||
                        !TryParseNibble(hex[1], out int gN) ||
                        !TryParseNibble(hex[2], out int bN))
                    {
                        return false;
                    }
                    r = (byte)((rN << 4) | rN);
                    g = (byte)((gN << 4) | gN);
                    b = (byte)((bN << 4) | bN);
                    if (hex.Length == 4)
                    {
                        if (!TryParseNibble(hex[3], out int aN)) return false;
                        a = (byte)((aN << 4) | aN);
                    }
                    return true;
                }
            case 6:  // #rrggbb
            case 8:  // #rrggbbaa
                {
                    if (!byte.TryParse(hex.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r) ||
                        !byte.TryParse(hex.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g) ||
                        !byte.TryParse(hex.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b))
                    {
                        return false;
                    }
                    if (hex.Length == 8)
                    {
                        if (!byte.TryParse(hex.AsSpan(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out a))
                        {
                            return false;
                        }
                    }
                    return true;
                }
            default:
                return false;
        }
    }

    private static bool TryParseNibble(char c, out int value)
    {
        if (c >= '0' && c <= '9') { value = c - '0'; return true; }
        if (c >= 'a' && c <= 'f') { value = c - 'a' + 10; return true; }
        if (c >= 'A' && c <= 'F') { value = c - 'A' + 10; return true; }
        value = 0;
        return false;
    }
}
