using System.Text;

namespace DatumIngest.Catalog.Providers;

/// <summary>
/// Reads CSV lines from a byte stream into a reusable character buffer, yielding
/// <see cref="ReadOnlySpan{T}"/> slices without per-line string allocation.
/// Handles UTF-8 decoding, <c>\r\n</c> and <c>\n</c> line endings, and automatic
/// buffer growth for lines exceeding the initial capacity.
/// </summary>
/// <remarks>
/// <para>
/// Designed for hot ingestion loops where <see cref="System.IO.StreamReader.ReadLine"/>
/// is the dominant allocation source. On a 32 M-row numeric CSV, this eliminates
/// ~32 million transient <see cref="string"/> allocations (~960 MB of GC pressure).
/// </para>
/// <para>
/// The span returned by <see cref="TryReadLine"/> is valid only until the next call
/// to <see cref="TryReadLine"/> or <see cref="TryReadLogicalLine"/>. Callers must
/// fully consume each line before advancing.
/// </para>
/// </remarks>
internal sealed class CsvLineReader : IDisposable
{
    private readonly Stream _stream;
    private readonly byte[] _byteBuffer;
    private readonly Decoder _decoder;
    private char[] _charBuffer;
    private int _charCount;
    private int _charPosition;
    private bool _endOfStream;

    /// <summary>
    /// Reusable buffer for assembling logical lines that span multiple physical lines
    /// (embedded newlines in quoted fields). Allocated lazily on the first multi-line field.
    /// </summary>
    private char[]? _multiLineBuffer;

    /// <summary>
    /// Initializes a new <see cref="CsvLineReader"/> that reads from the specified stream.
    /// The caller retains ownership of the stream.
    /// </summary>
    /// <param name="stream">A readable stream to decode lines from.</param>
    /// <param name="bufferSize">
    /// Size of both the byte read buffer and the initial character buffer, in bytes.
    /// Larger buffers reduce the number of I/O reads but increase memory footprint.
    /// </param>
    public CsvLineReader(Stream stream, int bufferSize = 65536)
    {
        _stream = stream;
        _byteBuffer = new byte[bufferSize];
        _charBuffer = new char[bufferSize];
        _decoder = Encoding.UTF8.GetDecoder();
    }

    /// <summary>
    /// Attempts to read the next physical line from the stream. The line terminator
    /// (<c>\n</c> or <c>\r\n</c>) is consumed but not included in the returned span.
    /// </summary>
    /// <param name="line">
    /// On success, a span of characters representing the line content (excluding
    /// the line terminator). Valid until the next call.
    /// </param>
    /// <returns><c>true</c> if a line was read; <c>false</c> at end of stream.</returns>
    public bool TryReadLine(out ReadOnlySpan<char> line)
    {
        while (true)
        {
            ReadOnlySpan<char> available = _charBuffer.AsSpan(_charPosition, _charCount - _charPosition);
            int newlineIndex = available.IndexOf('\n');

            if (newlineIndex >= 0)
            {
                int lineLength = newlineIndex;
                if (lineLength > 0 && available[lineLength - 1] == '\r')
                {
                    lineLength--;
                }

                line = available[..lineLength];
                _charPosition += newlineIndex + 1;
                return true;
            }

            if (_endOfStream)
            {
                if (_charPosition < _charCount)
                {
                    line = available;
                    if (line.Length > 0 && line[^1] == '\r')
                    {
                        line = line[..^1];
                    }

                    _charPosition = _charCount;
                    return true;
                }

                line = default;
                return false;
            }

            CompactAndFill();
        }
    }

    /// <summary>
    /// Reads the next logical CSV line, handling RFC 4180 multi-physical-line quoted fields.
    /// When the line contains an odd number of quote characters, continuation lines are
    /// read and concatenated until the quotes balance.
    /// </summary>
    /// <param name="line">
    /// On success, a span of characters representing the full logical line.
    /// For single-physical-line rows (the common case), this is a zero-allocation
    /// slice of the internal buffer. For multi-line quoted fields, this points into
    /// a reusable assembly buffer.
    /// </param>
    /// <returns><c>true</c> if a line was read; <c>false</c> at end of stream.</returns>
    public bool TryReadLogicalLine(out ReadOnlySpan<char> line)
    {
        if (!TryReadLine(out ReadOnlySpan<char> physicalLine))
        {
            line = default;
            return false;
        }

        // Fast path: no quotes at all — the vast majority of numeric CSV rows.
        if (!physicalLine.Contains('"'))
        {
            line = physicalLine;
            return true;
        }

        int quoteCount = CountQuotes(physicalLine);
        if (quoteCount % 2 == 0)
        {
            line = physicalLine;
            return true;
        }

        // Slow path: multi-line quoted field. Copy into a reusable buffer so the
        // result survives subsequent TryReadLine calls that refill _charBuffer.
        int multiLineLength = physicalLine.Length;
        EnsureMultiLineCapacity(multiLineLength + 1024);
        physicalLine.CopyTo(_multiLineBuffer);

        while (quoteCount % 2 != 0)
        {
            if (!TryReadLine(out ReadOnlySpan<char> continuation))
            {
                break;
            }

            EnsureMultiLineCapacity(multiLineLength + 1 + continuation.Length);
            _multiLineBuffer![multiLineLength] = '\n';
            multiLineLength++;
            continuation.CopyTo(_multiLineBuffer.AsSpan(multiLineLength));
            multiLineLength += continuation.Length;
            quoteCount += CountQuotes(continuation);
        }

        line = _multiLineBuffer.AsSpan(0, multiLineLength);
        return true;
    }

    /// <summary>
    /// Reads the first line from the stream as a materialized <see cref="string"/>.
    /// Intended for the header row, which is parsed once at startup and retained for
    /// schema construction.
    /// </summary>
    /// <returns>The first line, or <c>null</c> if the stream is empty.</returns>
    public string? ReadLineAsString()
    {
        if (TryReadLine(out ReadOnlySpan<char> line))
        {
            return line.ToString();
        }

        return null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Stream ownership belongs to the caller.
    }

    // ──────────────────── Buffer management ────────────────────

    /// <summary>
    /// Shifts unconsumed characters to the front of the buffer, then fills the
    /// remainder by reading and decoding bytes from the underlying stream.
    /// Grows the character buffer when a single line exceeds the current capacity.
    /// </summary>
    private void CompactAndFill()
    {
        int remaining = _charCount - _charPosition;

        if (remaining > 0 && _charPosition > 0)
        {
            _charBuffer.AsSpan(_charPosition, remaining).CopyTo(_charBuffer);
        }

        _charPosition = 0;
        _charCount = remaining;

        // If the buffer is full after compaction, the current line extends beyond
        // the buffer capacity — grow to accommodate it.
        if (_charCount == _charBuffer.Length)
        {
            int newSize = _charBuffer.Length * 2;
            char[] grown = new char[newSize];
            _charBuffer.AsSpan(0, _charCount).CopyTo(grown);
            _charBuffer = grown;
        }

        int bytesRead = _stream.Read(_byteBuffer, 0, _byteBuffer.Length);

        if (bytesRead == 0)
        {
            _endOfStream = true;
            return;
        }

        // Ensure char buffer has room. For pure ASCII (the common case), one byte
        // decodes to one char. Multi-byte UTF-8 sequences produce fewer chars, so
        // bytesRead is a safe upper bound.
        int requiredCapacity = _charCount + bytesRead;

        if (requiredCapacity > _charBuffer.Length)
        {
            int newSize = Math.Max(requiredCapacity, _charBuffer.Length * 2);
            char[] grown = new char[newSize];
            _charBuffer.AsSpan(0, _charCount).CopyTo(grown);
            _charBuffer = grown;
        }

        int charsDecoded = _decoder.GetChars(
            _byteBuffer.AsSpan(0, bytesRead),
            _charBuffer.AsSpan(_charCount),
            flush: false);

        _charCount += charsDecoded;
    }

    /// <summary>
    /// Ensures the multi-line assembly buffer has at least the specified capacity.
    /// </summary>
    private void EnsureMultiLineCapacity(int required)
    {
        if (_multiLineBuffer is not null && _multiLineBuffer.Length >= required)
        {
            return;
        }

        int newSize = _multiLineBuffer is null
            ? Math.Max(required, 4096)
            : Math.Max(required, _multiLineBuffer.Length * 2);

        char[] grown = new char[newSize];

        if (_multiLineBuffer is not null)
        {
            _multiLineBuffer.AsSpan().CopyTo(grown);
        }

        _multiLineBuffer = grown;
    }

    /// <summary>
    /// Counts all quote characters in the span. Doubled quotes (<c>""</c>) count as
    /// two individual quotes, which preserves parity for detecting whether a quoted
    /// field is still open.
    /// </summary>
    private static int CountQuotes(ReadOnlySpan<char> span)
    {
        int count = 0;

        for (int index = 0; index < span.Length; index++)
        {
            if (span[index] == '"')
            {
                count++;
            }
        }

        return count;
    }
}
