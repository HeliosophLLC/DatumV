using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using DatumIngest.Model;

namespace DatumIngest.Indexing.Sorted;

/// <summary>
/// Encodes and decodes <see cref="DataValue"/> keys in a fixed-width, sort-preserving binary format
/// for memory-mapped sorted indexes. For numeric and temporal kinds, the encoded byte sequence
/// preserves sort order under <c>SequenceCompareTo</c>: sign-flip and
/// big-endian for signed integers, IEEE-to-sortable for floats, big-endian for unsigned integers.
/// String keys encode a string-table reference (offset + length) and require the caller to
/// perform comparison through the string table.
/// </summary>
internal static class SortedIndexKeyEncoder
{
    /// <summary>
    /// Returns the fixed-width key size in bytes for the given data kind.
    /// </summary>
    /// <exception cref="NotSupportedException">The kind is not supported for sorted index keys.</exception>
    public static int GetKeyWidth(DataKind kind) => kind switch
    {
        DataKind.Boolean => 1,
        DataKind.UInt8 => 1,
        DataKind.Int8 => 1,
        DataKind.Int16 => 2,
        DataKind.UInt16 => 2,
        DataKind.Int32 => 4,
        DataKind.UInt32 => 4,
        DataKind.Float32 => 4,
        DataKind.Date => 4,
        DataKind.Int64 => 8,
        DataKind.UInt64 => 8,
        DataKind.Float64 => 8,
        DataKind.DateTime => 8,
        DataKind.Time => 8,
        DataKind.Duration => 8,
        DataKind.String => 8,
        DataKind.Uuid => 16,
        _ => throw new NotSupportedException($"DataKind {kind} is not supported for sorted index keys."),
    };

    /// <summary>
    /// Encodes a non-string <see cref="DataValue"/> key into the destination span using
    /// sort-preserving binary encoding. The destination must be at least <see cref="GetKeyWidth"/> bytes.
    /// For <see cref="DataKind.String"/> keys, use <see cref="EncodeStringReference"/> instead.
    /// </summary>
    /// <param name="key">The value to encode. Must not be null.</param>
    /// <param name="destination">Span to write the encoded key into.</param>
    /// <exception cref="InvalidOperationException">The key is a string (use <see cref="EncodeStringReference"/>).</exception>
    /// <exception cref="NotSupportedException">The key kind is not supported for sorted index keys.</exception>
    public static void Encode(DataValue key, Span<byte> destination)
    {
        switch (key.Kind)
        {
            case DataKind.Boolean:
                destination[0] = key.AsBoolean() ? (byte)1 : (byte)0;
                break;

            case DataKind.UInt8:
                destination[0] = key.AsUInt8();
                break;

            case DataKind.Int8:
                destination[0] = (byte)(key.AsInt8() ^ 0x80);
                break;

            case DataKind.UInt16:
                BinaryPrimitives.WriteUInt16BigEndian(destination, key.AsUInt16());
                break;

            case DataKind.Int16:
                BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)(key.AsInt16() ^ unchecked((short)0x8000)));
                break;

            case DataKind.UInt32:
                BinaryPrimitives.WriteUInt32BigEndian(destination, key.AsUInt32());
                break;

            case DataKind.Int32:
                BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)(key.AsInt32() ^ unchecked((int)0x80000000)));
                break;

            case DataKind.Float32:
                EncodeFloat32(key.AsFloat32(), destination);
                break;

            case DataKind.Date:
                BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)(key.AsDate().DayNumber ^ unchecked((int)0x80000000)));
                break;

            case DataKind.UInt64:
                BinaryPrimitives.WriteUInt64BigEndian(destination, key.AsUInt64());
                break;

            case DataKind.Int64:
                BinaryPrimitives.WriteUInt64BigEndian(destination, (ulong)(key.AsInt64() ^ unchecked((long)0x8000000000000000L)));
                break;

            case DataKind.Float64:
                EncodeFloat64(key.AsFloat64(), destination);
                break;

            case DataKind.DateTime:
            {
                DateTimeOffset dateTime = key.AsDateTime();
                long utcTicks = dateTime.UtcTicks;
                BinaryPrimitives.WriteUInt64BigEndian(destination, (ulong)(utcTicks ^ unchecked((long)0x8000000000000000L)));
                break;
            }

            case DataKind.Time:
            {
                long ticks = key.AsTime().Ticks;
                BinaryPrimitives.WriteUInt64BigEndian(destination, (ulong)(ticks ^ unchecked((long)0x8000000000000000L)));
                break;
            }

            case DataKind.Duration:
            {
                long ticks = key.AsDuration().Ticks;
                BinaryPrimitives.WriteUInt64BigEndian(destination, (ulong)(ticks ^ unchecked((long)0x8000000000000000L)));
                break;
            }

            case DataKind.Uuid:
                key.AsUuid().TryWriteBytes(destination);
                break;

            case DataKind.String:
                throw new InvalidOperationException("Use EncodeStringReference for string keys.");

            default:
                throw new NotSupportedException($"DataKind {key.Kind} is not supported for sorted index keys.");
        }
    }

    /// <summary>
    /// Encodes a string-table reference as a fixed-width 8-byte key.
    /// The encoded form is NOT sort-preserving; binary search on string columns must
    /// dereference the string table for comparison.
    /// </summary>
    /// <param name="offset">Byte offset into the string table.</param>
    /// <param name="length">Byte length of the UTF-8 string in the string table.</param>
    /// <param name="destination">Span to write the 8-byte reference into.</param>
    public static void EncodeStringReference(int offset, int length, Span<byte> destination)
    {
        BinaryPrimitives.WriteInt32BigEndian(destination, offset);
        BinaryPrimitives.WriteInt32BigEndian(destination[4..], length);
    }

    /// <summary>
    /// Decodes a string-table reference from an 8-byte key.
    /// </summary>
    /// <param name="source">The 8-byte encoded reference.</param>
    /// <returns>The byte offset and byte length into the string table.</returns>
    public static (int Offset, int Length) DecodeStringReference(ReadOnlySpan<byte> source)
    {
        int offset = BinaryPrimitives.ReadInt32BigEndian(source);
        int length = BinaryPrimitives.ReadInt32BigEndian(source[4..]);
        return (offset, length);
    }

    /// <summary>
    /// Decodes a fixed-width key from the source span back into a <see cref="DataValue"/>.
    /// For <see cref="DataKind.String"/> keys, use <see cref="DecodeStringReference"/> and
    /// resolve the string from the string table before calling <see cref="DataValue.FromString(string)"/>.
    /// </summary>
    /// <param name="kind">The data kind to decode as.</param>
    /// <param name="source">The encoded key bytes.</param>
    /// <returns>The decoded value.</returns>
    /// <exception cref="InvalidOperationException">The kind is a string (use <see cref="DecodeStringReference"/>).</exception>
    /// <exception cref="NotSupportedException">The kind is not supported for sorted index keys.</exception>
    public static DataValue Decode(DataKind kind, ReadOnlySpan<byte> source)
    {
        return kind switch
        {
            DataKind.Boolean => DataValue.FromBoolean(source[0] != 0),
            DataKind.UInt8 => DataValue.FromUInt8(source[0]),
            DataKind.Int8 => DataValue.FromInt8((sbyte)(source[0] ^ 0x80)),
            DataKind.UInt16 => DataValue.FromUInt16(BinaryPrimitives.ReadUInt16BigEndian(source)),
            DataKind.Int16 => DataValue.FromInt16((short)(BinaryPrimitives.ReadUInt16BigEndian(source) ^ 0x8000)),
            DataKind.UInt32 => DataValue.FromUInt32(BinaryPrimitives.ReadUInt32BigEndian(source)),
            DataKind.Int32 => DataValue.FromInt32((int)(BinaryPrimitives.ReadUInt32BigEndian(source) ^ 0x80000000u)),
            DataKind.Float32 => DataValue.FromFloat32(DecodeFloat32(source)),
            DataKind.Date => DataValue.FromDate(DateOnly.FromDayNumber((int)(BinaryPrimitives.ReadUInt32BigEndian(source) ^ 0x80000000u))),
            DataKind.UInt64 => DataValue.FromUInt64(BinaryPrimitives.ReadUInt64BigEndian(source)),
            DataKind.Int64 => DataValue.FromInt64((long)(BinaryPrimitives.ReadUInt64BigEndian(source) ^ 0x8000000000000000uL)),
            DataKind.Float64 => DataValue.FromFloat64(DecodeFloat64(source)),
            DataKind.DateTime => DecodeDateTime(source),
            DataKind.Time => DataValue.FromTime(new TimeOnly((long)(BinaryPrimitives.ReadUInt64BigEndian(source) ^ 0x8000000000000000uL))),
            DataKind.Duration => DataValue.FromDuration(TimeSpan.FromTicks((long)(BinaryPrimitives.ReadUInt64BigEndian(source) ^ 0x8000000000000000uL))),
            DataKind.Uuid => DataValue.FromUuid(new Guid(source[..16])),
            DataKind.String => throw new InvalidOperationException("Use DecodeStringReference for string keys."),
            _ => throw new NotSupportedException($"DataKind {kind} is not supported for sorted index keys."),
        };
    }

    /// <summary>
    /// IEEE 754 float to sort-preserving unsigned integer. Negative floats have all bits flipped;
    /// non-negative floats have only the sign bit flipped. Negative zero and NaN are canonicalized
    /// before encoding so that binary search treats -0 == +0 and all NaN patterns as identical.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EncodeFloat32(float value, Span<byte> destination)
    {
        int bits = BitConverter.SingleToInt32Bits(value);

        if (float.IsNaN(value))
            bits = BitConverter.SingleToInt32Bits(float.NaN);
        else if (bits == unchecked((int)0x80000000))
            bits = 0;

        uint encoded = (bits < 0)
            ? (uint)~bits
            : (uint)bits ^ 0x80000000u;

        BinaryPrimitives.WriteUInt32BigEndian(destination, encoded);
    }

    /// <summary>
    /// Reverses the sort-preserving float encoding back to an IEEE 754 float.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float DecodeFloat32(ReadOnlySpan<byte> source)
    {
        uint encoded = BinaryPrimitives.ReadUInt32BigEndian(source);

        int bits = ((encoded & 0x80000000u) == 0)
            ? ~(int)encoded
            : (int)(encoded ^ 0x80000000u);

        return BitConverter.Int32BitsToSingle(bits);
    }

    /// <summary>
    /// IEEE 754 double to sort-preserving unsigned long. Same canonicalization as
    /// <see cref="EncodeFloat32"/> but for 64-bit doubles.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EncodeFloat64(double value, Span<byte> destination)
    {
        long bits = BitConverter.DoubleToInt64Bits(value);

        if (double.IsNaN(value))
            bits = BitConverter.DoubleToInt64Bits(double.NaN);
        else if (bits == unchecked((long)0x8000000000000000L))
            bits = 0L;

        ulong encoded = (bits < 0)
            ? (ulong)~bits
            : (ulong)bits ^ 0x8000000000000000uL;

        BinaryPrimitives.WriteUInt64BigEndian(destination, encoded);
    }

    /// <summary>
    /// Reverses the sort-preserving double encoding back to an IEEE 754 double.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double DecodeFloat64(ReadOnlySpan<byte> source)
    {
        ulong encoded = BinaryPrimitives.ReadUInt64BigEndian(source);

        long bits = ((encoded & 0x8000000000000000uL) == 0)
            ? ~(long)encoded
            : (long)(encoded ^ 0x8000000000000000uL);

        return BitConverter.Int64BitsToDouble(bits);
    }

    /// <summary>
    /// Decodes a <see cref="DataKind.DateTime"/> key. The key stores UTC ticks only;
    /// the decoded <see cref="DateTimeOffset"/> always has a zero UTC offset.
    /// </summary>
    private static DataValue DecodeDateTime(ReadOnlySpan<byte> source)
    {
        long utcTicks = (long)(BinaryPrimitives.ReadUInt64BigEndian(source) ^ 0x8000000000000000uL);
        return DataValue.FromDateTime(new DateTimeOffset(utcTicks, TimeSpan.Zero));
    }
}
