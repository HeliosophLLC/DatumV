using System.Globalization;
using System.Text;

namespace Heliosoph.DatumV.Serialization.Csv;

/// <summary>
/// Applies pre-parse line filtering — <c>skip_lines</c> and <c>comment</c>
/// — to a CSV source stream before it reaches the delimiter detector,
/// the header detector, or the type scanner. The downstream parsers stay
/// unchanged: from their perspective the stream simply doesn't have the
/// preamble or comment rows. Used by <see cref="CsvTypeScanner"/> and
/// <see cref="CsvDeserializer"/>.
/// </summary>
/// <remarks>
/// When neither option is set this is a zero-cost pass-through — the
/// caller's original stream comes straight back. When either option is
/// set the filtered output is materialised into a <see cref="MemoryStream"/>
/// so the existing scanner / deserializer keep their two-pass (scan +
/// stream) shape with seek-to-zero between passes. For multi-GB inputs
/// that grows memory linearly; the SEC EDGAR master.idx files this was
/// built for top out at ~33 MB / quarter so the trade-off is fine for
/// the introductory use cases.
/// </remarks>
internal static class CsvStreamPrefilter
{
    /// <summary>
    /// Opens the source stream and applies skip_lines / comment filtering
    /// per the descriptor's options. Returns a seekable stream the caller
    /// owns (must dispose).
    /// </summary>
    public static async Task<Stream> OpenAsync(
        FileFormatDescriptor source,
        CancellationToken cancellationToken)
    {
        int skipLines = GetSkipLines(source.Options);
        char? commentPrefix = GetCommentPrefix(source.Options);

        Stream raw = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (skipLines == 0 && commentPrefix is null) return raw;

        MemoryStream buffered = new();
        try
        {
            using StreamReader reader = new(raw, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024);
            // Force \n line endings on the way out so downstream LineReader
            // sees a normalised stream regardless of the source CRLF / LF mix.
            byte[] newline = [(byte)'\n'];
            long lineNumber = 0;
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                lineNumber++;
                if (lineNumber <= skipLines) continue;
                if (commentPrefix is char cp)
                {
                    ReadOnlySpan<char> trimmed = line.AsSpan().TrimStart();
                    if (trimmed.Length > 0 && trimmed[0] == cp) continue;
                }
                byte[] bytes = Encoding.UTF8.GetBytes(line);
                await buffered.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await buffered.WriteAsync(newline, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            buffered.Dispose();
            throw;
        }
        finally
        {
            await raw.DisposeAsync().ConfigureAwait(false);
        }

        buffered.Position = 0;
        return buffered;
    }

    private static int GetSkipLines(IReadOnlyDictionary<string, string> options)
    {
        if (options.TryGetValue("skip_lines", out string? value) &&
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) &&
            n > 0)
        {
            return n;
        }
        return 0;
    }

    private static char? GetCommentPrefix(IReadOnlyDictionary<string, string> options)
    {
        if (options.TryGetValue("comment", out string? value) && value.Length == 1)
            return value[0];
        return null;
    }
}
