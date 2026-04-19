using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DatumIngest.DatumFile.Sidecar;

namespace DatumIngest.Model;

public readonly partial struct DataValue
{
    /// <summary>
    /// Creates a <see cref="DataKind.String"/> value whose UTF-8 bytes live in a
    /// <c>.datum-blob</c> sidecar. The DataValue carries 64-bit absolute offset and
    /// 40-bit length; resolution requires an <see cref="IBlobSource"/>. Used by the
    /// v2 reader when decoding a sidecar-pointer slot in a String column.
    /// </summary>
    public static DataValue FromStringInSidecar(long offset, long length, byte storeId = 0) =>
        BuildSidecar(DataKind.String, offset, length, storeId);

    /// <summary>
    /// Creates a value from a text string without a store. Works only when the string's
    /// UTF-8 form fits in 16 bytes (inline path); longer strings require a store —
    /// see <see cref="FromString(string, IValueStore)"/>.
    /// </summary>
    public static DataValue FromString(string value)
    {
        Span<byte> scratch = stackalloc byte[MaxInlineUtf8Bytes];
        if (System.Text.Encoding.UTF8.TryGetBytes(value, scratch, out int written))
        {
            return FromInlineUtf8(DataKind.String, scratch[..written], CountUtf8CodePoints(scratch[..written]));
        }
        throw new InvalidOperationException(
            $"FromString(value) without a store only supports strings whose UTF-8 form fits in {MaxInlineUtf8Bytes} bytes. " +
            "Use FromString(value, store) for longer strings.");
    }

    /// <summary>
    /// Creates a value from a text string using an explicit <see cref="IValueStore"/>.
    /// Strings whose UTF-8 form fits in 16 bytes are stored inline in the struct;
    /// longer strings are written to <paramref name="store"/>.
    /// </summary>
    /// <param name="value">The string to store.</param>
    /// <param name="store">The store to use for storage and later retrieval.</param>
    public static DataValue FromString(string value, IValueStore store)
    {
        Span<byte> scratch = stackalloc byte[MaxInlineUtf8Bytes];
        if (System.Text.Encoding.UTF8.TryGetBytes(value, scratch, out int written))
        {
            return FromInlineUtf8(DataKind.String, scratch[..written], CountUtf8CodePoints(scratch[..written]));
        }

        var (p0, p1) = store.StoreString(value);
        ulong hash = HashString(value.AsSpan());
        int codePoints = CountCharSpanCodePoints(value.AsSpan());
        ushort cc = codePoints <= ushort.MaxValue ? (ushort)codePoints : ushort.MaxValue;
        return new(DataKind.String, flags: DataValueFlags.InArena, offset: p0.Value, length: p1.Value, hash: hash, charCount: cc);
    }

    /// <summary>Creates a string value from a char span without allocating a managed string.</summary>
    public static DataValue FromCharSpan(ReadOnlySpan<char> chars, IValueStore store)
    {
        Span<byte> scratch = stackalloc byte[MaxInlineUtf8Bytes];
        if (System.Text.Encoding.UTF8.TryGetBytes(chars, scratch, out int written))
        {
            return FromInlineUtf8(DataKind.String, scratch[..written], CountUtf8CodePoints(scratch[..written]));
        }

        var (p0, p1) = store.StoreChars(chars);
        ulong hash = HashString(chars);
        int codePoints = CountCharSpanCodePoints(chars);
        ushort cc = codePoints <= ushort.MaxValue ? (ushort)codePoints : ushort.MaxValue;
        return new(DataKind.String, flags: DataValueFlags.InArena, offset: p0.Value, length: p1.Value, hash: hash, charCount: cc);
    }

    /// <summary>
    /// Creates a string value from raw UTF-8 bytes without allocating a managed
    /// string. Code-point count is computed from the bytes via
    /// <see cref="CountUtf8CodePoints"/> — single byte walk, no decode — so the
    /// stamped <c>_charCount</c> always matches PG <c>length(text)</c> semantics
    /// (surrogate-pair characters count as 1).
    /// </summary>
    public static DataValue FromUtf8Span(ReadOnlySpan<byte> utf8, IValueStore store)
    {
        int codePoints = CountUtf8CodePoints(utf8);
        if (utf8.Length <= MaxInlineUtf8Bytes)
        {
            return FromInlineUtf8(DataKind.String, utf8, codePoints);
        }

        var (p0, p1) = store.StoreUtf8(utf8);
        ulong hash = HashUtf8(utf8);
        ushort cc = codePoints <= ushort.MaxValue ? (ushort)codePoints : ushort.MaxValue;
        return new(DataKind.String, flags: DataValueFlags.InArena, offset: p0.Value, length: p1.Value, hash: hash, charCount: cc);
    }

    /// <summary>
    /// Creates an inline <see cref="DataKind.String"/> whose UTF-8 bytes live directly
    /// in the struct's payload region (<c>_p0</c>-<c>_p6</c> low 3 bytes, struct bytes 4-30).
    /// Requires <paramref name="utf8Bytes"/>.Length &lt;= <see cref="MaxInlineUtf8Bytes"/>
    /// and <paramref name="charCount"/> &lt;= <see cref="MaxInlineUtf8Bytes"/>. Unused
    /// payload bytes are zeroed. Byte length and char count are packed into
    /// <c>_charCount</c> (low byte = bytes, high byte = chars).
    /// </summary>
    private static DataValue FromInlineUtf8(DataKind kind, ReadOnlySpan<byte> utf8Bytes, int charCount)
    {
        // Stage into a 28-byte buffer (7 ints' worth) so the byte-to-int cast lines up
        // with the struct's 7 payload int slots. _p6's high byte (struct[31]) ends up zero
        // — we never write more than 27 bytes of UTF-8.
        Span<byte> padded = stackalloc byte[28];
        padded.Clear();
        utf8Bytes.CopyTo(padded);

        Span<int> asInts = MemoryMarshal.Cast<byte, int>(padded);

        ushort packed = (ushort)((utf8Bytes.Length & 0xFF) | ((charCount & 0xFF) << 8));

        DataValue result = new(
            kind,
            flags: 0,
            p0: asInts[0],
            p1: asInts[1],
            p2: asInts[2],
            p3: asInts[3],
            charCount: packed);

        // The 4-int ctor above zeros _p4/_p5/_p6. For inline strings ≥17 bytes we need
        // the spillover into those slots. Unsafe-write the staged buffer over the entire
        // 28-byte payload region.
        ref byte payloadStart = ref Unsafe.As<int, byte>(ref Unsafe.AsRef(in result._p0));
        Span<byte> destination = MemoryMarshal.CreateSpan(ref payloadStart, 28);
        padded.CopyTo(destination);

        return result;
    }

    /// <summary>
    /// Creates an arena-backed string value from an offset and length within a
    /// <see cref="Arena"/>.  The actual bytes are not stored in this struct;
    /// callers must resolve via <see cref="AsString(Arena)"/> or the
    /// <see cref="IValueStore"/> registry.
    /// </summary>
    /// <param name="offset">Byte offset into the owning <see cref="Arena"/>.</param>
    /// <param name="length">Byte length of the UTF-8 encoded string.</param>
    public static DataValue FromStringSlice(long offset, long length) =>
        new(DataKind.String, flags: DataValueFlags.InArena, offset: offset, length: length);
}
