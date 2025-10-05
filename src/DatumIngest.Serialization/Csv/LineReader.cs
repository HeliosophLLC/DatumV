using System.Text;

namespace DatumIngest.Serialization.Csv;

/// <summary>
/// Reads CSV lines from a byte stream into a reusable character buffer, yielding
/// <see cref="ReadOnlySpan{T}"/> slices without per-line string allocation.
/// Handles UTF-8 decoding, <c>\r\n</c> and <c>\n</c> line endings, and automatic
/// buffer growth for lines exceeding the initial capacity.
/// </summary>
internal sealed class LineReader : IDisposable
{
    private readonly Stream _stream;
    private readonly byte[] _byteBuffer;
    private readonly Decoder _decoder;
    private char[] _charBuffer;
    private int _charCount;
    private int _charPosition;
    private bool _endOfStream;
    private char[]? _multiLineBuffer;

    public LineReader(Stream stream, int bufferSize = 65536)
    {
        _stream = stream;
        _byteBuffer = new byte[bufferSize];
        _charBuffer = new char[bufferSize];
        _decoder = Encoding.UTF8.GetDecoder();
    }

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
                    lineLength--;

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
                        line = line[..^1];
                    _charPosition = _charCount;
                    return true;
                }

                line = default;
                return false;
            }

            CompactAndFill();
        }
    }

    public bool TryReadLogicalLine(out ReadOnlySpan<char> line)
    {
        if (!TryReadLine(out ReadOnlySpan<char> physicalLine))
        {
            line = default;
            return false;
        }

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

        int multiLineLength = physicalLine.Length;
        EnsureMultiLineCapacity(multiLineLength + 1024);
        physicalLine.CopyTo(_multiLineBuffer);

        while (quoteCount % 2 != 0)
        {
            if (!TryReadLine(out ReadOnlySpan<char> continuation))
                break;

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

    public string? ReadLineAsString()
    {
        if (TryReadLine(out ReadOnlySpan<char> line))
            return line.ToString();
        return null;
    }

    public void Dispose() { }

    private void CompactAndFill()
    {
        int remaining = _charCount - _charPosition;

        if (remaining > 0 && _charPosition > 0)
            _charBuffer.AsSpan(_charPosition, remaining).CopyTo(_charBuffer);

        _charPosition = 0;
        _charCount = remaining;

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

    private void EnsureMultiLineCapacity(int required)
    {
        if (_multiLineBuffer is not null && _multiLineBuffer.Length >= required)
            return;

        int newSize = _multiLineBuffer is null
            ? Math.Max(required, 4096)
            : Math.Max(required, _multiLineBuffer.Length * 2);

        char[] grown = new char[newSize];
        _multiLineBuffer?.AsSpan().CopyTo(grown);
        _multiLineBuffer = grown;
    }

    private static int CountQuotes(ReadOnlySpan<char> span)
    {
        int count = 0;
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] == '"') count++;
        }
        return count;
    }
}
