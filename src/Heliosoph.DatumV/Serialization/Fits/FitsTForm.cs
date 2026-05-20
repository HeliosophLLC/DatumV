using System.Globalization;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Serialization.Fits;

/// <summary>
/// Parsed FITS <c>TFORMn</c> card describing one binary-table column's
/// on-disk layout: the type character, the repeat count, and the derived
/// element / total byte sizes.
/// </summary>
/// <remarks>
/// <para>
/// FITS BINTABLE <c>TFORM</c> values look like <c>rTa</c>: an optional
/// integer repeat count <c>r</c>, a single character type code <c>T</c>,
/// and optional additional precision <c>a</c> (used for bit / variable-length
/// arrays — neither supported in v1). Examples: <c>"E"</c> = one Float32,
/// <c>"3J"</c> = array of three Int32, <c>"8A"</c> = an 8-character string
/// (the <c>A</c> repeat is the field byte width, not an array count).
/// </para>
/// <para>
/// v1 supports these type codes: <c>L</c> (logical), <c>B</c> (unsigned
/// byte), <c>I</c> (Int16), <c>J</c> (Int32), <c>K</c> (Int64),
/// <c>E</c> (Float32), <c>D</c> (Float64), <c>A</c> (fixed-width string).
/// <c>P</c>/<c>Q</c> (variable-length array), <c>C</c>/<c>M</c> (complex),
/// and <c>X</c> (bit) throw <see cref="NotSupportedException"/> from
/// <see cref="Parse"/>.
/// </para>
/// </remarks>
internal readonly record struct FitsTForm(
    char TypeChar,
    int Repeat,
    DataKind ElementKind,
    int ElementByteSize)
{
    /// <summary>
    /// True for an array-shaped column — repeat &gt; 1 AND the type char isn't
    /// <c>A</c>. <c>A</c> repeat is the string field byte width, not an array
    /// of single chars.
    /// </summary>
    public bool IsArray => Repeat > 1 && TypeChar != 'A';

    /// <summary>String column field byte width when <see cref="TypeChar"/> == <c>A</c>; otherwise unused.</summary>
    public int StringByteWidth => TypeChar == 'A' ? Repeat : 0;

    /// <summary>Bytes this column occupies in one BINTABLE row on disk.</summary>
    public int RowByteSize => TypeChar == 'A' ? Repeat : ElementByteSize * Repeat;

    /// <summary>
    /// Parses a <c>TFORMn</c> value. Throws <see cref="NotSupportedException"/>
    /// for v1-unsupported type codes (P/Q/C/M/X), and <see cref="FormatException"/>
    /// for syntactically invalid values.
    /// </summary>
    public static FitsTForm Parse(string tform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tform);
        string trimmed = tform.Trim();

        int i = 0;
        while (i < trimmed.Length && char.IsDigit(trimmed[i])) i++;

        int repeat;
        if (i == 0)
        {
            repeat = 1;
        }
        else
        {
            if (!int.TryParse(trimmed.AsSpan(0, i), NumberStyles.None, CultureInfo.InvariantCulture, out repeat)
                || repeat < 0)
            {
                throw new FormatException($"Invalid TFORM repeat count in '{tform}'.");
            }
        }

        if (i >= trimmed.Length)
        {
            throw new FormatException($"TFORM '{tform}' is missing a type character.");
        }

        char typeChar = trimmed[i];
        // Trailing precision (e.g. for P/Q descriptors) is allowed by the spec
        // but irrelevant to v1's supported subset — we just ignore anything
        // after the type char so we don't choke on real-world files.

        (DataKind kind, int elementBytes) = MapTypeChar(typeChar, tform);

        // Spec allows repeat=0 as a degenerate "no field" — treat as a hard
        // error for v1; callers shouldn't see it from a well-formed source.
        if (repeat == 0)
        {
            throw new FormatException($"TFORM '{tform}' has zero repeat count.");
        }

        return new FitsTForm(typeChar, repeat, kind, elementBytes);
    }

    private static (DataKind Kind, int ElementBytes) MapTypeChar(char typeChar, string tform) =>
        typeChar switch
        {
            'L' => (DataKind.Boolean, 1),
            'B' => (DataKind.UInt8, 1),
            'I' => (DataKind.Int16, 2),
            'J' => (DataKind.Int32, 4),
            'K' => (DataKind.Int64, 8),
            'E' => (DataKind.Float32, 4),
            'D' => (DataKind.Float64, 8),
            'A' => (DataKind.String, 1),
            'P' or 'Q' => throw new NotSupportedException(
                $"FITS variable-length array columns ({typeChar} in TFORM '{tform}') aren't supported in v1. " +
                "Catalogs that need them should land via a follow-up that walks the heap area."),
            'C' or 'M' => throw new NotSupportedException(
                $"FITS complex columns ({typeChar} in TFORM '{tform}') aren't supported in v1."),
            'X' => throw new NotSupportedException(
                $"FITS bit-array columns (X in TFORM '{tform}') aren't supported in v1."),
            _ => throw new FormatException($"Unknown TFORM type character '{typeChar}' in '{tform}'."),
        };
}
