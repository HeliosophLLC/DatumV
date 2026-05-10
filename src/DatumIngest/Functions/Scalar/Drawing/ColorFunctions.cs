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

/// <summary>
/// <c>color_interpolate(from Color, to Color, t Float32)</c> → Color.
/// Linearly blends <c>from</c> toward <c>to</c> by
/// fraction <c>t</c>: <c>t = 0</c> returns <c>from</c>, <c>t = 1</c> returns
/// <c>to</c>, intermediate values mix per-channel. <c>t</c> is clamped to
/// <c>[0, 1]</c>; out-of-range inputs map to the endpoints rather than
/// extrapolating.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Colour space.</strong> Interpolation runs directly on the sRGB
/// byte components (R, G, B, A) — fast and predictable, and matches the
/// look of CSS / canvas gradients that most users have in mind. The
/// mathematically-cleaner perceptual-uniform variant (sRGB → linear →
/// lerp → sRGB) is a separate primitive worth adding when a real consumer
/// asks for it; the simple form covers the common gradient case.
/// </para>
/// <para>
/// <strong>Pairs well with.</strong> Animation curves and the
/// <c>audio_waveform_drawing</c> lambda's column position — e.g.
/// <c>color_interpolate(color_hex('#00d4ff'), color_hex('#ff00aa'), t)</c>
/// inside a per-column lambda paints a horizontal gradient across the
/// rendered waveform.
/// </para>
/// </remarks>
public sealed class ColorInterpolateFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "color_interpolate";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Drawing;

    /// <inheritdoc />
    public static string Description =>
        "Linearly blends two Color values in sRGB byte space by fraction t in [0, 1]. "
        + "t outside the range clamps to the endpoints. Use for gradients across "
        + "animation time, waveform column position, or any normalised parameter.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("from", DataKindMatcher.Exact(DataKind.Color)),
                new ParameterSpec("to",   DataKindMatcher.Exact(DataKind.Color)),
                new ParameterSpec("t",    DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Color)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ColorInterpolateFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Color));
        }
        (byte fr, byte fg, byte fb, byte fa) = args[0].AsColor();
        (byte tr, byte tg, byte tb, byte ta) = args[1].AsColor();
        float t = args[2].ToFloat();
        if (t < 0f) t = 0f;
        else if (t > 1f) t = 1f;

        byte r = Lerp(fr, tr, t);
        byte g = Lerp(fg, tg, t);
        byte b = Lerp(fb, tb, t);
        byte a = Lerp(fa, ta, t);
        return new ValueTask<ValueRef>(ValueRef.FromColor(r, g, b, a));
    }

    private static byte Lerp(byte a, byte b, float t)
    {
        // Round-to-nearest so symmetric mid-points (t=0.5) don't bias toward
        // the lower component due to truncation.
        float blended = a + (b - a) * t;
        int rounded = (int)MathF.Round(blended);
        if (rounded < 0) rounded = 0;
        else if (rounded > 255) rounded = 255;
        return (byte)rounded;
    }
}
