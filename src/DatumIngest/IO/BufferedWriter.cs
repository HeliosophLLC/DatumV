using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace DatumIngest.IO;

/// <summary>
/// High-throughput binary writer that batches small writes into a rented buffer
/// and flushes to the underlying stream in large blocks. Replaces
/// <see cref="BinaryWriter"/> on hot serialization paths where millions of
/// small <c>Write(int)</c> / <c>Write(long)</c> calls create measurable
/// virtual dispatch overhead.
/// </summary>
/// <remarks>
/// All integer types are written in little-endian byte order, matching both the
/// <c>.datum-index</c> format and <see cref="BinaryWriter"/> behaviour on x86.
/// Strings are length-prefixed using the same 7-bit encoded integer format as
/// <see cref="BinaryWriter"/> for binary compatibility.
/// </remarks>
internal sealed class BufferedWriter : IDisposable
{
    private readonly Stream _output;
    private readonly byte[] _buffer;
    private int _position;

    /// <summary>
    /// Creates a buffered writer that writes to <paramref name="output"/>.
    /// </summary>
    /// <param name="output">Target stream. Must be writable.</param>
    /// <param name="bufferSize">Flush threshold in bytes. Defaults to 64 KiB.</param>
    public BufferedWriter(Stream output, int bufferSize = 65_536)
    {
        _output = output;
        _buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        _position = 0;
    }

    /// <summary>Current position in the underlying stream, including buffered bytes.</summary>
    public long Position => _output.Position + _position;

    /// <summary>The underlying stream (after flushing, its position is authoritative).</summary>
    public Stream BaseStream => _output;

    /// <summary>Writes a single byte.</summary>
    public void Write(byte value)
    {
        EnsureCapacity(1);
        _buffer[_position++] = value;
    }

    /// <summary>Writes a boolean as a single byte (0 or 1).</summary>
    public void Write(bool value)
    {
        EnsureCapacity(1);
        _buffer[_position++] = value ? (byte)1 : (byte)0;
    }

    /// <summary>Writes a signed byte.</summary>
    public void Write(sbyte value)
    {
        EnsureCapacity(1);
        _buffer[_position++] = unchecked((byte)value);
    }

    /// <summary>Writes a 16-bit signed integer in little-endian order.</summary>
    public void Write(short value)
    {
        EnsureCapacity(2);
        BinaryPrimitives.WriteInt16LittleEndian(_buffer.AsSpan(_position), value);
        _position += 2;
    }

    /// <summary>Writes a 16-bit unsigned integer in little-endian order.</summary>
    public void Write(ushort value)
    {
        EnsureCapacity(2);
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_position), value);
        _position += 2;
    }

    /// <summary>Writes a 32-bit signed integer in little-endian order.</summary>
    public void Write(int value)
    {
        EnsureCapacity(4);
        BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_position), value);
        _position += 4;
    }

    /// <summary>Writes a 32-bit unsigned integer in little-endian order.</summary>
    public void Write(uint value)
    {
        EnsureCapacity(4);
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_position), value);
        _position += 4;
    }

    /// <summary>Writes a 64-bit signed integer in little-endian order.</summary>
    public void Write(long value)
    {
        EnsureCapacity(8);
        BinaryPrimitives.WriteInt64LittleEndian(_buffer.AsSpan(_position), value);
        _position += 8;
    }

    /// <summary>Writes a 64-bit unsigned integer in little-endian order.</summary>
    public void Write(ulong value)
    {
        EnsureCapacity(8);
        BinaryPrimitives.WriteUInt64LittleEndian(_buffer.AsSpan(_position), value);
        _position += 8;
    }

    /// <summary>Writes a 32-bit floating-point value in little-endian order.</summary>
    public void Write(float value)
    {
        EnsureCapacity(4);
        BinaryPrimitives.WriteSingleLittleEndian(_buffer.AsSpan(_position), value);
        _position += 4;
    }

    /// <summary>Writes a 64-bit floating-point value in little-endian order.</summary>
    public void Write(double value)
    {
        EnsureCapacity(8);
        BinaryPrimitives.WriteDoubleLittleEndian(_buffer.AsSpan(_position), value);
        _position += 8;
    }

    /// <summary>
    /// Writes a length-prefixed UTF-8 string using the same 7-bit encoded integer
    /// length prefix as <see cref="BinaryWriter"/>, ensuring binary compatibility
    /// with existing <c>.datum-index</c> readers.
    /// </summary>
    public void Write(string value)
    {
        int maxByteCount = Encoding.UTF8.GetMaxByteCount(value.Length);

        // Fast path: if the string + length prefix + 5-byte max varint fits in the
        // remaining buffer, encode directly into the buffer.
        if (_position + maxByteCount + 5 <= _buffer.Length)
        {
            // Encode first to know exact byte count, then write the length prefix before it.
            int byteCount = Encoding.UTF8.GetBytes(value, _buffer.AsSpan(_position + 5));
            int prefixBytes = Write7BitEncodedInt(byteCount, _buffer.AsSpan(_position));

            // Shift the encoded string bytes down if the prefix was shorter than 5 bytes.
            if (prefixBytes < 5)
            {
                Buffer.BlockCopy(_buffer, _position + 5, _buffer, _position + prefixBytes, byteCount);
            }

            _position += prefixBytes + byteCount;
        }
        else
        {
            // Large string — encode to a temp array and write in parts.
            byte[] encoded = Encoding.UTF8.GetBytes(value);
            Write7BitEncodedIntToBuffer(encoded.Length);
            Write(encoded);
        }
    }

    /// <summary>Writes a raw byte array.</summary>
    public void Write(byte[] data)
    {
        Write(data.AsSpan());
    }

    /// <summary>Writes a raw byte span.</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        // If data fits in remaining buffer, copy directly.
        int remaining = _buffer.Length - _position;
        if (data.Length <= remaining)
        {
            data.CopyTo(_buffer.AsSpan(_position));
            _position += data.Length;
            return;
        }

        // Flush current buffer, then write large data directly to stream.
        Flush();
        _output.Write(data);
    }

    /// <summary>Flushes any buffered bytes to the underlying stream.</summary>
    public void Flush()
    {
        if (_position > 0)
        {
            _output.Write(_buffer, 0, _position);
            _position = 0;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Flush();
        ArrayPool<byte>.Shared.Return(_buffer);
    }

    private void EnsureCapacity(int required)
    {
        if (_position + required > _buffer.Length)
        {
            Flush();
        }
    }

    /// <summary>
    /// Writes a 7-bit encoded integer into the span and returns the number of bytes written.
    /// </summary>
    private static int Write7BitEncodedInt(int value, Span<byte> destination)
    {
        uint unsigned = (uint)value;
        int index = 0;
        while (unsigned > 0x7Fu)
        {
            destination[index++] = (byte)(unsigned | ~0x7Fu);
            unsigned >>= 7;
        }
        destination[index++] = (byte)unsigned;
        return index;
    }

    /// <summary>
    /// Writes a 7-bit encoded integer into the internal buffer.
    /// </summary>
    private void Write7BitEncodedIntToBuffer(int value)
    {
        EnsureCapacity(5);
        int bytes = Write7BitEncodedInt(value, _buffer.AsSpan(_position));
        _position += bytes;
    }
}
