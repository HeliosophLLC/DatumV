using System.Globalization;

namespace DatumIngest.Serialization.Fits;

/// <summary>
/// One 80-byte FITS header card: an 8-character keyword, an optional value
/// (separated from the keyword by <c>=&#160;</c> in cols 9-10), and an
/// optional <c>/</c>-prefixed comment in the remaining space.
/// </summary>
/// <remarks>
/// <para>
/// Reserved keyword forms with no <c>=</c> separator — <c>COMMENT</c>,
/// <c>HISTORY</c>, the <c>END</c> marker, and blank keywords — are parsed
/// with <see cref="RawValue"/> set to <c>null</c> and the rest of the card
/// (after the keyword) carried in <see cref="Comment"/>. End-card detection
/// is done by the descriptor's read loop, not by the card parser itself.
/// </para>
/// <para>
/// Quoted string values are unescaped here (FITS doubles single quotes to
/// represent a literal single quote) and trailing spaces inside the quoted
/// value are preserved per the FITS standard — only the trailing padding
/// after the closing quote is discarded.
/// </para>
/// </remarks>
internal readonly record struct FitsCard(string Keyword, string? RawValue, string? Comment)
{
    /// <summary>Each FITS card is exactly 80 ASCII bytes on disk.</summary>
    public const int CardByteSize = 80;

    /// <summary>Standard FITS keyword column width (cols 1-8).</summary>
    public const int KeywordByteSize = 8;

    /// <summary>The <c>END</c> keyword marks the last card of an HDU header.</summary>
    public const string EndKeyword = "END";

    /// <summary>True when the keyword is <c>END</c> — the read loop uses this to stop.</summary>
    public bool IsEnd => string.Equals(Keyword, EndKeyword, StringComparison.Ordinal);

    /// <summary>
    /// Parses the 80-byte card at <paramref name="cardBytes"/> into a <see cref="FitsCard"/>.
    /// The input span MUST be exactly <see cref="CardByteSize"/> bytes.
    /// </summary>
    public static FitsCard Parse(ReadOnlySpan<byte> cardBytes)
    {
        if (cardBytes.Length != CardByteSize)
        {
            throw new ArgumentException(
                $"FITS card must be exactly {CardByteSize} bytes; got {cardBytes.Length}.",
                nameof(cardBytes));
        }

        Span<char> chars = stackalloc char[CardByteSize];
        for (int i = 0; i < CardByteSize; i++)
        {
            // FITS cards are pure ASCII; anything else is a malformed file.
            // Reading as 7-bit ASCII matches the standard and gives the same
            // result as a UTF-8 decode for the valid range.
            chars[i] = (char)cardBytes[i];
        }

        string keyword = new string(chars[..KeywordByteSize]).TrimEnd();

        // Value cards have "= " in cols 9-10 (zero-indexed 8-9). Anything
        // else is a comment-style card (COMMENT, HISTORY, END, blank).
        bool hasValue = chars[KeywordByteSize] == '=' && chars[KeywordByteSize + 1] == ' ';

        if (!hasValue)
        {
            string remainder = new string(chars[KeywordByteSize..]).TrimEnd();
            return new FitsCard(keyword, RawValue: null, Comment: remainder.Length == 0 ? null : remainder);
        }

        ReadOnlySpan<char> valueAndComment = chars[(KeywordByteSize + 2)..];
        SplitValueAndComment(valueAndComment, out string? value, out string? comment);
        return new FitsCard(keyword, value, comment);
    }

    /// <summary>
    /// Splits the value-and-comment portion of a card (cols 11-80) into the
    /// trimmed value text and the comment text. Honours single-quoted
    /// strings (the <c>/</c> inside a quoted value is not a comment delimiter)
    /// and undoes FITS' single-quote-doubling escape.
    /// </summary>
    private static void SplitValueAndComment(
        ReadOnlySpan<char> body,
        out string? value,
        out string? comment)
    {
        int i = 0;
        while (i < body.Length && body[i] == ' ') i++;

        if (i >= body.Length)
        {
            value = string.Empty;
            comment = null;
            return;
        }

        if (body[i] == '\'')
        {
            // Quoted string value. FITS escapes a single quote as ''.
            int start = i + 1;
            int end = start;
            System.Text.StringBuilder sb = new();
            while (end < body.Length)
            {
                if (body[end] == '\'')
                {
                    if (end + 1 < body.Length && body[end + 1] == '\'')
                    {
                        sb.Append('\'');
                        end += 2;
                        continue;
                    }
                    break;
                }
                sb.Append(body[end]);
                end++;
            }

            value = sb.ToString().TrimEnd();
            int afterQuote = end + 1;
            comment = ExtractCommentAfter(body, afterQuote);
            return;
        }

        // Unquoted value: trim trailing spaces, stop at first /.
        int slash = -1;
        int valueEnd = body.Length;
        for (int j = i; j < body.Length; j++)
        {
            if (body[j] == '/')
            {
                slash = j;
                valueEnd = j;
                break;
            }
        }

        ReadOnlySpan<char> raw = body[i..valueEnd].TrimEnd();
        value = raw.Length == 0 ? null : new string(raw);
        comment = slash < 0
            ? null
            : ExtractCommentAfter(body, slash);
    }

    private static string? ExtractCommentAfter(ReadOnlySpan<char> body, int index)
    {
        // Skip any whitespace + the / marker, then take the rest trimmed.
        int j = index;
        while (j < body.Length && body[j] == ' ') j++;
        if (j < body.Length && body[j] == '/') j++;
        while (j < body.Length && body[j] == ' ') j++;
        if (j >= body.Length) return null;
        ReadOnlySpan<char> tail = body[j..].TrimEnd();
        return tail.Length == 0 ? null : new string(tail);
    }

    /// <summary>Parses <see cref="RawValue"/> as an unquoted FITS string (returns the value unmodified if it wasn't quoted on disk — the quote-stripping happens in <see cref="Parse"/>).</summary>
    public string? AsString() => RawValue;

    /// <summary>
    /// Parses <see cref="RawValue"/> as a FITS logical (<c>T</c> / <c>F</c>).
    /// Returns <c>null</c> when the value is missing or unparseable.
    /// </summary>
    public bool? AsBoolean() =>
        RawValue switch
        {
            "T" => true,
            "F" => false,
            _ => null,
        };

    /// <summary>Parses <see cref="RawValue"/> as a FITS integer. Returns <c>null</c> if unparseable.</summary>
    public long? AsInt64() =>
        RawValue is not null
        && long.TryParse(RawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v)
            ? v
            : null;

    /// <summary>Parses <see cref="RawValue"/> as a FITS floating-point value. Returns <c>null</c> if unparseable.</summary>
    public double? AsDouble() =>
        RawValue is not null
        && double.TryParse(
            // FITS uses "D" for double exponents (e.g. 1.2D3). Normalise to E so .NET parses it.
            RawValue.Replace('D', 'E').Replace('d', 'E'),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double v)
            ? v
            : null;
}
