using System.Buffers.Binary;
using System.Text;
using DatumIngest.Execution;
using DatumIngest.Model;

namespace DatumIngest.Indexing;

/// <summary>
/// Encodes a tuple of <see cref="DataValue"/>s into a single byte array
/// suitable for use as the sort key in a bytes-keyed B+Tree. The encoding
/// is memcmp-orderable: <c>SequenceCompareTo</c> on two encoded tuples
/// returns the same sign as the lexicographic compare of the original
/// tuples component-by-component.
/// </summary>
/// <remarks>
/// <para>Per-kind encoding:</para>
/// <list type="bullet">
///   <item><c>Bool</c> → 1 byte (0/1)</item>
///   <item>Signed integers (Int8–Int128) → big-endian + sign-bit flip
///         (the high bit XORed) so the unsigned compare gives the signed order</item>
///   <item>Unsigned integers (UInt8–UInt128) → big-endian</item>
///   <item><c>Float16/32/64</c> → IEEE 754 bits, flip the sign bit for
///         non-negative values, flip all bits for negative values
///         (standard memcmp-orderable float trick)</item>
///   <item><c>Date</c> → DayNumber as Int32</item>
///   <item><c>Time</c> → Ticks as Int64 (non-negative; sign-flip preserves order)</item>
///   <item><c>DateTime</c> → UtcTicks as Int64, then OffsetMinutes as Int16
///         (UTC instant dominates ordering; offset is a tiebreaker)</item>
///   <item><c>Duration</c> → TimeSpan.Ticks as Int64</item>
///   <item><c>Uuid</c> → raw 16 bytes from <see cref="Guid.ToByteArray()"/>
///         (Microsoft mixed-endian; provides a consistent total order)</item>
///   <item><c>String</c> → UTF-8 with <c>\x00 → \x00\xFF</c> escape +
///         <c>\x00\x00</c> terminator (preserves lexicographic order)</item>
///   <item><c>UInt8[]</c> (byte array) → same escape + terminator pattern
///         as String</item>
/// </list>
/// <para>
/// <see cref="DataKind.Decimal"/> and the geometric kinds
/// (<c>Point2D</c>/<c>Point3D</c>) are deferred — Decimal's memcmp encoding
/// is non-trivial, and points have no natural total order on R²/R³.
/// </para>
/// <para>
/// <c>NULL</c> components are rejected. The B+Tree's key space must be
/// totally ordered, and NULL has no defined position in that ordering —
/// callers must filter NULL PK components before reaching the encoder
/// (the catalog already enforces NOT NULL on every PK column).
/// </para>
/// </remarks>
internal static class CompositeKeyEncoder
{
    /// <summary>
    /// Encodes a tuple of values into a single byte array. Component order
    /// is significant — encoded(a, b) and encoded(b, a) produce different
    /// bytes.
    /// </summary>
    /// <param name="values">The values to encode, in column order.</param>
    /// <param name="store">
    /// Backing store for non-inline payloads (long strings, byte arrays).
    /// May be <see langword="null"/> when every value is inline.
    /// </param>
    public static byte[] Encode(IReadOnlyList<DataValue> values, IValueStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        using MemoryStream stream = new();
        for (int i = 0; i < values.Count; i++)
        {
            EncodeOne(values[i], store, stream);
        }
        return stream.ToArray();
    }

    /// <summary>
    /// Convenience overload for the single-column case (used by both the
    /// degenerate "composite of one" path and tests).
    /// </summary>
    public static byte[] EncodeSingle(DataValue value, IValueStore? store = null)
    {
        using MemoryStream stream = new();
        EncodeOne(value, store, stream);
        return stream.ToArray();
    }

    private static void EncodeOne(DataValue value, IValueStore? store, MemoryStream stream)
    {
        if (value.IsNull)
        {
            throw new InvalidOperationException(
                "CompositeKeyEncoder: NULL components are not supported in encoded keys. " +
                "PRIMARY KEY columns are NOT NULL by definition; filter NULL values before encoding.");
        }

        // Byte arrays carry Kind=UInt8 + IsArray; handle before the scalar
        // UInt8 case so the array branch wins.
        if (value.IsByteArrayKind)
        {
            byte[] bytes = value.AsUInt8Array(store!);
            WriteEscapedBytes(stream, bytes);
            return;
        }

        switch (value.Kind)
        {
            case DataKind.Boolean:
                stream.WriteByte(value.AsBoolean() ? (byte)1 : (byte)0);
                break;

            case DataKind.Int8:    WriteInt8(stream, value.AsInt8()); break;
            case DataKind.Int16:   WriteInt16(stream, value.AsInt16()); break;
            case DataKind.Int32:   WriteInt32(stream, value.AsInt32()); break;
            case DataKind.Int64:   WriteInt64(stream, value.AsInt64()); break;
            case DataKind.Int128:  WriteInt128(stream, value.AsInt128()); break;

            case DataKind.UInt8:   stream.WriteByte(value.AsUInt8()); break;
            case DataKind.UInt16:  WriteUInt16(stream, value.AsUInt16()); break;
            case DataKind.UInt32:  WriteUInt32(stream, value.AsUInt32()); break;
            case DataKind.UInt64:  WriteUInt64(stream, value.AsUInt64()); break;
            case DataKind.UInt128: WriteUInt128(stream, value.AsUInt128()); break;

            case DataKind.Float16: WriteFloat16(stream, value.AsFloat16()); break;
            case DataKind.Float32: WriteFloat32(stream, value.AsFloat32()); break;
            case DataKind.Float64: WriteFloat64(stream, value.AsFloat64()); break;

            case DataKind.Date:     WriteInt32(stream, value.AsDate().DayNumber); break;
            case DataKind.Time:     WriteInt64(stream, value.AsTime().Ticks); break;
            case DataKind.Duration: WriteInt64(stream, value.AsDuration().Ticks); break;

            case DataKind.TimestampTz:
                WriteInt64(stream, value.AsTimestampTz().UtcTicks);
                break;
            case DataKind.Timestamp:
                WriteInt64(stream, value.AsTimestamp().Ticks);
                break;

            case DataKind.Uuid:
                stream.Write(value.AsUuid().ToByteArray());
                break;

            case DataKind.String:
                string text = value.AsString(store!);
                byte[] textBytes = Encoding.UTF8.GetBytes(text);
                WriteEscapedBytes(stream, textBytes);
                break;

            default:
                throw new NotSupportedException(
                    $"CompositeKeyEncoder does not support DataKind.{value.Kind} in PK encoding. " +
                    "v1 supports: Bool, Int8–Int128, UInt8–UInt128, Float16/32/64, Date, Time, " +
                    "DateTime, Duration, Uuid, String, UInt8[]. " +
                    "Decimal and Point2D/Point3D are deferred.");
        }
    }

    // ───── Signed integers: big-endian + sign-bit flip ─────
    // (The high bit XORed so that unsigned compare reproduces signed order.)

    private static void WriteInt8(MemoryStream stream, sbyte v) =>
        stream.WriteByte((byte)(v ^ -128));  // -128 = 0x80 sign-bit mask

    private static void WriteInt16(MemoryStream stream, short v)
    {
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buf, (ushort)(v ^ unchecked((short)0x8000)));
        stream.Write(buf);
    }

    private static void WriteInt32(MemoryStream stream, int v)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)(v ^ unchecked((int)0x8000_0000)));
        stream.Write(buf);
    }

    private static void WriteInt64(MemoryStream stream, long v)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buf, (ulong)(v ^ unchecked((long)0x8000_0000_0000_0000)));
        stream.Write(buf);
    }

    private static void WriteInt128(MemoryStream stream, Int128 v)
    {
        UInt128 unsigned = (UInt128)v;
        unsigned ^= (UInt128)1 << 127;  // flip sign bit
        Span<byte> buf = stackalloc byte[16];
        BinaryPrimitives.WriteUInt128BigEndian(buf, unsigned);
        stream.Write(buf);
    }

    // ───── Unsigned integers: big-endian ─────

    private static void WriteUInt16(MemoryStream stream, ushort v)
    {
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buf, v);
        stream.Write(buf);
    }

    private static void WriteUInt32(MemoryStream stream, uint v)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, v);
        stream.Write(buf);
    }

    private static void WriteUInt64(MemoryStream stream, ulong v)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buf, v);
        stream.Write(buf);
    }

    private static void WriteUInt128(MemoryStream stream, UInt128 v)
    {
        Span<byte> buf = stackalloc byte[16];
        BinaryPrimitives.WriteUInt128BigEndian(buf, v);
        stream.Write(buf);
    }

    // ───── IEEE floats: sign-flip positives, all-bits-flip negatives ─────

    private static void WriteFloat16(MemoryStream stream, Half v)
    {
        ushort bits = BitConverter.HalfToUInt16Bits(v);
        if ((bits & 0x8000) != 0) bits = (ushort)~bits;
        else bits ^= 0x8000;
        WriteUInt16(stream, bits);
    }

    private static void WriteFloat32(MemoryStream stream, float v)
    {
        uint bits = BitConverter.SingleToUInt32Bits(v);
        if ((bits & 0x8000_0000) != 0) bits = ~bits;
        else bits ^= 0x8000_0000;
        WriteUInt32(stream, bits);
    }

    private static void WriteFloat64(MemoryStream stream, double v)
    {
        ulong bits = BitConverter.DoubleToUInt64Bits(v);
        if ((bits & 0x8000_0000_0000_0000) != 0) bits = ~bits;
        else bits ^= 0x8000_0000_0000_0000;
        WriteUInt64(stream, bits);
    }

    // ───── Variable-length bytes/strings with escape ─────
    //
    // The escape pattern is `\x00 → \x00\xFF` with `\x00\x00` as terminator.
    // This preserves lexicographic order because:
    //   - any byte ≥ 0x01 in the payload sorts higher than `\x00\x00` (terminator),
    //   - escaped `\x00\xFF` sorts higher than any `\x00\x00` terminator (since
    //     0xFF > 0x00 in the second byte), so a payload with embedded nulls
    //     correctly sorts higher than a shorter prefix.
    // Length-prefixed encoding would not preserve order ([5] sorts after
    // [10, 0, 0, 0, 0] under length-then-bytes compare).

    private static void WriteEscapedBytes(MemoryStream stream, ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            stream.WriteByte(b);
            if (b == 0) stream.WriteByte(0xFF);
        }
        stream.WriteByte(0);
        stream.WriteByte(0);
    }
}
