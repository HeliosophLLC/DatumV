using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DatumIngest.DatumFile.Sidecar;

namespace DatumIngest.Model;

/// <summary>
/// An immutable, discriminated union value that carries typed data through the query pipeline.
/// Use the static factory methods (<see cref="FromFloat32"/>, <see cref="FromArenaArray{T}"/>, etc.)
/// to construct instances and the accessor methods to retrieve typed payloads.
/// </summary>
/// <remarks>
/// <para>
/// The struct is 20 bytes with 4-byte alignment (3 values per 64-byte cache line).
/// It contains no managed reference fields, keeping <see cref="DataValue"/> arrays
/// invisible to the garbage collector.
/// </para>
/// <para>
/// Fixed-size primitives (integers, floats, dates, booleans, UUIDs) are stored inline
/// in the 16-byte payload (<c>_p0</c>–<c>_p3</c>). Reference-type payloads (strings,
/// float arrays, byte arrays, image handles, typed arrays) are stored in an
/// <see cref="IValueStore"/> and accessed via an
/// integer index in <c>_referenceIndex</c>. Arena-backed strings use offset/length
/// in the inline payload. Always pass an <see cref="IValueStore"/> to factory and accessor methods.
/// </para>
/// <para>
/// <c>default(DataValue)</c> has <see cref="DataKind.Unknown"/> (= 0) and is not null.
/// It represents an uninitialized or untyped value. Always use factory methods or
/// <see cref="Null"/> to construct intentional values.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = 20)]
public readonly struct DataValue : IEquatable<DataValue>
{
    // ───────────────────────── Flag constants ─────────────────────────

    /// <summary>
    /// Bitfield describing where a <see cref="DataValue"/>'s payload lives and how it
    /// should be resolved. Each flag is mutually exclusive with the other storage flags
    /// (a value is in arena, in sidecar, or inline; not multiple). <see cref="None"/>
    /// = inline scalar / inline string. <see cref="IsNull"/> overrides everything.
    /// </summary>
    [Flags]
    private enum DataValueFlags : byte
    {
        /// <summary>Plain inline value: payload self-contained in <c>_p0</c>-<c>_p3</c>.</summary>
        None = 0,

        /// <summary>Typed null. Other bits and payload are ignored.</summary>
        IsNull = 0x01,

        /// <summary>
        /// Payload lives in an external <see cref="IValueStore"/> (typically an
        /// <see cref="Arena"/>) rather than inline. Set for reference-type payloads
        /// (vectors, matrices, arrays, images, …) and for strings / JSON whose UTF-8
        /// form exceeds 16 bytes.
        /// </summary>
        InArena = 0x02,

        /// <summary>
        /// Payload lives in a <c>.datum-blob</c> sidecar addressed by a 64-bit absolute
        /// offset (<c>_p0</c>+<c>_p1</c>) and a 40-bit length (<c>_p2</c> + low byte of
        /// <c>_p3</c>). The high 24 bits of <c>_p3</c> are reserved. Mutually exclusive
        /// with <see cref="InArena"/>; resolution requires an <see cref="IBlobSource"/>
        /// (looked up via the <c>storeId</c> in the low byte of <c>_charCount</c>).
        /// </summary>
        InSidecar = 0x04,

        /// <summary>
        /// This value is a typed array of <see cref="DataValue.Kind"/> elements rather
        /// than a scalar. Storage flag (<see cref="InArena"/> / <see cref="InSidecar"/> /
        /// inline) tells where the bytes live; this flag tells how to interpret them.
        /// New typed-array kinds (UInt8[], Int32[], Float64[], Date[], …) all come into
        /// existence via <c>Kind + IsArray</c>. The legacy heterogeneous-element
        /// <see cref="DataKind.Array"/> kind predates this flag and doesn't set it;
        /// <see cref="DataValue.IsArray"/> reports <c>true</c> for it via a kind-based
        /// fallback so callers don't need to know the migration history.
        /// </summary>
        IsArray = 0x08,

        /// <summary>
        /// Array payload is packed inline into <c>_p0</c>-<c>_p3</c> rather than living
        /// in an external store. Combined with <see cref="IsArray"/>: total payload size
        /// (count × sizeof(element)) must fit in 16 bytes. Element count lives in the
        /// low byte of <c>_charCount</c>. Useful for small typed arrays — Float32[4]
        /// (quaternions), Int32[4], Float64[2], UInt8[16] — that would otherwise pay
        /// for an arena allocation. Self-contained for retention / sort / hash; no
        /// arena or registry dereference needed.
        /// </summary>
        InlineArray = 0x10,

        // 0x20, 0x40, 0x80 reserved for future use.
    }

    /// <summary>Maximum representable length for a sidecar-backed value (40-bit cap, ~1 TiB).</summary>
    private const long SidecarLengthMax = (1L << 40) - 1;

    // ───────────────────────── Fields (20 bytes) ─────────────────────────

    // Header (4 bytes)
    [FieldOffset(0)]  private readonly DataKind _kind;     //  1 byte  — type discriminator
    [FieldOffset(1)]  private readonly DataValueFlags _flags; //  1 byte  — see DataValueFlags
    // ushort at offset 2 carries string/JSON sizing info, interpreted by storage mode:
    //   Non-inline (reference-store / arena-slice): full char count (0 = unknown, 65535 = overflow sentinel)
    //   Inline: low byte  = UTF-8 byte length (0-16)
    //           high byte = char count         (0-16)
    [FieldOffset(2)]  private readonly ushort _charCount;

    // Payload — inline interpretation (16 bytes)
    [FieldOffset(4)]  private readonly int _p0;            //  4 bytes — payload word 0
    [FieldOffset(8)]  private readonly int _p1;            //  4 bytes — payload word 1
    [FieldOffset(12)] private readonly int _p2;            //  4 bytes — payload word 2
    [FieldOffset(16)] private readonly int _p3;            //  4 bytes — payload word 3

    // Payload — reference interpretation (overlaps _p0/_p1)
    [FieldOffset(4)]  private readonly int _referenceIndex; // overlaps _p0
    [FieldOffset(8)]  private readonly short _meta;         // overlaps low 2 bytes of _p1

    private DataValue(DataKind kind, DataValueFlags flags, int p0, int p1 = 0, int p2 = 0, int p3 = 0, ushort charCount = 0)
    {
        Unsafe.SkipInit(out this);
        _kind = kind;
        _flags = flags;
        _charCount = charCount;
        _p0 = p0;
        _p1 = p1;
        _p2 = p2;
        _p3 = p3;
    }

    /// <summary>
    /// Constructor for reference types that need <c>_meta</c>.
    /// Sets <c>_p0</c> = referenceIndex and <c>_meta</c> at the overlapping offset.
    /// </summary>
    private DataValue(DataKind kind, DataValueFlags flags, int referenceIndex, short meta)
    {
        Unsafe.SkipInit(out this);
        _kind = kind;
        _flags = flags;
        _charCount = 0;
        _referenceIndex = referenceIndex;
        _meta = meta;
        _p2 = 0;
        _p3 = 0;
    }

    /// <summary>The type discriminator for this value.</summary>
    public DataKind Kind => _kind;

    /// <summary>Whether this value represents a typed null.</summary>
    public bool IsNull => (_flags & DataValueFlags.IsNull) != 0;

    /// <summary>
    /// Whether this value's payload is self-contained in the struct's 16-byte inline region
    /// (<c>_p0</c>-<c>_p3</c>). True for fixed-size scalars (integers, floats, dates, times,
    /// UUIDs, booleans, types) and for strings/JSON whose UTF-8 form fits in 16 bytes.
    /// False for nulls and for any value whose payload lives in an external store —
    /// either an arena (<see cref="IsArenaBacked"/>) or a sidecar (<see cref="IsInSidecar"/>).
    /// </summary>
    /// <remarks>
    /// Indexes (bloom, bitmap, sorted, B+Tree) only admit inline values — lookups against
    /// non-inline values can short-circuit to a negative result.
    /// </remarks>
    public bool IsInline => (_flags & (DataValueFlags.IsNull | DataValueFlags.InArena | DataValueFlags.InSidecar)) == 0;

    /// <summary>
    /// Whether this value's payload lives in a <c>.datum-blob</c> sidecar, addressed by
    /// a 64-bit absolute offset and a 40-bit length. Resolution requires the table
    /// provider's <see cref="IBlobSource"/>; the standard <see cref="IValueStore"/>
    /// cannot satisfy reads against sidecar-backed values because the coordinate space
    /// is 64-bit, not 32-bit.
    /// </summary>
    public bool IsInSidecar => (_flags & DataValueFlags.InSidecar) != 0;

    /// <summary>
    /// Whether this value is a typed array of <see cref="Kind"/> elements (rather than
    /// a scalar). Returns <c>true</c> when either:
    /// <list type="bullet">
    ///   <item><description>
    ///     The new <c>IsArray</c> flag is set on the value (used by typed arrays of
    ///     arbitrary element kinds: Int32[], Float64[], Date[], …)
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="Kind"/> is the legacy heterogeneous-element
    ///     <see cref="DataKind.Array"/> kind — which predates the flag and doesn't
    ///     set it. Treated as an array so callers don't need to know the migration
    ///     history.
    ///   </description></item>
    /// </list>
    /// Switch dispatch can use <c>case DataKind.UInt8 when value.IsArray:</c> to handle
    /// new-style byte arrays without relying on a separate kind enum value.
    /// </summary>
    public bool IsArray =>
        (_flags & DataValueFlags.IsArray) != 0
        || _kind == DataKind.Array;

    /// <summary>
    /// Whether this value's array payload is packed inline into <c>_p0</c>-<c>_p3</c>
    /// (no arena or sidecar reference). True only when both <see cref="DataValueFlags.IsArray"/>
    /// and <see cref="DataValueFlags.InlineArray"/> are set. Reading the elements is a
    /// direct span over the inline payload region; no store dereference required.
    /// </summary>
    public bool IsInlineArray =>
        (_flags & (DataValueFlags.IsArray | DataValueFlags.InlineArray))
            == (DataValueFlags.IsArray | DataValueFlags.InlineArray);

    /// <summary>
    /// Element count for inline arrays (0-16). Stored in the low byte of <c>_charCount</c>.
    /// Only meaningful when <see cref="IsInlineArray"/> is <c>true</c>.
    /// </summary>
    internal byte InlineArrayElementCount => (byte)(_charCount & 0xFF);

    /// <summary>
    /// Decodes the 64-bit sidecar offset packed across <c>_p0</c> and <c>_p1</c>. Only
    /// meaningful when <see cref="IsInSidecar"/> is <c>true</c>.
    /// </summary>
    internal long SidecarOffset => Unsafe.As<int, long>(ref Unsafe.AsRef(in _p0));

    /// <summary>
    /// Sidecar <c>storeId</c> packed into the low byte of <c>_charCount</c>. Identifies
    /// which <see cref="IBlobSource"/> in the per-query
    /// <see cref="DatumFile.Sidecar.SidecarRegistry"/> backs this value's bytes. Only
    /// meaningful when <see cref="IsInSidecar"/> is <c>true</c>; otherwise zero.
    /// Stamped onto values by the v2 <c>VariableSlotPageDecoderV2</c>
    /// using the storeId the table provider received from the registry at scan time.
    /// </summary>
    internal byte SidecarStoreId => (byte)(_charCount & 0xFF);

    /// <summary>
    /// Decodes the 40-bit sidecar length packed across <c>_p2</c> and the low byte of
    /// <c>_p3</c>. Only meaningful when <see cref="IsInSidecar"/> is <c>true</c>.
    /// </summary>
    internal long SidecarLength => (long)(uint)_p2 | ((long)(_p3 & 0xFF) << 32);

    /// <summary>
    /// For inline strings, the UTF-8 byte length (0-16) stored in the low byte of <c>_charCount</c>.
    /// </summary>
    private byte InlineByteLength => (byte)(_charCount & 0xFF);

    /// <summary>
    /// For inline strings, the char count (0-16) stored in the high byte of <c>_charCount</c>.
    /// </summary>
    private byte InlineCharCount => (byte)(_charCount >> 8);

    /// <summary>
    /// Returns a span over the 16 inline payload bytes, sliced to the UTF-8 byte length.
    /// Only valid when <see cref="IsInline"/> is <c>true</c>.
    /// </summary>
    private ReadOnlySpan<byte> InlineUtf8Span
    {
        get
        {
            ref byte byteRef = ref Unsafe.As<int, byte>(ref Unsafe.AsRef(in _p0));
            return MemoryMarshal.CreateReadOnlySpan(ref byteRef, InlineByteLength);
        }
    }

    // ───────────────────────── Cached common instances ─────────────────────────

    private static readonly DataValue Float32Zero = new(DataKind.Float32, flags: 0, p0: 0);
    private static readonly DataValue Float32One = new(DataKind.Float32, flags: 0, p0: BitConverter.SingleToInt32Bits(1f));
    private static readonly DataValue NullUnknown = new(DataKind.Unknown, flags: DataValueFlags.IsNull, p0: 0);
    private static readonly DataValue BooleanTrue = new(DataKind.Boolean, flags: 0, p0: 1);
    private static readonly DataValue BooleanFalse = new(DataKind.Boolean, flags: 0, p0: 0);

    // ───────────────────────── Factory methods ─────────────────────────

    /// <summary>Creates a value from a 32-bit floating-point number.</summary>
    public static DataValue FromFloat32(float value)
    {
        if (value == 0f) return Float32Zero;
        if (value == 1f) return Float32One;
        return new(DataKind.Float32, flags: 0, p0: BitConverter.SingleToInt32Bits(value));
    }

    /// <summary>Creates a value from an unsigned 8-bit integer.</summary>
    public static DataValue FromUInt8(byte value) =>
        new(DataKind.UInt8, flags: 0, p0: value);

    /// <summary>Creates a value from a signed 8-bit integer.</summary>
    public static DataValue FromInt8(sbyte value) =>
        new(DataKind.Int8, flags: 0, p0: value);

    /// <summary>Creates a value from a signed 16-bit integer.</summary>
    public static DataValue FromInt16(short value) =>
        new(DataKind.Int16, flags: 0, p0: value);

    /// <summary>Creates a value from an unsigned 16-bit integer.</summary>
    public static DataValue FromUInt16(ushort value) =>
        new(DataKind.UInt16, flags: 0, p0: value);

    /// <summary>Creates a value from a signed 32-bit integer.</summary>
    public static DataValue FromInt32(int value) =>
        new(DataKind.Int32, flags: 0, p0: value);

    /// <summary>Creates a value from an unsigned 32-bit integer.</summary>
    public static DataValue FromUInt32(uint value) =>
        new(DataKind.UInt32, flags: 0, p0: unchecked((int)value));

    /// <summary>Creates a value from a signed 64-bit integer.</summary>
    public static DataValue FromInt64(long value) =>
        new(DataKind.Int64, flags: 0, p0: (int)value, p1: (int)(value >> 32));

    /// <summary>Creates a value from an unsigned 64-bit integer.</summary>
    public static DataValue FromUInt64(ulong value) =>
        new(DataKind.UInt64, flags: 0, p0: (int)value, p1: (int)(value >> 32));

    /// <summary>Creates a value from a 64-bit double-precision floating-point number.</summary>
    public static DataValue FromFloat64(double value)
    {
        long bits = BitConverter.DoubleToInt64Bits(value);
        return new(DataKind.Float64, flags: 0, p0: (int)bits, p1: (int)(bits >> 32));
    }

    /// <summary>Creates a value from a 16-bit IEEE 754 binary16 floating-point number.</summary>
    public static DataValue FromFloat16(Half value)
    {
        ushort bits = BitConverter.HalfToUInt16Bits(value);
        return new(DataKind.Float16, flags: 0, p0: bits);
    }

    /// <summary>Creates a value from a 128-bit decimal floating-point number.</summary>
    public static DataValue FromDecimal(decimal value)
    {
        // System.Decimal is 16 bytes — fits exactly inline in DataValue's _p0–_p3.
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        return new(DataKind.Decimal, flags: 0,
            p0: bits[0], p1: bits[1], p2: bits[2], p3: bits[3]);
    }

    /// <summary>Creates a value from an unsigned 128-bit integer.</summary>
    public static DataValue FromUInt128(UInt128 value)
    {
        // UInt128 is 16 bytes — reinterpret as four int32 words and store inline.
        ref int words = ref Unsafe.As<UInt128, int>(ref value);
        return new(DataKind.UInt128, flags: 0,
            p0: words,
            p1: Unsafe.Add(ref words, 1),
            p2: Unsafe.Add(ref words, 2),
            p3: Unsafe.Add(ref words, 3));
    }

    /// <summary>Creates a value from a signed 128-bit integer.</summary>
    public static DataValue FromInt128(Int128 value)
    {
        ref int words = ref Unsafe.As<Int128, int>(ref value);
        return new(DataKind.Int128, flags: 0,
            p0: words,
            p1: Unsafe.Add(ref words, 1),
            p2: Unsafe.Add(ref words, 2),
            p3: Unsafe.Add(ref words, 3));
    }

    /// <summary>
    /// Creates a byte-array value: <see cref="DataKind.UInt8"/> with the
    /// <see cref="DataValueFlags.IsArray"/> flag set. Bytes are written to
    /// <paramref name="store"/>. Use <see cref="AsByteSpan"/> or
    /// <see cref="AsUInt8Array(IValueStore, SidecarRegistry?)"/> to read.
    /// </summary>
    public static DataValue FromByteArray(byte[] value, IValueStore store)
    {
        var (p0, p1) = store.StoreBytes(value);
        return new(
            DataKind.UInt8,
            flags: DataValueFlags.InArena | DataValueFlags.IsArray,
            p0: p0, p1: p1);
    }

    /// <summary>
    /// Creates a byte-array value whose bytes live in a <c>.datum-blob</c>
    /// sidecar. The DataValue carries 64-bit absolute offset and 40-bit
    /// length; resolution requires an <see cref="IBlobSource"/> via the
    /// per-query <see cref="SidecarRegistry"/>.
    /// </summary>
    /// <param name="offset">Absolute byte offset into the sidecar file (includes header).</param>
    /// <param name="length">Number of bytes; 0 ≤ length ≤ <c>2^40 − 1</c> (~1 TiB).</param>
    /// <param name="storeId">
    /// The byte assigned by the per-query <see cref="DatumFile.Sidecar.SidecarRegistry"/>
    /// when the table provider registered its sidecar. Resolved at access time to find
    /// the right <see cref="IBlobSource"/>. Defaults to 0 (single-sidecar / first-registered).
    /// </param>
    public static DataValue FromByteArrayInSidecar(long offset, long length, byte storeId = 0) =>
        FromArrayInSidecar(DataKind.UInt8, offset, length, storeId);

    /// <summary>
    /// Creates a typed-array value (<paramref name="elementKind"/> + <see cref="DataValueFlags.IsArray"/>)
    /// whose element bytes live in a <c>.datum-blob</c> sidecar. Generalisation of
    /// <see cref="FromByteArrayInSidecar"/> for any fixed-width element kind. The
    /// DataValue carries 64-bit absolute offset and 40-bit length; resolution requires
    /// an <see cref="IBlobSource"/> via the per-query <see cref="SidecarRegistry"/>.
    /// </summary>
    /// <param name="elementKind">The fixed-width element kind (e.g. <see cref="DataKind.Float32"/>).</param>
    /// <param name="offset">Absolute byte offset into the sidecar file (includes header).</param>
    /// <param name="length">Number of bytes; 0 ≤ length ≤ <c>2^40 − 1</c> (~1 TiB).</param>
    /// <param name="storeId">
    /// The byte assigned by the per-query <see cref="DatumFile.Sidecar.SidecarRegistry"/>.
    /// Resolved at access time. Defaults to 0 (single-sidecar / first-registered).
    /// </param>
    public static DataValue FromArrayInSidecar(DataKind elementKind, long offset, long length, byte storeId = 0) =>
        BuildSidecar(elementKind, offset, length, storeId, isArray: true);

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
        Span<byte> scratch = stackalloc byte[16];
        if (System.Text.Encoding.UTF8.TryGetBytes(value, scratch, out int written))
        {
            return FromInlineUtf8(DataKind.String, scratch[..written], value.Length);
        }
        throw new InvalidOperationException(
            "FromString(value) without a store only supports strings whose UTF-8 form fits in 16 bytes. " +
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
        Span<byte> scratch = stackalloc byte[16];
        if (System.Text.Encoding.UTF8.TryGetBytes(value, scratch, out int written))
        {
            return FromInlineUtf8(DataKind.String, scratch[..written], value.Length);
        }

        var (p0, p1) = store.StoreString(value);
        var (hashLo, hashHi) = HashString(value.AsSpan());
        ushort cc = value.Length <= ushort.MaxValue ? (ushort)value.Length : ushort.MaxValue;
        return new(DataKind.String, flags: DataValueFlags.InArena, p0: p0, p1: p1, p2: hashLo, p3: hashHi, charCount: cc);
    }

    /// <summary>Creates a string value from a char span without allocating a managed string.</summary>
    public static DataValue FromCharSpan(ReadOnlySpan<char> chars, IValueStore store)
    {
        Span<byte> scratch = stackalloc byte[16];
        if (System.Text.Encoding.UTF8.TryGetBytes(chars, scratch, out int written))
        {
            return FromInlineUtf8(DataKind.String, scratch[..written], chars.Length);
        }

        var (p0, p1) = store.StoreChars(chars);
        var (hashLo, hashHi) = HashString(chars);
        ushort cc = chars.Length <= ushort.MaxValue ? (ushort)chars.Length : ushort.MaxValue;
        return new(DataKind.String, flags: DataValueFlags.InArena, p0: p0, p1: p1, p2: hashLo, p3: hashHi, charCount: cc);
    }

    /// <summary>Creates a string value from raw UTF-8 bytes without allocating a managed string.</summary>
    public static DataValue FromUtf8Span(ReadOnlySpan<byte> utf8, int charCount, IValueStore store)
    {
        if (utf8.Length <= 16)
        {
            return FromInlineUtf8(DataKind.String, utf8, charCount);
        }

        var (p0, p1) = store.StoreUtf8(utf8);
        var (hashLo, hashHi) = HashUtf8(utf8);
        ushort cc = charCount <= ushort.MaxValue ? (ushort)charCount : ushort.MaxValue;
        return new(DataKind.String, flags: DataValueFlags.InArena, p0: p0, p1: p1, p2: hashLo, p3: hashHi, charCount: cc);
    }

    /// <summary>
    /// Creates an inline <see cref="DataKind.String"/> whose UTF-8 bytes live directly
    /// in <c>_p0</c>-<c>_p3</c>. Requires
    /// <paramref name="utf8Bytes"/>.Length &lt;= 16 and <paramref name="charCount"/> &lt;= 16.
    /// Unused payload bytes are zeroed. Byte length and char count are packed into
    /// <c>_charCount</c> (low byte = bytes, high byte = chars).
    /// </summary>
    private static DataValue FromInlineUtf8(DataKind kind, ReadOnlySpan<byte> utf8Bytes, int charCount)
    {
        Span<byte> padded = stackalloc byte[16];
        padded.Clear();
        utf8Bytes.CopyTo(padded);

        // Reinterpret the 16-byte buffer as four native-order int32 words, matching how
        // the struct stores _p0-_p3 in memory. InlineUtf8Span reads the same bytes back
        // by casting the fields to a byte span, so the round-trip is endian-neutral.
        Span<int> asInts = MemoryMarshal.Cast<byte, int>(padded);

        ushort packed = (ushort)((utf8Bytes.Length & 0xFF) | ((charCount & 0xFF) << 8));

        return new(
            kind,
            flags: 0,
            p0: asInts[0],
            p1: asInts[1],
            p2: asInts[2],
            p3: asInts[3],
            charCount: packed);
    }

    /// <summary>
    /// Creates an arena-backed string value from an offset and length within a
    /// <see cref="Arena"/>.  The actual bytes are not stored in this struct;
    /// callers must resolve via <see cref="AsString(Arena)"/> or the
    /// <see cref="IValueStore"/> registry.
    /// </summary>
    /// <param name="offset">Byte offset into the owning <see cref="Arena"/>.</param>
    /// <param name="length">Byte length of the UTF-8 encoded string.</param>
    public static DataValue FromStringSlice(int offset, int length) =>
        new(DataKind.String, flags: DataValueFlags.InArena, p0: offset, p1: length);

    // ───────────────────────── Reference-type arrays ─────────────────────────
    //
    // Layout (per project_reference_type_arrays.md):
    //   N=0:  IsArray | IsInline, _charCount=0, payload bytes zero
    //   N=1:  IsArray | IsInline, _charCount=1, _p0–_p3 hold one 16-byte ArraySlot
    //   N≥2:  IsArray | InArena,  (_p0, _p1) = (offset, length) of slot block in store
    //
    // Each slot's offset/length references the element's bytes in the array's
    // single backing store. Cross-store combination requires a deep copy.

    /// <summary>
    /// Creates an <c>Array&lt;String&gt;</c> value. Element UTF-8 bytes are written
    /// to <paramref name="store"/>; for N≥2 a slot block (<c>N × 16 bytes</c>) is
    /// also written to <paramref name="store"/>. Use <see cref="AsStringArray"/> to
    /// read back.
    /// </summary>
    public static DataValue FromStringArray(ReadOnlySpan<string> elements, IValueStore store)
    {
        if (elements.Length == 0)
        {
            // Inline = absence of storage flags; only IsArray is set.
            return new(
                DataKind.String,
                flags: DataValueFlags.IsArray,
                p0: 0,
                charCount: 0);
        }

        if (elements.Length == 1)
        {
            (int elementP0, int elementP1) = store.StoreString(elements[0]);
            Span<byte> slotBytes = stackalloc byte[ArraySlot.SizeBytes];
            ArraySlot.Write(slotBytes, elementP0, elementP1);
            int p0 = MemoryMarshal.Read<int>(slotBytes[..4]);
            int p1 = MemoryMarshal.Read<int>(slotBytes[4..8]);
            int p2 = MemoryMarshal.Read<int>(slotBytes[8..12]);
            int p3 = MemoryMarshal.Read<int>(slotBytes[12..16]);
            return new(
                DataKind.String,
                flags: DataValueFlags.IsArray,
                p0: p0, p1: p1, p2: p2, p3: p3,
                charCount: 1);
        }

        // N ≥ 2: write each element, then write the slot block.
        byte[] slotBlock = new byte[elements.Length * ArraySlot.SizeBytes];
        for (int i = 0; i < elements.Length; i++)
        {
            (int elementP0, int elementP1) = store.StoreString(elements[i]);
            ArraySlot.Write(
                slotBlock.AsSpan(i * ArraySlot.SizeBytes, ArraySlot.SizeBytes),
                elementP0,
                elementP1);
        }
        (int blockP0, int blockP1) = store.StoreBytes(slotBlock);
        return new(
            DataKind.String,
            flags: DataValueFlags.IsArray | DataValueFlags.InArena,
            p0: blockP0,
            p1: blockP1);
    }

    /// <summary>
    /// Reads an <c>Array&lt;String&gt;</c> value. <paramref name="store"/> resolves
    /// arena-backed slot blocks and element bytes; <paramref name="registry"/>
    /// resolves sidecar-backed arrays. Materialises into a <see cref="string"/>[]
    /// — for very large arrays prefer streaming once a streaming accessor lands.
    /// </summary>
    public string[] AsStringArray(IValueStore store, SidecarRegistry? registry = null)
    {
        ThrowIfNotReferenceArray(DataKind.String);

        // Sidecar-backed: read slot block AND per-element bytes through the
        // registry's IBlobSource. No per-query arena copy at access time —
        // sidecar arrays stay sidecar-resident until a caller explicitly
        // Stabilizes them.
        if (IsInSidecar)
        {
            IBlobSource src = ResolveSidecarSource(registry);
            ReadOnlySpan<byte> blockBytes = ReadSidecarBytes(registry);
            int elementCount = blockBytes.Length / ArraySlot.SizeBytes;
            string[] result = new string[elementCount];
            for (int i = 0; i < elementCount; i++)
            {
                ArraySlot.Read(
                    blockBytes.Slice(i * ArraySlot.SizeBytes, ArraySlot.SizeBytes),
                    out long elementOffset,
                    out long elementLength,
                    out _);
                ReadOnlySpan<byte> utf8 = src.Read(elementOffset, elementLength);
                result[i] = System.Text.Encoding.UTF8.GetString(utf8);
            }
            return result;
        }

        // N = 0 / N = 1 inline path (in-memory only — sidecar-backed arrays are
        // never inline). _charCount = element count (0 or 1).
        if (IsInline)
        {
            if (_charCount == 0) return [];

            // _charCount == 1 — parse the one inline slot from _p0–_p3.
            Span<byte> slotBytes = stackalloc byte[ArraySlot.SizeBytes];
            MemoryMarshal.Write(slotBytes[..4], _p0);
            MemoryMarshal.Write(slotBytes[4..8], _p1);
            MemoryMarshal.Write(slotBytes[8..12], _p2);
            MemoryMarshal.Write(slotBytes[12..16], _p3);
            ArraySlot.Read(slotBytes, out long elementOffset, out long elementLength, out _);
            return [store.RetrieveString((int)elementOffset, (int)elementLength)];
        }

        // N ≥ 2 arena-backed — slot block lives at (_p0, _p1) in the array's store.
        ReadOnlySpan<byte> arenaBlock = store.RetrieveUtf8Span(_p0, _p1);
        int n = arenaBlock.Length / ArraySlot.SizeBytes;
        string[] arenaResult = new string[n];
        for (int i = 0; i < n; i++)
        {
            ArraySlot.Read(
                arenaBlock.Slice(i * ArraySlot.SizeBytes, ArraySlot.SizeBytes),
                out long elementOffset,
                out long elementLength,
                out _);
            arenaResult[i] = store.RetrieveString((int)elementOffset, (int)elementLength);
        }
        return arenaResult;
    }

    /// <summary>
    /// Asserts that this value is a reference-array of <paramref name="elementKind"/>.
    /// Centralises the predicate so per-kind accessors share the same error shape.
    /// </summary>
    private void ThrowIfNotReferenceArray(DataKind elementKind)
    {
        if (!IsArray || _kind != elementKind || IsNull)
        {
            throw new InvalidOperationException(
                $"Expected an Array<{elementKind}> value (Kind={elementKind}, IsArray=true, non-null); " +
                $"got Kind={_kind}, IsArray={IsArray}, IsNull={IsNull}.");
        }
    }

    /// <summary>
    /// Resolves the <see cref="IBlobSource"/> backing a sidecar-resident reference
    /// array via the <see cref="SidecarStoreId"/> stored on this DataValue. Throws
    /// when no registry is supplied or the storeId isn't registered. Caller must
    /// have already verified <see cref="IsInSidecar"/>.
    /// </summary>
    private IBlobSource ResolveSidecarSource(SidecarRegistry? registry)
    {
        if (registry is null)
        {
            throw new InvalidOperationException(
                $"Reading a sidecar-backed Array<{_kind}> value requires a SidecarRegistry; got null.");
        }
        return registry.Resolve(SidecarStoreId)
            ?? throw new InvalidOperationException(
                $"Sidecar storeId {SidecarStoreId} from a sidecar-backed Array<{_kind}> value is " +
                "not registered in the supplied SidecarRegistry.");
    }

    /// <summary>Creates a value from encoded image bytes.</summary>
    /// <remarks>Obsolete: ReferenceStore has been removed. Use <see cref="FromImage(byte[], IValueStore)"/> instead.</remarks>
    public static DataValue FromImage(byte[] value) =>
        throw new InvalidOperationException("Use FromImage(value, store). ReferenceStore is no longer available.");

    /// <summary>Creates a value from encoded image bytes using an explicit <see cref="IValueStore"/>.</summary>
    public static DataValue FromImage(byte[] value, IValueStore store)
    {
        var (p0, p1) = store.StoreBytes(value);
        return new(DataKind.Image, flags: DataValueFlags.InArena, p0: p0, p1: p1);
    }

    /// <summary>
    /// Creates a <see cref="DataKind.Image"/> value whose encoded bytes live in a
    /// <c>.datum-blob</c> sidecar. The DataValue carries 64-bit absolute offset,
    /// 40-bit length, and the <c>storeId</c> byte that resolves to the right
    /// <see cref="IBlobSource"/> in the per-query
    /// <see cref="DatumFile.Sidecar.SidecarRegistry"/>.
    /// </summary>
    /// <param name="offset">Absolute byte offset into the sidecar file (includes header).</param>
    /// <param name="length">Number of bytes; 0 ≤ length ≤ <c>2^40 − 1</c> (~1 TiB).</param>
    /// <param name="storeId">Registry storeId byte (defaults to 0 for single-sidecar / first-registered).</param>
    public static DataValue FromImageInSidecar(long offset, long length, byte storeId = 0) =>
        BuildSidecar(DataKind.Image, offset, length, storeId);

    /// <summary>
    /// Creates an <c>Array&lt;Image&gt;</c> value. Each element's encoded bytes are
    /// written to <paramref name="store"/>; for N≥2 a slot block is also written.
    /// Layout matches <see cref="FromStringArray"/> — see
    /// <c>project_reference_type_arrays.md</c>.
    /// </summary>
    public static DataValue FromImageArray(ReadOnlySpan<byte[]> elements, IValueStore store)
    {
        if (elements.Length == 0)
        {
            return new(
                DataKind.Image,
                flags: DataValueFlags.IsArray,
                p0: 0,
                charCount: 0);
        }

        if (elements.Length == 1)
        {
            (int elementP0, int elementP1) = store.StoreBytes(elements[0]);
            Span<byte> slotBytes = stackalloc byte[ArraySlot.SizeBytes];
            ArraySlot.Write(slotBytes, elementP0, elementP1);
            int p0 = MemoryMarshal.Read<int>(slotBytes[..4]);
            int p1 = MemoryMarshal.Read<int>(slotBytes[4..8]);
            int p2 = MemoryMarshal.Read<int>(slotBytes[8..12]);
            int p3 = MemoryMarshal.Read<int>(slotBytes[12..16]);
            return new(
                DataKind.Image,
                flags: DataValueFlags.IsArray,
                p0: p0, p1: p1, p2: p2, p3: p3,
                charCount: 1);
        }

        byte[] slotBlock = new byte[elements.Length * ArraySlot.SizeBytes];
        for (int i = 0; i < elements.Length; i++)
        {
            (int elementP0, int elementP1) = store.StoreBytes(elements[i]);
            ArraySlot.Write(
                slotBlock.AsSpan(i * ArraySlot.SizeBytes, ArraySlot.SizeBytes),
                elementP0,
                elementP1);
        }
        (int blockP0, int blockP1) = store.StoreBytes(slotBlock);
        return new(
            DataKind.Image,
            flags: DataValueFlags.IsArray | DataValueFlags.InArena,
            p0: blockP0,
            p1: blockP1);
    }

    /// <summary>
    /// Reads an <c>Array&lt;Image&gt;</c> value as a <see cref="byte"/>[][].
    /// </summary>
    public byte[][] AsImageArray(IValueStore store, SidecarRegistry? registry = null)
    {
        ThrowIfNotReferenceArray(DataKind.Image);

        if (IsInSidecar)
        {
            IBlobSource src = ResolveSidecarSource(registry);
            ReadOnlySpan<byte> blockBytes = ReadSidecarBytes(registry);
            int elementCount = blockBytes.Length / ArraySlot.SizeBytes;
            byte[][] result = new byte[elementCount][];
            for (int i = 0; i < elementCount; i++)
            {
                ArraySlot.Read(
                    blockBytes.Slice(i * ArraySlot.SizeBytes, ArraySlot.SizeBytes),
                    out long elementOffset,
                    out long elementLength,
                    out _);
                result[i] = src.Read(elementOffset, elementLength).ToArray();
            }
            return result;
        }

        if (IsInline)
        {
            if (_charCount == 0) return [];

            Span<byte> slotBytes = stackalloc byte[ArraySlot.SizeBytes];
            MemoryMarshal.Write(slotBytes[..4], _p0);
            MemoryMarshal.Write(slotBytes[4..8], _p1);
            MemoryMarshal.Write(slotBytes[8..12], _p2);
            MemoryMarshal.Write(slotBytes[12..16], _p3);
            ArraySlot.Read(slotBytes, out long elementOffset, out long elementLength, out _);
            return [store.RetrieveBytes((int)elementOffset, (int)elementLength)];
        }

        ReadOnlySpan<byte> arenaBlock = store.RetrieveUtf8Span(_p0, _p1);
        int n = arenaBlock.Length / ArraySlot.SizeBytes;
        byte[][] arenaResult = new byte[n][];
        for (int i = 0; i < n; i++)
        {
            ArraySlot.Read(
                arenaBlock.Slice(i * ArraySlot.SizeBytes, ArraySlot.SizeBytes),
                out long elementOffset,
                out long elementLength,
                out _);
            arenaResult[i] = store.RetrieveBytes((int)elementOffset, (int)elementLength);
        }
        return arenaResult;
    }

    /// <summary>
    /// Creates an <c>Array&lt;Struct&gt;</c> value. Each element is a struct's
    /// <see cref="DataValue"/>[] of fields; each is stored via
    /// <see cref="IValueStore.StoreDataValues"/> and referenced from a slot. For
    /// N≥2 a slot block is also written. Field count per struct is implicit in
    /// the stored field-array length — the slot itself carries no field-count
    /// metadata, so heterogeneous-field-count elements are allowed at the value
    /// layer (the schema layer enforces uniformity when configured).
    /// </summary>
    public static DataValue FromStructArray(ReadOnlySpan<DataValue[]> elements, IValueStore store)
    {
        if (elements.Length == 0)
        {
            return new(
                DataKind.Struct,
                flags: DataValueFlags.IsArray,
                p0: 0,
                charCount: 0);
        }

        if (elements.Length == 1)
        {
            (int elementP0, int elementP1) = store.StoreDataValues(elements[0]);
            Span<byte> slotBytes = stackalloc byte[ArraySlot.SizeBytes];
            ArraySlot.Write(slotBytes, elementP0, elementP1);
            int p0 = MemoryMarshal.Read<int>(slotBytes[..4]);
            int p1 = MemoryMarshal.Read<int>(slotBytes[4..8]);
            int p2 = MemoryMarshal.Read<int>(slotBytes[8..12]);
            int p3 = MemoryMarshal.Read<int>(slotBytes[12..16]);
            return new(
                DataKind.Struct,
                flags: DataValueFlags.IsArray,
                p0: p0, p1: p1, p2: p2, p3: p3,
                charCount: 1);
        }

        byte[] slotBlock = new byte[elements.Length * ArraySlot.SizeBytes];
        for (int i = 0; i < elements.Length; i++)
        {
            (int elementP0, int elementP1) = store.StoreDataValues(elements[i]);
            ArraySlot.Write(
                slotBlock.AsSpan(i * ArraySlot.SizeBytes, ArraySlot.SizeBytes),
                elementP0,
                elementP1);
        }
        (int blockP0, int blockP1) = store.StoreBytes(slotBlock);
        return new(
            DataKind.Struct,
            flags: DataValueFlags.IsArray | DataValueFlags.InArena,
            p0: blockP0,
            p1: blockP1);
    }

    /// <summary>
    /// Reads an <c>Array&lt;Struct&gt;</c> value as a jagged
    /// <see cref="DataValue"/>[][] — outer index is element, inner is struct field.
    /// For sidecar-backed arrays, struct fields are deserialised on the fly from
    /// their sidecar bytes; reference-typed fields (strings, etc.) materialise
    /// into <paramref name="store"/> since they need a value-store target.
    /// </summary>
    public DataValue[][] AsStructArray(IValueStore store, SidecarRegistry? registry = null)
    {
        ThrowIfNotReferenceArray(DataKind.Struct);

        if (IsInSidecar)
        {
            IBlobSource src = ResolveSidecarSource(registry);
            ReadOnlySpan<byte> blockBytes = ReadSidecarBytes(registry);
            int elementCount = blockBytes.Length / ArraySlot.SizeBytes;
            DataValue[][] result = new DataValue[elementCount][];
            for (int i = 0; i < elementCount; i++)
            {
                ArraySlot.Read(
                    blockBytes.Slice(i * ArraySlot.SizeBytes, ArraySlot.SizeBytes),
                    out long elementOffset,
                    out long elementLength,
                    out _);
                ReadOnlySpan<byte> structBytes = src.Read(elementOffset, elementLength);
                result[i] = DeserializeStructFields(structBytes, store);
            }
            return result;
        }

        if (IsInline)
        {
            if (_charCount == 0) return [];

            Span<byte> slotBytes = stackalloc byte[ArraySlot.SizeBytes];
            MemoryMarshal.Write(slotBytes[..4], _p0);
            MemoryMarshal.Write(slotBytes[4..8], _p1);
            MemoryMarshal.Write(slotBytes[8..12], _p2);
            MemoryMarshal.Write(slotBytes[12..16], _p3);
            ArraySlot.Read(slotBytes, out long elementOffset, out long elementLength, out _);
            return [store.RetrieveDataValues((int)elementOffset, (int)elementLength)];
        }

        ReadOnlySpan<byte> arenaBlock = store.RetrieveUtf8Span(_p0, _p1);
        int n = arenaBlock.Length / ArraySlot.SizeBytes;
        DataValue[][] arenaResult = new DataValue[n][];
        for (int i = 0; i < n; i++)
        {
            ArraySlot.Read(
                arenaBlock.Slice(i * ArraySlot.SizeBytes, ArraySlot.SizeBytes),
                out long elementOffset,
                out long elementLength,
                out _);
            arenaResult[i] = store.RetrieveDataValues((int)elementOffset, (int)elementLength);
        }
        return arenaResult;
    }

    /// <summary>
    /// Deserialises a single struct's field bytes (uint16 fieldCount + N
    /// self-describing records) — the inverse of the encoder's
    /// <c>SerializeStructFieldArray</c>. Reference-typed fields are written
    /// into <paramref name="store"/> as part of deserialisation.
    /// </summary>
    private static DataValue[] DeserializeStructFields(ReadOnlySpan<byte> bytes, IValueStore store)
    {
        byte[] copy = bytes.ToArray();
        using MemoryStream ms = new(copy, writable: false);
        using BinaryReader br = new(ms, System.Text.Encoding.UTF8, leaveOpen: false);
        ushort fieldCount = br.ReadUInt16();
        DataValue[] fields = new DataValue[fieldCount];
        for (int j = 0; j < fieldCount; j++)
        {
            fields[j] = IO.DataValueReader.ReadDataValue(br, store);
        }
        return fields;
    }

    /// <summary>
    /// Packs a sidecar coordinate into the DataValue payload. <c>_p0</c>+<c>_p1</c>
    /// hold the 64-bit offset, <c>_p2</c> + low byte of <c>_p3</c> hold the 40-bit
    /// length, the high 24 bits of <c>_p3</c> are reserved (zero in v1), and the
    /// low byte of <c>_charCount</c> holds the registry <c>storeId</c>. Pass
    /// <paramref name="isArray"/> = <c>true</c> when authoring a typed-array
    /// payload via the IsArray-flag model (e.g. <c>FromByteArrayInSidecar</c>);
    /// scalar / single-blob kinds like <see cref="DataKind.Image"/> leave it
    /// <c>false</c>.
    /// </summary>
    internal static DataValue BuildSidecar(
        DataKind kind, long offset, long length, byte storeId, bool isArray = false)
    {
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset), offset, "Sidecar offset must be non-negative.");
        }
        if (length < 0 || length > SidecarLengthMax)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length), length,
                $"Sidecar length must be in [0, {SidecarLengthMax}] (40-bit cap).");
        }

        int p0 = (int)offset;
        int p1 = (int)(offset >> 32);
        int p2 = (int)length;
        int p3 = (int)((length >> 32) & 0xFF);  // high 8 bits of length; high 24 bits of _p3 reserved

        DataValueFlags flags = DataValueFlags.InSidecar;
        if (isArray) flags |= DataValueFlags.IsArray;

        return new(kind, flags: flags, p0: p0, p1: p1, p2: p2, p3: p3, charCount: storeId);
    }

    // ───────────────────────── Inline arrays ─────────────────────────

    /// <summary>
    /// Maximum byte length for an inline array payload — equals the size of the
    /// <c>_p0</c>-<c>_p3</c> payload region.
    /// </summary>
    private const int InlineArrayMaxBytes = 16;

    /// <summary>
    /// Creates a typed-array <see cref="DataValue"/> with elements packed inline into
    /// the struct's 16-byte payload region. No arena allocation, no store dereference,
    /// no <see cref="DataValueRetention.Stabilize"/> copy required — fully self-contained.
    /// Useful for small fixed-shape arrays: <c>Float32[4]</c> (quaternions, RGBA, 3D
    /// points), <c>Int32[4]</c>, <c>Float64[2]</c> (lat/lon), <c>UInt8[16]</c>
    /// (UUID-like byte tags).
    /// </summary>
    /// <typeparam name="T">Unmanaged element type matching <paramref name="elementKind"/>.</typeparam>
    /// <param name="elements">The elements to pack. <c>elements.Length * sizeof(T)</c> must fit in 16 bytes.</param>
    /// <param name="elementKind">
    /// Stored as <see cref="Kind"/>. The caller is responsible for the kind matching
    /// <typeparamref name="T"/> (e.g. <see cref="DataKind.Float32"/> with <c>T = float</c>);
    /// no runtime check is performed because the lookup at read time is by kind, not by
    /// the original generic argument.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <c>elements.Length * sizeof(T)</c> exceeds 16, or when
    /// <c>elements.Length</c> exceeds 255 (the byte cap on the count field).
    /// </exception>
    public static DataValue FromInlineArray<T>(ReadOnlySpan<T> elements, DataKind elementKind)
        where T : unmanaged
        => FromInlineArrayBytes(MemoryMarshal.AsBytes(elements), elementKind);

    /// <summary>
    /// Creates an inline typed-array value from raw bytes, deriving the element count
    /// from <paramref name="bytes"/>.Length and <see cref="ElementByteSize"/>(<paramref name="elementKind"/>).
    /// Used by the v2 decoder and any caller that already has the byte payload — the
    /// <see cref="FromInlineArray{T}"/> overload is a typed wrapper for callers with
    /// element-typed spans.
    /// </summary>
    /// <remarks>
    /// The flag layout, payload packing, and 16-byte cap are identical to
    /// <see cref="FromInlineArray{T}"/>; this is the single packing implementation both
    /// factories share. Callers must ensure <paramref name="bytes"/>.Length is a
    /// multiple of <see cref="ElementByteSize"/>(<paramref name="elementKind"/>).
    /// </remarks>
    public static DataValue FromInlineArrayBytes(ReadOnlySpan<byte> bytes, DataKind elementKind)
    {
        int elementSize = ElementByteSize(elementKind);
        if (bytes.Length % elementSize != 0)
        {
            throw new ArgumentException(
                $"Inline array byte length {bytes.Length} is not a multiple of element size " +
                $"{elementSize} for DataKind.{elementKind}.",
                nameof(bytes));
        }
        if (bytes.Length > InlineArrayMaxBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bytes), bytes.Length,
                $"Inline array payload {bytes.Length} bytes exceeds the {InlineArrayMaxBytes}-byte " +
                "limit. Use the arena-backed array factory instead.");
        }
        int elementCount = bytes.Length / elementSize;
        if (elementCount > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bytes), elementCount,
                $"Inline array element count {elementCount} exceeds the 255-element field cap.");
        }

        // Pack bytes into the four payload words via a stack buffer. The buffer is 16
        // bytes (= InlineArrayMaxBytes); the validated byteCount fits within it, so the
        // unfilled tail stays zero — readers slice by element count, not by buffer
        // size, so the trailing zeros don't leak.
        Span<byte> buffer = stackalloc byte[InlineArrayMaxBytes];
        bytes.CopyTo(buffer);

        int p0 = MemoryMarshal.Read<int>(buffer[..4]);
        int p1 = MemoryMarshal.Read<int>(buffer[4..8]);
        int p2 = MemoryMarshal.Read<int>(buffer[8..12]);
        int p3 = MemoryMarshal.Read<int>(buffer[12..16]);

        return new(
            elementKind,
            flags: DataValueFlags.IsArray | DataValueFlags.InlineArray,
            p0: p0, p1: p1, p2: p2, p3: p3,
            charCount: (ushort)elementCount);
    }

    /// <summary>
    /// Creates an arena-backed typed-array value from a span of elements. Bytes are
    /// written contiguously to <paramref name="store"/>; the resulting DataValue carries
    /// <see cref="DataKind"/> = <paramref name="elementKind"/> with
    /// <see cref="DataValueFlags.IsArray"/> | <see cref="DataValueFlags.InArena"/> set.
    /// Use <see cref="AsArraySpan{T}"/> to read.
    /// </summary>
    /// <typeparam name="T">
    /// Element type. Must be unmanaged and must match <paramref name="elementKind"/>'s
    /// storage shape — caller is responsible (e.g. <c>T = float</c> for
    /// <see cref="DataKind.Float32"/>). The store sees only bytes; mismatched T/kind
    /// pairings produce silently wrong reads.
    /// </typeparam>
    public static DataValue FromArenaArray<T>(
        ReadOnlySpan<T> elements,
        DataKind elementKind,
        IValueStore store)
        where T : unmanaged
    {
        var (p0, p1) = store.StoreBytes(MemoryMarshal.AsBytes(elements));
        return new(
            elementKind,
            flags: DataValueFlags.InArena | DataValueFlags.IsArray,
            p0: p0, p1: p1);
    }

    /// <summary>
    /// Returns the inline-array elements as a typed read-only span. The span points
    /// directly into the struct's payload region via a managed reference, so it is
    /// valid for the lifetime of <c>this</c> (or any copy — <see cref="DataValue"/>
    /// is a value type, so the elements follow the struct).
    /// </summary>
    /// <typeparam name="T">Element type. Must match the kind the value was authored
    /// with — caller is responsible for using a sensible <typeparamref name="T"/>.</typeparam>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="IsInlineArray"/> is <c>false</c> — caller should branch
    /// on the flag before invoking.
    /// </exception>
    public ReadOnlySpan<T> AsInlineArraySpan<T>() where T : unmanaged
    {
        if (!IsInlineArray)
        {
            throw new InvalidOperationException(
                "AsInlineArraySpan called on a non-inline value. Check IsInlineArray before invoking.");
        }
        // Reinterpret the payload region's first int field as ref T and build a span
        // covering the active element count. Same idiom as SidecarOffset / SidecarLength
        // accessors above; works without `fixed` because the ref keeps the struct
        // tracked by the GC via Unsafe.AsRef on a readonly field.
        ref T head = ref Unsafe.As<int, T>(ref Unsafe.AsRef(in _p0));
        return MemoryMarshal.CreateReadOnlySpan(ref head, InlineArrayElementCount);
    }

    /// <summary>
    /// Returns the raw inline-array bytes. Equivalent to
    /// <see cref="AsInlineArraySpan{T}"/> with <c>T = byte</c>, but more convenient when
    /// the caller doesn't need a typed span.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="IsInlineArray"/> is <c>false</c>.
    /// </exception>
    public ReadOnlySpan<byte> InlineArrayBytes
    {
        get
        {
            if (!IsInlineArray)
            {
                throw new InvalidOperationException(
                    "InlineArrayBytes accessed on a non-inline value. Check IsInlineArray first.");
            }
            int byteCount = InlineArrayElementCount * ElementByteSize(_kind);
            ref byte head = ref Unsafe.As<int, byte>(ref Unsafe.AsRef(in _p0));
            return MemoryMarshal.CreateReadOnlySpan(ref head, byteCount);
        }
    }

    /// <summary>
    /// Byte size of a single element of the given primitive <see cref="DataKind"/>.
    /// Used by <see cref="InlineArrayBytes"/> to compute the active byte count from
    /// the stored element count. Throws for kinds without a fixed element size.
    /// </summary>
    private static int ElementByteSize(DataKind kind) => kind switch
    {
        DataKind.UInt8 or DataKind.Int8 or DataKind.Boolean => 1,
        DataKind.UInt16 or DataKind.Int16 or DataKind.Float16 => 2,
        DataKind.UInt32 or DataKind.Int32 or DataKind.Float32 or DataKind.Date => 4,
        DataKind.UInt64 or DataKind.Int64 or DataKind.Float64
            or DataKind.DateTime or DataKind.Time or DataKind.Duration => 8,
        DataKind.Uuid or DataKind.Decimal or DataKind.UInt128 or DataKind.Int128 => 16,
        _ => throw new InvalidOperationException(
            $"DataKind.{kind} has no fixed element byte size — inline arrays of this kind are not supported."),
    };

    /// <summary>
    /// Universal typed-array reader: dispatches across inline, sidecar, and arena
    /// storage paths and returns a <see cref="ReadOnlySpan{T}"/> over the elements,
    /// so callers don't have to inspect storage flags themselves.
    /// </summary>
    /// <typeparam name="T">
    /// Element type. Must be unmanaged and must match the kind the value was authored
    /// with (e.g. <c>T = float</c> for a <see cref="DataKind.Float32"/> array). Caller
    /// is responsible — this method does not check kind/<typeparamref name="T"/>
    /// agreement because the underlying storage is byte-level.
    /// </typeparam>
    /// <param name="store">
    /// Required when the array is arena-backed; ignored otherwise. Pass the
    /// <c>EvaluationFrame.Source</c> arena (or whichever store backs the row).
    /// </param>
    /// <param name="registry">
    /// Required when the array is sidecar-backed; ignored otherwise. Pass the
    /// <c>EvaluationFrame.SidecarRegistry</c> (or the catalog's registry).
    /// </param>
    /// <remarks>
    /// <para>
    /// Supported storage paths in this version:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>Inline arrays (<see cref="IsInlineArray"/>): zero-allocation, no parameters needed.</description></item>
    ///   <item><description>Sidecar-backed arrays (<see cref="IsInSidecar"/>): byte-level read from the registry.</description></item>
    ///   <item><description>Arena-backed values produced via the new <c>IsArray</c> flag model: byte-level read from <paramref name="store"/>.</description></item>
    /// </list>
    /// <para>
    /// The legacy <see cref="DataKind.Array"/> kind (heterogeneous-element array
    /// stored as <c>DataValue[]</c>) does not route through this auto-router and
    /// must use <c>AsArray(store)</c>. Folding it in is deferred to the composite
    /// reference-array design.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the value isn't an array, when a required parameter is missing for
    /// the resolved storage path, or when the value uses the legacy
    /// <see cref="DataKind.Array"/> kind.
    /// </exception>
    public ReadOnlySpan<T> AsArraySpan<T>(IValueStore? store = null, SidecarRegistry? registry = null)
        where T : unmanaged
    {
        if (!IsArray)
        {
            throw new InvalidOperationException(
                $"AsArraySpan called on a non-array value (Kind={_kind}, IsArray=false). " +
                "Use the scalar accessor or check IsArray first.");
        }

        if (IsInlineArray)
        {
            return AsInlineArraySpan<T>();
        }

        if (IsInSidecar)
        {
            ReadOnlySpan<byte> sidecarBytes = ReadSidecarBytes(registry);
            return MemoryMarshal.Cast<byte, T>(sidecarBytes);
        }

        // Arena-backed via the new IsArray flag model: bytes live at (_p0, _p1) in
        // the store. Reinterpret the byte span as ReadOnlySpan<T>.
        if ((_flags & DataValueFlags.IsArray) != 0 && (_flags & DataValueFlags.InArena) != 0)
        {
            if (store is null)
            {
                throw new InvalidOperationException(
                    "AsArraySpan: arena-backed array requires an IValueStore. " +
                    "Pass the frame's Source arena.");
            }
            ReadOnlySpan<byte> arenaBytes = store.RetrieveUtf8Span(_p0, _p1);
            return MemoryMarshal.Cast<byte, T>(arenaBytes);
        }

        // Legacy DataKind.Array reaches IsArray=true via the kind-based fallback in
        // the IsArray getter, but its DataValue[]-backed payload is managed by its
        // dedicated AsArray(store) accessor. Folding it in is deferred to the
        // composite reference-array design.
        throw new InvalidOperationException(
            $"AsArraySpan does not yet route DataKind.{_kind} arrays. " +
            "Use AsArray(store) for the legacy heterogeneous-element array kind.");
    }

    /// <summary>
    /// Creates an <see cref="DataKind.Image"/> value that references bytes already
    /// written to an <see cref="IValueStore"/> at the given <paramref name="offset"/>
    /// and <paramref name="length"/>. Use when the bytes were streamed directly into
    /// an arena (e.g. via <see cref="Arena.AppendFromStream"/>) to avoid the managed
    /// <c>byte[]</c> allocation that <see cref="FromImage(byte[], IValueStore)"/>
    /// would otherwise force.
    /// </summary>
    public static DataValue FromImageAtOffset(int offset, int length) =>
        new(DataKind.Image, flags: DataValueFlags.InArena, p0: offset, p1: length);

    /// <summary>
    /// Creates a byte-array value that references bytes already written to an
    /// <see cref="IValueStore"/> at the given offset and length. Parallel to
    /// <see cref="FromImageAtOffset"/> for generic binary payloads where the
    /// bytes are already arena-resident. Produces <see cref="DataKind.UInt8"/>
    /// with the <see cref="DataValueFlags.IsArray"/> flag.
    /// </summary>
    public static DataValue FromByteArrayAtOffset(int offset, int length) =>
        new(
            DataKind.UInt8,
            flags: DataValueFlags.InArena | DataValueFlags.IsArray,
            p0: offset, p1: length);

    /// <summary>Creates a value from a calendar date.</summary>
    public static DataValue FromDate(DateOnly value) =>
        new(DataKind.Date, flags: 0, p0: value.DayNumber);

    /// <summary>Creates a value from a date and time with UTC offset.</summary>
    public static DataValue FromDateTime(DateTimeOffset value)
    {
        long ticks = value.Ticks;
        return new(DataKind.DateTime, flags: 0,
            p0: (int)ticks, p1: (int)(ticks >> 32),
            p2: (int)(value.Offset.Ticks / TimeSpan.TicksPerMinute));
    }

    /// <summary>
    /// Computes XxHash64 over the UTF-8 encoding of a char span and splits into two int32 halves.
    /// Always hashes UTF-8 bytes for consistency with <see cref="HashUtf8"/>.
    /// </summary>
    /// <summary>
    /// Computes GetHashCode for a string value that has no cached hash (e.g. legacy arena-backed values).
    /// Falls back to offset/length-based hash since we have no store to resolve content.
    /// </summary>
    private int ComputeStringHashCode()
    {
        return HashCode.Combine(_kind, _p0, _p1);
    }

    private static (int Lo, int Hi) HashString(ReadOnlySpan<char> chars)
    {
        int maxBytes = System.Text.Encoding.UTF8.GetMaxByteCount(chars.Length);
        byte[]? rented = null;
        Span<byte> utf8 = maxBytes <= 256
            ? stackalloc byte[maxBytes]
            : (rented = System.Buffers.ArrayPool<byte>.Shared.Rent(maxBytes));
        int written = System.Text.Encoding.UTF8.GetBytes(chars, utf8);
        var result = HashUtf8(utf8[..written]);
        if (rented is not null) System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        return result;
    }

    /// <summary>Computes XxHash64 over raw UTF-8 bytes and splits into two int32 halves.</summary>
    private static (int Lo, int Hi) HashUtf8(ReadOnlySpan<byte> utf8)
    {
        ulong hash = XxHash64.HashToUInt64(utf8);
        return ((int)hash, (int)(hash >> 32));
    }

    /// <summary>Creates a value from a 128-bit universally unique identifier.</summary>
    public static DataValue FromUuid(Guid value)
    {
        ref int words = ref Unsafe.As<Guid, int>(ref value);
        return new(DataKind.Uuid, flags: 0,
            p0: words,
            p1: Unsafe.Add(ref words, 1),
            p2: Unsafe.Add(ref words, 2),
            p3: Unsafe.Add(ref words, 3));
    }

    /// <summary>Creates a boolean value.</summary>
    public static DataValue FromBoolean(bool value) =>
        value ? BooleanTrue : BooleanFalse;

    /// <summary>Creates a value from a time-of-day.</summary>
    public static DataValue FromTime(TimeOnly value)
    {
        long ticks = value.Ticks;
        return new(DataKind.Time, flags: 0, p0: (int)ticks, p1: (int)(ticks >> 32));
    }

    /// <summary>Creates a value from a duration (elapsed time span).</summary>
    public static DataValue FromDuration(TimeSpan value)
    {
        long ticks = value.Ticks;
        return new(DataKind.Duration, flags: 0, p0: (int)ticks, p1: (int)(ticks >> 32));
    }

    /// <summary>Creates a type tag value that describes another <see cref="DataKind"/>.</summary>
    public static DataValue FromType(DataKind value) =>
        new(DataKind.Type, flags: 0, p0: (int)value);

    // ───────────────────────── Arena state ─────────────────────────

    /// <summary>
    /// Whether this value's payload lives in an external <see cref="IValueStore"/>
    /// (typically an <see cref="Arena"/>) rather than inline. The inline payload holds an
    /// offset/length pair (<c>_p0</c>/<c>_p1</c>) into the store. True for variable-size
    /// reference types (vectors, matrices, images, arrays, structs, byte arrays) and for
    /// strings/JSON whose UTF-8 form exceeds 16 bytes or were produced via
    /// <see cref="FromStringSlice(int, int)"/>.
    /// </summary>
    public bool IsArenaBacked => (_flags & DataValueFlags.InArena) != 0;

    /// <summary>
    /// Returns a new <see cref="DataValue"/> with all arena-backed data materialised
    /// into self-contained managed objects stored in <paramref name="store"/>.
    /// Non-arena values are returned unchanged.
    /// </summary>
    /// <param name="arena">Arena that owns the UTF-8 bytes.</param>
    /// <param name="store">Store to write the materialised string into.</param>
    /// <returns>A self-contained value that does not reference the arena.</returns>
    public DataValue Materialize(Arena arena, IValueStore store)
    {
        if (!IsArenaBacked) return this;

        return _kind switch
        {
            DataKind.String => FromString(arena.GetString(_p0, _p1), store),
            _ => this,
        };
    }

    /// <summary>
    /// Returns a new <see cref="DataValue"/> with all arena-backed data materialised.
    /// </summary>
    /// <param name="arena">Arena for string and binary data.</param>
    /// <returns>A self-contained value that does not reference any arena.</returns>
    public DataValue Materialize(Arena arena) => Materialize(arena, arena);

    /// <summary>
    /// Returns a new arena-backed <see cref="DataValue"/> whose offset has been shifted by
    /// <paramref name="delta"/> bytes.  Used when merging per-column private arenas into a
    /// shared batch arena after parallel decode.
    /// </summary>
    /// <param name="delta">Number of bytes to add to the current offset.</param>
    /// <returns>An adjusted value whose length is unchanged.</returns>
    internal DataValue WithArenaOffset(int delta)
    {
        return new DataValue(_kind, flags: _flags, p0: _p0 + delta, p1: _p1, p2: _p2, p3: _p3, charCount: _charCount);
    }

    /// <summary>Creates a typed array value from an element kind and an array of elements.</summary>
    /// <remarks>Obsolete: ReferenceStore has been removed. Use <see cref="FromArray(DataKind, DataValue[], IValueStore)"/> instead.</remarks>
    public static DataValue FromArray(DataKind elementKind, DataValue[] elements) =>
        throw new InvalidOperationException("Use FromArray(elementKind, elements, store). ReferenceStore is no longer available.");

    /// <summary>Creates a typed array value using an explicit <see cref="IValueStore"/>.</summary>
    public static DataValue FromArray(DataKind elementKind, DataValue[] elements, IValueStore store)
    {
        var (p0, p1) = store.StoreDataValues(elements);
        return new(DataKind.Array, flags: DataValueFlags.InArena, p0: p0, p1: p1, p2: (int)elementKind);
    }

    /// <summary>Creates a typed array value using an explicit <see cref="IValueStore"/>.</summary>
    public static DataValue FromArray(DataKind elementKind, List<DataValue> elements, IValueStore store)
    {
        var (p0, p1) = store.StoreDataValues(CollectionsMarshal.AsSpan(elements));
        return new(DataKind.Array, flags: DataValueFlags.InArena, p0: p0, p1: p1, p2: (int)elementKind);
    }

    /// <summary>Creates a typed null array with the given element kind.</summary>
    /// <param name="elementKind">The element kind of the null array.</param>
    public static DataValue NullArray(DataKind elementKind) =>
        new(DataKind.Array, DataValueFlags.IsNull, referenceIndex: 0, meta: (short)elementKind);

    /// <summary>Creates a struct value from a positional array of field values.</summary>
    /// <remarks>Obsolete: ReferenceStore has been removed. Use <see cref="FromStruct(short, DataValue[], IValueStore)"/> instead.</remarks>
    public static DataValue FromStruct(short fieldCount, DataValue[] fields) =>
        throw new InvalidOperationException("Use FromStruct(fieldCount, fields, store). ReferenceStore is no longer available.");

    /// <summary>Creates a struct value using an explicit <see cref="IValueStore"/>.</summary>
    public static DataValue FromStruct(short fieldCount, DataValue[] fields, IValueStore store)
    {
        var (p0, p1) = store.StoreDataValues(fields);
        return new(DataKind.Struct, flags: DataValueFlags.InArena, p0: p0, p1: p1, p2: fieldCount);
    }

    /// <summary>Creates a typed null struct with the given field count.</summary>
    public static DataValue NullStruct(short fieldCount) =>
        new(DataKind.Struct, DataValueFlags.IsNull, referenceIndex: 0, meta: fieldCount);

    /// <summary>Creates a typed null value.</summary>
    public static DataValue Null(DataKind kind)
        => new(kind, flags: DataValueFlags.IsNull, p0: 0);

    /// <summary>Creates a typed null byte array (UInt8 + IsNull + IsArray).</summary>
    public static DataValue NullByteArray()
        => new(DataKind.UInt8, flags: DataValueFlags.IsNull | DataValueFlags.IsArray, p0: 0);

    /// <summary>
    /// Creates a null value whose type is not statically known.
    /// </summary>
    /// <remarks>
    /// SQL NULL has no inherent type. When a NULL literal appears outside a typed context
    /// (e.g. <c>SELECT NULL</c>), neither the parser nor the evaluator can determine its
    /// kind. This factory produces a <see cref="DataKind.Unknown"/> null. Downstream
    /// consumers (aggregations, output writers, CASE coercion) resolve the actual kind
    /// from context. Call sites should prefer a typed <see cref="Null(DataKind)"/> when
    /// the expected kind is known.
    /// </remarks>
    public static DataValue UnknownNull() => NullUnknown;

    // ───────────────────────── Literal conversion ─────────────────────────

    /// <summary>
    /// Converts a CLR literal value (typically from an AST <see cref="Parsing.Ast.LiteralExpression"/>)
    /// to a <see cref="DataValue"/> using the natural type mapping.
    /// </summary>
    /// <param name="rawLiteral">
    /// A boxed CLR value: <see cref="double"/> (from the SQL parser), <see cref="int"/>,
    /// <see cref="long"/>, <see cref="float"/> (from rewriters), <see cref="string"/>,
    /// <see cref="bool"/>, or an existing <see cref="DataValue"/>.
    /// </param>
    /// <param name="store">Store for reference-type payloads (strings, etc.).</param>
    /// <returns>A <see cref="DataValue"/> preserving the CLR type's natural precision.</returns>
    /// <exception cref="ArgumentException">The literal type is not supported.</exception>
    public static DataValue FromLiteral(object rawLiteral, IValueStore store)
    {
        return rawLiteral switch
        {
            DataValue dataValue => dataValue,
            sbyte int8Value => FromInt8(int8Value),
            short int16Value => FromInt16(int16Value),
            int intValue => FromInt32(intValue),
            long longValue => FromInt64(longValue),
            float floatValue => FromFloat32(floatValue),
            double doubleValue => FromFloat64(doubleValue),
            decimal decimalValue => FromFloat64((double)decimalValue),
            string stringValue => FromString(stringValue, store),
            bool boolValue => FromBoolean(boolValue),
            _ => throw new ArgumentException(
                $"Unsupported literal type: {rawLiteral.GetType().Name}.", nameof(rawLiteral)),
        };
    }

    // /// <summary>
    // /// Converts a CLR literal value to a <see cref="DataValue"/>.
    // /// </summary>
    // /// <remarks>Note: string literals require a store. Use <see cref="FromLiteral(object, IValueStore)"/> for string literals.</remarks>
    // public static DataValue FromLiteral(object rawLiteral)
    // {
    //     throw new InvalidOperationException(
    //         "Use FromLiteral(rawLiteral, store) for string literals. ReferenceStore is no longer available.");
    // }

    /// <summary>
    /// Maps a CLR <see cref="Type"/> to the corresponding <see cref="DataKind"/>.
    /// Unwraps <see cref="Nullable{T}"/> automatically. Falls back to
    /// <see cref="DataKind.String"/> for unrecognised types.
    /// </summary>
    public static DataKind MapClrType(Type clrType) => DataValueComparer.MapClrType(clrType);

    /// <summary>
    /// Coerces this value to a different <see cref="DataKind"/>. Used when the column's
    /// stored type differs from the literal's natural type (e.g. a <see cref="DataKind.Float64"/>
    /// literal compared against a <see cref="DataKind.Int32"/> bitmap index key).
    /// </summary>
    /// <remarks>
    /// Numeric and boolean values are coerced via a <see cref="double"/> intermediary.
    /// Non-numeric/non-boolean values, or values whose kind already matches
    /// <paramref name="targetKind"/>, are returned unchanged.
    /// </remarks>
    /// <param name="targetKind">The desired <see cref="DataKind"/>.</param>
    /// <returns>A new value of the target kind, or this value unchanged if coercion is not applicable.</returns>
    public DataValue CoerceToKind(DataKind targetKind)
    {
        if (_kind == targetKind)
        {
            return this;
        }

        if (!IsCoercibleKind(_kind) || !IsCoercibleKind(targetKind))
        {
            return this;
        }

        double intermediate = ToDoubleRaw();
        return FromDoubleRaw(intermediate, targetKind);
    }

    /// <summary>
    /// Returns whether the given kind participates in numeric/boolean coercion.
    /// </summary>
    private static bool IsCoercibleKind(DataKind kind)
    {
        return kind is DataKind.Float16 or DataKind.Float32 or DataKind.Float64
            or DataKind.Decimal
            or DataKind.UInt8 or DataKind.Int8 or DataKind.Int16 or DataKind.UInt16
            or DataKind.Int32 or DataKind.UInt32 or DataKind.Int64 or DataKind.UInt64
            or DataKind.Int128 or DataKind.UInt128
            or DataKind.Boolean;
    }

    /// <summary>
    /// Extracts the numeric payload as a <see cref="double"/> for coercion purposes.
    /// </summary>
    private double ToDoubleRaw()
    {
        return _kind switch
        {
            DataKind.Float16 => (double)BitConverter.UInt16BitsToHalf((ushort)_p0),
            DataKind.Float32 => BitConverter.Int32BitsToSingle(_p0),
            DataKind.Float64 => BitConverter.Int64BitsToDouble(ReadLong()),
            DataKind.Decimal => (double)AsDecimal(),
            DataKind.UInt8 => (byte)_p0,
            DataKind.Int8 => (sbyte)_p0,
            DataKind.Int16 => (short)_p0,
            DataKind.UInt16 => (ushort)_p0,
            DataKind.Int32 => _p0,
            DataKind.UInt32 => (uint)_p0,
            DataKind.Int64 => ReadLong(),
            DataKind.UInt64 => (ulong)ReadLong(),
            DataKind.Int128 => (double)AsInt128(),
            DataKind.UInt128 => (double)AsUInt128(),
            DataKind.Boolean => _p0 != 0 ? 1d : 0d,
            _ => 0d,
        };
    }

    /// <summary>
    /// Reads <c>_p0</c> and <c>_p1</c> as a single <see cref="long"/> value.
    /// Used by 64-bit types (Int64, UInt64, Float64, DateTime, Time, Duration).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long ReadLong() =>
        Unsafe.As<int, long>(ref Unsafe.AsRef(in _p0));

    /// <summary>
    /// Creates a <see cref="DataValue"/> of the specified kind from a <see cref="double"/> value.
    /// </summary>
    private static DataValue FromDoubleRaw(double value, DataKind targetKind)
    {
        return targetKind switch
        {
            DataKind.Float16 => FromFloat16((Half)value),
            DataKind.Float32 => FromFloat32((float)value),
            DataKind.Float64 => FromFloat64(value),
            DataKind.Decimal => FromDecimal((decimal)value),
            DataKind.UInt8 => FromUInt8((byte)value),
            DataKind.Int8 => FromInt8((sbyte)value),
            DataKind.Int16 => FromInt16((short)value),
            DataKind.UInt16 => FromUInt16((ushort)value),
            DataKind.Int32 => FromInt32((int)value),
            DataKind.UInt32 => FromUInt32((uint)value),
            DataKind.Int64 => FromInt64((long)value),
            DataKind.UInt64 => FromUInt64((ulong)value),
            DataKind.Int128 => FromInt128((Int128)value),
            DataKind.UInt128 => FromUInt128((UInt128)value),
            DataKind.Boolean => FromBoolean(value != 0d),
            _ => throw new ArgumentException(
                $"Cannot coerce to non-numeric kind {targetKind}.", nameof(targetKind)),
        };
    }

    // ───────────────────────── Accessor methods ─────────────────────────

    /// <summary>Returns the 32-bit floating-point payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public float AsFloat32()
    {
        ThrowIfNullOrWrongKind(DataKind.Float32);
        return BitConverter.Int32BitsToSingle(_p0);
    }

    /// <summary>Returns the unsigned 8-bit integer payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public byte AsUInt8()
    {
        ThrowIfNullOrWrongKind(DataKind.UInt8);
        return (byte)_p0;
    }

    /// <summary>Returns the signed 8-bit integer payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public sbyte AsInt8()
    {
        ThrowIfNullOrWrongKind(DataKind.Int8);
        return (sbyte)_p0;
    }

    /// <summary>Returns the signed 16-bit integer payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public short AsInt16()
    {
        ThrowIfNullOrWrongKind(DataKind.Int16);
        return (short)_p0;
    }

    /// <summary>Returns the unsigned 16-bit integer payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public ushort AsUInt16()
    {
        ThrowIfNullOrWrongKind(DataKind.UInt16);
        return (ushort)_p0;
    }

    /// <summary>Returns the signed 32-bit integer payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public int AsInt32()
    {
        ThrowIfNullOrWrongKind(DataKind.Int32);
        return _p0;
    }

    /// <summary>Returns the unsigned 32-bit integer payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public uint AsUInt32()
    {
        ThrowIfNullOrWrongKind(DataKind.UInt32);
        return unchecked((uint)_p0);
    }

    /// <summary>Returns the signed 64-bit integer payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public long AsInt64()
    {
        ThrowIfNullOrWrongKind(DataKind.Int64);
        return ReadLong();
    }

    /// <summary>Returns the unsigned 64-bit integer payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public ulong AsUInt64()
    {
        ThrowIfNullOrWrongKind(DataKind.UInt64);
        return unchecked((ulong)ReadLong());
    }

    /// <summary>Returns the 64-bit double-precision floating-point payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public double AsFloat64()
    {
        ThrowIfNullOrWrongKind(DataKind.Float64);
        return BitConverter.Int64BitsToDouble(ReadLong());
    }

    /// <summary>Returns the 16-bit IEEE 754 binary16 floating-point payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public Half AsFloat16()
    {
        ThrowIfNullOrWrongKind(DataKind.Float16);
        return BitConverter.UInt16BitsToHalf((ushort)_p0);
    }

    /// <summary>Returns the 128-bit decimal floating-point payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public decimal AsDecimal()
    {
        ThrowIfNullOrWrongKind(DataKind.Decimal);
        return new decimal([_p0, _p1, _p2, _p3]);
    }

    /// <summary>Returns the 128-bit unsigned integer payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public UInt128 AsUInt128()
    {
        ThrowIfNullOrWrongKind(DataKind.UInt128);
        return Unsafe.As<int, UInt128>(ref Unsafe.AsRef(in _p0));
    }

    /// <summary>Returns the 128-bit signed integer payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public Int128 AsInt128()
    {
        ThrowIfNullOrWrongKind(DataKind.Int128);
        return Unsafe.As<int, Int128>(ref Unsafe.AsRef(in _p0));
    }

    // ─────────────────────── Widening numeric conversions ───────────────────────

    /// <summary>
    /// Returns <see langword="true"/> when this value's <see cref="Kind"/> is any integer,
    /// floating-point, or boolean scalar that can be widened to <see cref="float"/> or <see cref="double"/>.
    /// Boolean values are treated as 1 (true) and 0 (false).
    /// </summary>
    public bool IsNumericScalar => IsNumericScalarKind(_kind);

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="kind"/> is any integer,
    /// floating-point, or boolean scalar that can be converted to a numeric value.
    /// </summary>
    public static bool IsNumericScalarKind(DataKind kind) =>
        kind is DataKind.Float16 or DataKind.Float32 or DataKind.Float64 or DataKind.Decimal
            or DataKind.Int8 or DataKind.Int16 or DataKind.Int32 or DataKind.Int64 or DataKind.Int128
            or DataKind.UInt8 or DataKind.UInt16 or DataKind.UInt32 or DataKind.UInt64 or DataKind.UInt128
            or DataKind.Boolean;

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="kind"/> is any integer type
    /// (signed or unsigned). Excludes floating-point and boolean kinds.
    /// Use for function parameters that are logically integer (positions, counts, indices).
    /// </summary>
    public static bool IsIntegerKind(DataKind kind) =>
        kind is DataKind.Int8 or DataKind.Int16 or DataKind.Int32 or DataKind.Int64
            or DataKind.UInt8 or DataKind.UInt16 or DataKind.UInt32 or DataKind.UInt64;

    /// <summary>
    /// Widens any numeric scalar value to <see cref="float"/>.
    /// Int64/UInt64 values may lose precision beyond 2^24.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value is null or not a numeric scalar kind.</exception>
    public float ToFloat()
    {
        if (TryToFloat(out float result)) return result;
        throw new InvalidOperationException($"Cannot convert {_kind} to float.");
    }

    /// <summary>
    /// Widens any numeric scalar value to <see cref="double"/>.
    /// UInt64 values may lose precision beyond 2^53.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value is null or not a numeric scalar kind.</exception>
    public double ToDouble()
    {
        if (TryToDouble(out double result)) return result;
        throw new InvalidOperationException($"Cannot convert {_kind} to double.");
    }

    /// <summary>
    /// Converts any numeric scalar value to <see cref="int"/>.
    /// Floating-point values are truncated. Values outside the int range overflow silently.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value is null or not a numeric scalar kind.</exception>
    public int ToInt32()
    {
        if (TryToInt32(out int result)) return result;
        throw new InvalidOperationException($"Cannot convert {_kind} to int.");
    }

    /// <summary>
    /// Converts any numeric scalar value to <see cref="long"/>.
    /// Floating-point values are truncated. UInt64 values beyond <see cref="long.MaxValue"/> overflow silently.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value is null or not a numeric scalar kind.</exception>
    public long ToInt64()
    {
        if (TryToInt64(out long result)) return result;
        throw new InvalidOperationException($"Cannot convert {_kind} to long.");
    }

    /// <summary>Attempts to widen this value to <see cref="float"/>. Returns <see langword="false"/> for non-numeric kinds or null values.</summary>
    public bool TryToFloat(out float result)
    {
        if (IsNull) { result = default; return false; }
        switch (_kind)
        {
            case DataKind.Float32: result = AsFloat32(); return true;
            case DataKind.Float64: result = (float)AsFloat64(); return true;
            case DataKind.UInt8:   result = AsUInt8(); return true;
            case DataKind.Int8:    result = AsInt8(); return true;
            case DataKind.Int16:   result = AsInt16(); return true;
            case DataKind.UInt16:  result = AsUInt16(); return true;
            case DataKind.Int32:   result = AsInt32(); return true;
            case DataKind.UInt32:  result = AsUInt32(); return true;
            case DataKind.Int64:   result = AsInt64(); return true;
            case DataKind.UInt64:  result = AsUInt64(); return true;
            case DataKind.Int128:  result = (float)AsInt128(); return true;
            case DataKind.UInt128: result = (float)AsUInt128(); return true;
            case DataKind.Float16: result = (float)AsFloat16(); return true;
            case DataKind.Decimal: result = (float)AsDecimal(); return true;
            case DataKind.Boolean: result = AsBoolean() ? 1f : 0f; return true;
            default: result = default; return false;
        }
    }

    /// <summary>Attempts to widen this value to <see cref="double"/>. Returns <see langword="false"/> for non-numeric kinds or null values.</summary>
    public bool TryToDouble(out double result)
    {
        if (IsNull) { result = default; return false; }
        switch (_kind)
        {
            case DataKind.Float32: result = AsFloat32(); return true;
            case DataKind.Float64: result = AsFloat64(); return true;
            case DataKind.UInt8:   result = AsUInt8(); return true;
            case DataKind.Int8:    result = AsInt8(); return true;
            case DataKind.Int16:   result = AsInt16(); return true;
            case DataKind.UInt16:  result = AsUInt16(); return true;
            case DataKind.Int32:   result = AsInt32(); return true;
            case DataKind.UInt32:  result = AsUInt32(); return true;
            case DataKind.Int64:   result = AsInt64(); return true;
            case DataKind.UInt64:  result = (double)AsUInt64(); return true;
            case DataKind.Int128:  result = (double)AsInt128(); return true;
            case DataKind.UInt128: result = (double)AsUInt128(); return true;
            case DataKind.Float16: result = (double)AsFloat16(); return true;
            case DataKind.Decimal: result = (double)AsDecimal(); return true;
            case DataKind.Boolean: result = AsBoolean() ? 1.0 : 0.0; return true;
            default: result = default; return false;
        }
    }

    /// <summary>Attempts to convert this value to <see cref="int"/>. Returns <see langword="false"/> for non-numeric kinds or null values.</summary>
    public bool TryToInt32(out int result)
    {
        if (IsNull) { result = default; return false; }
        switch (_kind)
        {
            case DataKind.Float32: result = (int)AsFloat32(); return true;
            case DataKind.Float64: result = (int)AsFloat64(); return true;
            case DataKind.UInt8:   result = AsUInt8(); return true;
            case DataKind.Int8:    result = AsInt8(); return true;
            case DataKind.Int16:   result = AsInt16(); return true;
            case DataKind.UInt16:  result = AsUInt16(); return true;
            case DataKind.Int32:   result = AsInt32(); return true;
            case DataKind.UInt32:  result = (int)AsUInt32(); return true;
            case DataKind.Int64:   result = (int)AsInt64(); return true;
            case DataKind.UInt64:  result = (int)AsUInt64(); return true;
            case DataKind.Int128:  result = (int)AsInt128(); return true;
            case DataKind.UInt128: result = (int)AsUInt128(); return true;
            case DataKind.Float16: result = (int)AsFloat16(); return true;
            case DataKind.Decimal: result = (int)AsDecimal(); return true;
            case DataKind.Boolean: result = AsBoolean() ? 1 : 0; return true;
            default: result = default; return false;
        }
    }

    /// <summary>Attempts to convert this value to <see cref="long"/>. Returns <see langword="false"/> for non-numeric kinds or null values.</summary>
    public bool TryToInt64(out long result)
    {
        if (IsNull) { result = default; return false; }
        switch (_kind)
        {
            case DataKind.Float32: result = (long)AsFloat32(); return true;
            case DataKind.Float64: result = (long)AsFloat64(); return true;
            case DataKind.UInt8:   result = AsUInt8(); return true;
            case DataKind.Int8:    result = AsInt8(); return true;
            case DataKind.Int16:   result = AsInt16(); return true;
            case DataKind.UInt16:  result = AsUInt16(); return true;
            case DataKind.Int32:   result = AsInt32(); return true;
            case DataKind.UInt32:  result = AsUInt32(); return true;
            case DataKind.Int64:   result = AsInt64(); return true;
            case DataKind.UInt64:  result = (long)AsUInt64(); return true;
            case DataKind.Int128:  result = (long)AsInt128(); return true;
            case DataKind.UInt128: result = (long)AsUInt128(); return true;
            case DataKind.Float16: result = (long)AsFloat16(); return true;
            case DataKind.Decimal: result = (long)AsDecimal(); return true;
            case DataKind.Boolean: result = AsBoolean() ? 1L : 0L; return true;
            default: result = default; return false;
        }
    }

    // ─────────────────────── Date/time widening ───────────────────────────────

    /// <summary>
    /// Converts a <see cref="DataKind.Date"/> or <see cref="DataKind.DateTime"/>
    /// value to <see cref="DateTimeOffset"/>. Date values become midnight UTC.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The value is null or its kind is neither Date nor DateTime.
    /// </exception>
    public DateTimeOffset ToDateTimeOffset()
    {
        if (IsNull) throw new InvalidOperationException("Cannot convert a null DataValue to DateTimeOffset.");
        return _kind switch
        {
            DataKind.Date => new DateTimeOffset(AsDate().ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            DataKind.DateTime => AsDateTime(),
            _ => throw new InvalidOperationException(
                $"Cannot convert DataKind.{_kind} to DateTimeOffset. Expected Date or DateTime."),
        };
    }

    // ─────────────────────── Object boxing ─────────────────────────────────────

    /// <summary>
    /// Returns the value as its natural boxed CLR type. Useful for JSON serialization
    /// and other contexts where the typed object is needed rather than a string.
    /// </summary>
    /// <returns>
    /// The boxed primitive (<see cref="float"/>, <see cref="int"/>, <see cref="bool"/>, etc.),
    /// the reference-type payload (<see cref="string"/>, <c>float[]</c>, <c>byte[]</c>, etc.),
    /// or <see langword="null"/> when <see cref="IsNull"/> is true.
    /// Composite types (<see cref="DataKind.Array"/>, <see cref="DataKind.Struct"/>) return
    /// the raw <c>DataValue[]</c> — callers that need recursive conversion should handle
    /// those kinds explicitly.
    /// </returns>
    public object? ToObject()
    {
        if (IsNull) return null;

        return _kind switch
        {
            DataKind.Float32   => AsFloat32(),
            DataKind.Float64   => AsFloat64(),
            DataKind.UInt8     => AsUInt8(),
            DataKind.Int8      => AsInt8(),
            DataKind.Int16     => AsInt16(),
            DataKind.UInt16    => AsUInt16(),
            DataKind.Int32     => AsInt32(),
            DataKind.UInt32    => AsUInt32(),
            DataKind.Int64     => AsInt64(),
            DataKind.UInt64    => AsUInt64(),
            DataKind.Int128    => AsInt128(),
            DataKind.UInt128   => AsUInt128(),
            DataKind.Decimal   => AsDecimal(),
            DataKind.Boolean   => AsBoolean(),
            DataKind.Date      => AsDate(),
            DataKind.DateTime  => AsDateTime(),
            DataKind.Time      => AsTime(),
            DataKind.Duration  => AsDuration(),
            DataKind.Uuid      => AsUuid(),
            // Reference types require a store — return the ToString() summary without content.
            _ => ToString(),
        };
    }

    // ─────────────────────── Display formatting ───────────────────────────────

    /// <summary>
    /// Returns a human-readable string representation of this value suitable for
    /// display in tables, logs, and diagnostics.
    /// </summary>
    /// <param name="converter">
    /// Optional per-kind override. When supplied, the delegate is called first with the
    /// value's <see cref="DataKind"/>. If it returns <c>(true, result)</c> the result is
    /// used directly; if it returns <c>(false, _)</c> the canonical formatting applies.
    /// This lets callers customise specific kinds (e.g. numeric precision, date format)
    /// while inheriting default formatting for everything else.
    /// </param>
    /// <returns>
    /// A formatted string, or <c>"NULL"</c> when <see cref="IsNull"/> is true.
    /// </returns>
    public string ToDisplayString(Func<DataValue, (bool Handled, string? Result)>? converter = null)
    {
        if (IsNull) return "NULL";

        if (converter is not null)
        {
            (bool handled, string? result) = converter(this);
            if (handled) return result ?? "NULL";
        }

        return _kind switch
        {
            DataKind.Float32  => AsFloat32().ToString("G"),
            DataKind.Float64  => AsFloat64().ToString("G"),
            DataKind.UInt8    => AsUInt8().ToString(),
            DataKind.Int8     => AsInt8().ToString(),
            DataKind.Int16    => AsInt16().ToString(),
            DataKind.UInt16   => AsUInt16().ToString(),
            DataKind.Int32    => AsInt32().ToString(),
            DataKind.UInt32   => AsUInt32().ToString(),
            DataKind.Int64    => AsInt64().ToString(),
            DataKind.UInt64   => AsUInt64().ToString(),
            DataKind.Int128   => AsInt128().ToString(System.Globalization.CultureInfo.InvariantCulture),
            DataKind.UInt128  => AsUInt128().ToString(System.Globalization.CultureInfo.InvariantCulture),
            DataKind.Decimal  => AsDecimal().ToString(System.Globalization.CultureInfo.InvariantCulture),
            DataKind.Float16  => AsFloat16().ToString("G"),
            DataKind.Boolean  => AsBoolean() ? "true" : "false",
            DataKind.Date     => AsDate().ToString("yyyy-MM-dd"),
            DataKind.DateTime => AsDateTime().ToString("O"),
            DataKind.Time     => AsTime().ToString("HH:mm:ss.FFFFFFF"),
            DataKind.Duration => AsDuration().ToString("c"),
            DataKind.Uuid     => AsUuid().ToString("D"),
            DataKind.Type     => AsType().ToString(),
            // Reference types require a store — return ToString() summary without content.
            _ => ToString() ?? _kind.ToString(),
        };
    }

    // Format*Display helpers removed — ToDisplayString falls back to ToString() for reference types.

    // ─────────────────────── Reference-type accessors ─────────────────────────

    /// <summary>
    /// Returns the byte array payload. For arena-backed values, reads from
    /// <paramref name="store"/>; for sidecar-backed values, looks up the source in
    /// <paramref name="registry"/> by the value's <c>storeId</c> and reads from there.
    /// The flag on the DataValue determines which path runs.
    /// </summary>
    public byte[] AsUInt8Array(IValueStore store, SidecarRegistry? registry = null)
    {
        if (!IsByteArrayKind)
        {
            throw new InvalidOperationException(
                $"AsUInt8Array is only valid for byte arrays (UInt8 + IsArray); got {_kind}.");
        }
        if (IsNull)
        {
            throw new InvalidOperationException("Value is null.");
        }

        if (IsInSidecar)
        {
            return ReadSidecarBytes(registry).ToArray();
        }
        return store.RetrieveBytes(_p0, _p1);
    }

    /// <summary>
    /// True when this value carries a byte-array payload — i.e.
    /// <see cref="DataKind.UInt8"/> with the <see cref="DataValueFlags.IsArray"/>
    /// flag set. Used to dispatch byte-content paths
    /// (<see cref="AsByteSpan"/>, <see cref="AsUInt8Array(IValueStore, SidecarRegistry?)"/>)
    /// without enumerating the kind in every call site.
    /// </summary>
    public bool IsByteArrayKind =>
        _kind == DataKind.UInt8 && (_flags & DataValueFlags.IsArray) != 0;

    /// <summary>
    /// Returns the byte payload for a byte-array (UInt8 + IsArray) or
    /// <see cref="DataKind.Image"/> value as a <see cref="ReadOnlySpan{T}"/>, without
    /// materializing a managed <c>byte[]</c>. Zero-allocation hot-path reader.
    /// </summary>
    /// <remarks>
    /// For arena-backed values the span is valid only while <paramref name="store"/>'s
    /// backing arena is alive; for sidecar-backed values the span lives as long as the
    /// resolved <see cref="IBlobSource"/>'s mmap view does. Callers must consume the
    /// span before whichever store backs it goes away.
    /// </remarks>
    public ReadOnlySpan<byte> AsByteSpan(IValueStore store, SidecarRegistry? registry = null)
    {
        // Image and (UInt8 + IsArray) both carry byte content at (_p0, _p1) — read path is identical.
        if (_kind != DataKind.Image && !IsByteArrayKind)
        {
            throw new InvalidOperationException(
                $"AsByteSpan is only valid for byte-content kinds (Image or UInt8 + IsArray); got {_kind}.");
        }

        if (IsInSidecar)
        {
            return ReadSidecarBytes(registry);
        }
        return store.RetrieveUtf8Span(_p0, _p1);
    }

    /// <summary>
    /// Resolves a sidecar-backed byte payload by looking up the value's
    /// <see cref="SidecarStoreId"/> in <paramref name="registry"/>. Throws when no
    /// registry is supplied or the storeId isn't registered — sidecar-backed
    /// DataValues cannot be read against an arena alone.
    /// </summary>
    private ReadOnlySpan<byte> ReadSidecarBytes(SidecarRegistry? registry)
    {
        if (registry is null)
        {
            throw new InvalidOperationException(
                "DataValue is sidecar-backed (DataValueFlags.InSidecar) but no SidecarRegistry was provided. " +
                "Pass the ExecutionContext's registry (or the frame's) so the storeId can resolve.");
        }

        IBlobSource? source = registry.Resolve(SidecarStoreId)
            ?? throw new InvalidOperationException(
                $"Sidecar storeId {SidecarStoreId} is not registered in the supplied " +
                "SidecarRegistry. The DataValue references a sidecar that wasn't opened by " +
                "this query — likely the table provider didn't register its IBlobSource.");

        return source.Read(SidecarOffset, SidecarLength);
    }

    /// <summary>Returns the text string payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    /// <remarks>
    /// Works for inline strings (whose bytes are self-contained in the struct) without a store.
    /// For non-inline strings (reference-store or arena-backed), use
    /// <see cref="AsString(IValueStore)"/> or <see cref="AsString(Arena)"/> — those require an
    /// explicit store to resolve the payload.
    /// </remarks>
    public string AsString()
    {
        ThrowIfNullOrWrongKind(DataKind.String);
        if (IsInline) return System.Text.Encoding.UTF8.GetString(InlineUtf8Span);
        throw new InvalidOperationException(
            "AsString() without a store only supports inline strings. For non-inline strings, " +
            "use AsString(IValueStore) or AsString(Arena).");
    }

    /// <summary>Returns the text string payload from an explicit <see cref="IValueStore"/>.</summary>
    /// <remarks>
    /// Does not handle sidecar-backed values — for those use the
    /// <see cref="AsString(IValueStore, SidecarRegistry)"/> overload that
    /// accepts a registry. This method throws with a helpful pointer when
    /// it encounters one.
    /// </remarks>
    public string AsString(IValueStore store)
    {
        ThrowIfNullOrWrongKind(DataKind.String);
        if (IsInline) return System.Text.Encoding.UTF8.GetString(InlineUtf8Span);
        if (IsInSidecar)
        {
            throw new InvalidOperationException(
                "AsString(store) cannot resolve a sidecar-backed String. Use the " +
                "AsString(store, registry) overload — it routes sidecar values through " +
                "the SidecarRegistry and inline / arena values through the store.");
        }
        return store.RetrieveString(_p0, _p1);
    }

    /// <summary>
    /// Returns the text string payload, resolving inline / arena / sidecar
    /// storage tiers uniformly. Inline values come from the struct itself,
    /// arena-backed values come from <paramref name="store"/>, and
    /// sidecar-backed values come from <paramref name="registry"/> via the
    /// value's recorded <c>storeId</c>.
    /// </summary>
    /// <param name="store">Used for arena-backed values; ignored for inline / sidecar.</param>
    /// <param name="registry">Required for sidecar-backed values; ignored for inline / arena.</param>
    public string AsString(IValueStore store, SidecarRegistry? registry)
    {
        ThrowIfNullOrWrongKind(DataKind.String);
        if (IsInline) return System.Text.Encoding.UTF8.GetString(InlineUtf8Span);
        if (IsInSidecar) return System.Text.Encoding.UTF8.GetString(ReadSidecarBytes(registry));
        return store.RetrieveString(_p0, _p1);
    }

    /// <summary>
    /// Returns the character count of a string or JSON value without accessing the store.
    /// Cached in <c>_p2</c> at creation time for values built from a managed <see cref="string"/>.
    /// For arena-slice values where <c>_p2</c> is 0, falls back to UTF-8 decode counting
    /// via <paramref name="store"/>.
    /// </summary>
    /// <param name="store">Fallback store for arena-slice values where the char count was not cached.</param>
    /// <returns>The number of <see cref="char"/> units in the string.</returns>
    public int StringCharCount(IValueStore store)
    {
        ThrowIfNullOrWrongKind(DataKind.String);
        // Inline: char count is cached in the high byte of _charCount at construction.
        if (IsInline) return InlineCharCount;
        // _charCount holds the full char count (ushort). 65535 = overflow sentinel → fall back to decode.
        // 0 = unknown (e.g. FromStringSlice) → also fall back.
        return _charCount is not 0 and not ushort.MaxValue
            ? _charCount
            : System.Text.Encoding.UTF8.GetCharCount(store.RetrieveUtf8Span(_p0, _p1));
    }

    /// <summary>
    /// Returns the cached char count as-is, without falling back to a UTF-8 decode.
    /// Returns <c>0</c> for values that never cached the count (e.g. <see cref="FromStringSlice"/>),
    /// and <c>65535</c> for strings that overflowed the 16-bit cache.
    /// </summary>
    /// <remarks>
    /// Zero-allocation hot-path reader for callers that only need a coarse "how big is this string"
    /// signal (e.g. the auto-indexing size threshold). Callers that need exact lengths for strings
    /// near or above 65535 chars should use <see cref="StringCharCount(IValueStore)"/> instead.
    /// </remarks>
    public int RawCharCount => IsInline ? InlineCharCount : _charCount;

    /// <summary>
    /// Returns the byte length of this value's content as stored in process memory
    /// (inline payload or arena-backed). Returns <c>0</c> for null, sidecar-backed
    /// values (whose bytes live on disk — see <see cref="SidecarByteLength"/>), and
    /// inline scalars whose bytes are entirely inside the <see cref="DataValue"/>
    /// struct itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Zero-allocation hot-path reader: every kind reads its byte count from the
    /// inline payload words (<c>_p1</c>/<c>_p2</c>) or from <c>InlineByteLength</c>,
    /// without materializing the value through an <see cref="IValueStore"/>. Use
    /// this for two-pass encoders sizing pooled buffers, memory-budget estimation,
    /// and any other "how big is this payload" query.
    /// </para>
    /// <para>
    /// Per-kind layout:
    /// </para>
    /// <list type="bullet">
    ///   <item><see cref="DataKind.String"/>:
    ///     UTF-8 byte length (<see cref="InlineByteLength"/> when inline; <c>_p1</c> when arena-backed).</item>
    ///   <item>Any typed array (Kind + IsArray) and <see cref="DataKind.Image"/>:
    ///     byte length (<see cref="InlineByteLength"/> when inline; <c>_p1</c> when arena-backed).</item>
    ///   <item>Inline arrays (<see cref="IsInlineArray"/>): element count × element size.</item>
    ///   <item>All other kinds (inline scalars, null, sidecar): <c>0</c>.</item>
    /// </list>
    /// </remarks>
    public int ContentByteLength
    {
        get
        {
            if (IsNull || IsInSidecar) return 0;

            if (IsInline)
            {
                if (IsInlineArray) return InlineArrayElementCount * ElementByteSize(_kind);
                if (_kind == DataKind.String) return InlineByteLength;
                return 0;
            }

            // Arena-backed.
            if (IsArray || _kind == DataKind.Image) return _p1;
            return _kind switch
            {
                DataKind.String => _p1,
                _ => 0,
            };
        }
    }

    /// <summary>
    /// Returns the byte length of this value's payload in a <c>.datum-blob</c>
    /// sidecar, or <c>null</c> when the value is not sidecar-backed. The length
    /// is a 40-bit value (max ~1 TiB) packed across <c>_p2</c> and the low byte
    /// of <c>_p3</c>; see <see cref="BuildSidecar"/> for the layout.
    /// </summary>
    /// <remarks>
    /// Pairs with <see cref="ContentByteLength"/>: that returns in-memory bytes
    /// (zero for sidecar values), this returns on-disk bytes (null for non-sidecar
    /// values). Encoders that need total content size compute
    /// <c>ContentByteLength + (SidecarByteLength ?? 0)</c>.
    /// </remarks>
    public long? SidecarByteLength => IsInSidecar ? SidecarLength : null;

    /// <summary>
    /// Returns the cached XxHash64 of the string's UTF-8 bytes as a single ulong,
    /// or <c>0</c> for values with no cached hash (e.g. arena-slice strings built
    /// via <see cref="FromStringSlice"/>).
    /// </summary>
    /// <remarks>
    /// Zero-allocation hot-path reader for frequency-sketch accumulators that need
    /// a content-addressed hash key without materializing the string. When the
    /// cached hash is absent, callers must fall back to hashing the UTF-8 bytes
    /// through an <see cref="IValueStore"/>.
    /// </remarks>
    public ulong RawContentHash
    {
        get
        {
            // Inline strings don't cache a hash — _p2 and _p3 hold payload bytes, not hash bits.
            // XxHash64 over a <=16-byte span is a single stripe, effectively free.
            if (IsInline) return XxHash64.HashToUInt64(InlineUtf8Span);
            return (uint)_p2 | ((ulong)(uint)_p3 << 32);
        }
    }

    /// <summary>
    /// Returns the element count for arena-backed typed-array values without
    /// allocating. Returns the byte count for byte arrays (1 element = 1 byte) and
    /// derives element count from byte count for wider element kinds via
    /// <see cref="ElementByteSize"/>.
    /// </summary>
    /// <returns>The element count, or -1 when not derivable inline (non-array, sidecar, etc.).</returns>
    public int ElementCount
    {
        get
        {
            if (IsByteArrayKind) return _p1;
            if (IsArray && (_flags & DataValueFlags.InArena) != 0)
            {
                int elementSize = ElementByteSize(_kind);
                return elementSize > 0 ? _p1 / elementSize : -1;
            }
            return -1;
        }
    }

    /// <summary>
    /// Returns the raw UTF-8 bytes for a string value without allocating a managed
    /// <see cref="string"/>. For <see cref="Arena"/>-backed stores this is a zero-copy
    /// slice of the backing buffer. Ideal for hashing, equality checks, serialization,
    /// and byte-level operations.
    /// </summary>
    /// <param name="store">The value store that owns the string data.</param>
    /// <returns>A span of UTF-8 bytes. Valid only while the store is alive.</returns>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public ReadOnlySpan<byte> AsUtf8Span(IValueStore store)
    {
        ThrowIfNullOrWrongKind(DataKind.String);
        if (IsInline) return InlineUtf8Span;
        if (IsInSidecar)
        {
            throw new InvalidOperationException(
                "AsUtf8Span(store) cannot resolve a sidecar-backed String. Use the " +
                "AsUtf8Span(store, registry) overload that routes through the SidecarRegistry.");
        }
        return store.RetrieveUtf8Span(_p0, _p1);
    }

    /// <summary>
    /// Returns the UTF-8 byte span, resolving inline / arena / sidecar
    /// storage tiers uniformly. Inline values come from the struct,
    /// arena-backed values come from <paramref name="store"/>, and
    /// sidecar-backed values come from <paramref name="registry"/>.
    /// </summary>
    public ReadOnlySpan<byte> AsUtf8Span(IValueStore store, SidecarRegistry? registry)
    {
        ThrowIfNullOrWrongKind(DataKind.String);
        if (IsInline) return InlineUtf8Span;
        if (IsInSidecar) return ReadSidecarBytes(registry);
        return store.RetrieveUtf8Span(_p0, _p1);
    }

    /// <summary>
    /// Decodes the string value into a <see cref="ReadOnlySpan{T}"/> of <see cref="char"/>
    /// without allocating a managed <see cref="string"/>. The caller must return the
    /// rented buffer to <see cref="System.Buffers.ArrayPool{T}.Shared"/> after use.
    /// </summary>
    /// <param name="store">The value store that owns the string data.</param>
    /// <param name="rentedBuffer">
    /// Receives the rented char buffer. The caller must return it via
    /// <c>ArrayPool&lt;char&gt;.Shared.Return(rentedBuffer)</c> after consuming the span.
    /// </param>
    /// <returns>A span of chars backed by <paramref name="rentedBuffer"/>.</returns>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public ReadOnlySpan<char> AsStringSpan(IValueStore store, out char[] rentedBuffer)
    {
        ThrowIfNullOrWrongKind(DataKind.String);
        ReadOnlySpan<byte> utf8 = AsUtf8Span(store);
        int maxChars = System.Text.Encoding.UTF8.GetMaxCharCount(utf8.Length);
        rentedBuffer = System.Buffers.ArrayPool<char>.Shared.Rent(maxChars);
        int charCount = System.Text.Encoding.UTF8.GetChars(utf8, rentedBuffer);
        return rentedBuffer.AsSpan(0, charCount);
    }

    /// <summary>
    /// Returns the text string payload, resolving arena-backed values from the
    /// given <see cref="Arena"/>.  Falls back to the reference store for
    /// non-arena values.
    /// </summary>
    /// <param name="arena">The arena that owns the UTF-8 bytes.</param>
    /// <returns>The decoded string.</returns>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public string AsString(Arena arena)
    {
        ThrowIfNullOrWrongKind(DataKind.String);
        if (IsInline) return System.Text.Encoding.UTF8.GetString(InlineUtf8Span);
        return arena.GetString(_p0, _p1);
    }

    /// <summary>
    /// Returns the raw UTF-8 bytes for an arena-backed string without allocating.
    /// </summary>
    /// <param name="arena">The arena that owns the UTF-8 bytes.</param>
    /// <returns>A span of UTF-8 bytes.  Valid only while the arena is alive.</returns>
    /// <exception cref="InvalidOperationException">Wrong kind, null, or not arena-backed.</exception>
    public ReadOnlySpan<byte> GetArenaStringSpan(Arena arena)
    {
        ThrowIfNullOrWrongKind(DataKind.String);
        return arena.GetSpan(_p0, _p1);
    }


    /// <summary>
    /// Returns the encoded image byte array payload.
    /// </summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    /// <remarks>Obsolete: ReferenceStore has been removed. Use <see cref="AsImage(IValueStore, SidecarRegistry?)"/> instead.</remarks>
    public byte[] AsImage()
    {
        ThrowIfNullOrWrongKind(DataKind.Image);
        throw new InvalidOperationException("Use AsImage(store). ReferenceStore is no longer available.");
    }

    /// <summary>
    /// Returns the encoded image byte array. For arena-backed values, reads from
    /// <paramref name="store"/>; for sidecar-backed values, looks up the value's
    /// <c>storeId</c> in <paramref name="registry"/> to find its
    /// <see cref="IBlobSource"/>. The flag on the DataValue determines which path runs.
    /// </summary>
    public byte[] AsImage(IValueStore store, SidecarRegistry? registry = null)
    {
        ThrowIfNullOrWrongKind(DataKind.Image);

        if (IsInSidecar)
        {
            return ReadSidecarBytes(registry).ToArray();
        }
        return store.RetrieveBytes(_p0, _p1);
    }

    /// <summary>Returns the calendar date payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public DateOnly AsDate()
    {
        ThrowIfNullOrWrongKind(DataKind.Date);
        return DateOnly.FromDayNumber(_p0);
    }

    /// <summary>Returns the date and time payload with UTC offset.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public DateTimeOffset AsDateTime()
    {
        ThrowIfNullOrWrongKind(DataKind.DateTime);
        return new DateTimeOffset(ReadLong(), new TimeSpan((long)_p2 * TimeSpan.TicksPerMinute));
    }

    /// <summary>Returns the UUID payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public Guid AsUuid()
    {
        ThrowIfNullOrWrongKind(DataKind.Uuid);
        return Unsafe.As<int, Guid>(ref Unsafe.AsRef(in _p0));
    }

    /// <summary>Returns the boolean payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public bool AsBoolean()
    {
        ThrowIfNullOrWrongKind(DataKind.Boolean);
        return _p0 != 0;
    }

    /// <summary>Returns the time-of-day payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public TimeOnly AsTime()
    {
        ThrowIfNullOrWrongKind(DataKind.Time);
        return new TimeOnly(ReadLong());
    }

    /// <summary>Returns the duration payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public TimeSpan AsDuration()
    {
        ThrowIfNullOrWrongKind(DataKind.Duration);
        return new TimeSpan(ReadLong());
    }

    /// <summary>Returns the <see cref="DataKind"/> that this type tag describes.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public DataKind AsType()
    {
        ThrowIfNullOrWrongKind(DataKind.Type);
        return (DataKind)(byte)_p0;
    }

    /// <summary>Returns the typed array payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    /// <remarks>Obsolete: ReferenceStore has been removed. Use <see cref="AsArray(IValueStore)"/> instead.</remarks>
    public DataValue[] AsArray()
    {
        ThrowIfNullOrWrongKind(DataKind.Array);
        throw new InvalidOperationException("Use AsArray(store). ReferenceStore is no longer available.");
    }

    /// <summary>Returns the typed array payload from an explicit <see cref="IValueStore"/>.</summary>
    public DataValue[] AsArray(IValueStore store)
    {
        ThrowIfNullOrWrongKind(DataKind.Array);
        return store.RetrieveDataValues(_p0, _p1);
    }

    /// <summary>
    /// Returns the element <see cref="DataKind"/> for an <see cref="DataKind.Array"/> value.
    /// Available on both null and non-null array values.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called on a non-array value.</exception>
    public DataKind ArrayElementKind
    {
        get
        {
            if (_kind != DataKind.Array)
            {
                throw new InvalidOperationException(
                    $"Cannot read ArrayElementKind on a {_kind} value.");
            }

            // Layout split: typed nulls (NullArray) store the element kind in _meta
            // because _p1 isn't carrying a length; non-null arrays (FromArray) store
            // it in _p2 because _meta would alias _p1's length low bytes.
            return IsNull ? (DataKind)_meta : (DataKind)_p2;
        }
    }

    /// <summary>Returns the positional field-value array for a struct value.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    /// <remarks>Obsolete: ReferenceStore has been removed. Use <see cref="AsStruct(IValueStore)"/> instead.</remarks>
    public DataValue[] AsStruct()
    {
        ThrowIfNullOrWrongKind(DataKind.Struct);
        throw new InvalidOperationException("Use AsStruct(store). ReferenceStore is no longer available.");
    }

    /// <summary>Returns the positional field-value array from an explicit <see cref="IValueStore"/>.</summary>
    public DataValue[] AsStruct(IValueStore store)
    {
        ThrowIfNullOrWrongKind(DataKind.Struct);
        return store.RetrieveDataValues(_p0, _p1);
    }

    /// <summary>
    /// Returns the declared field count for a <see cref="DataKind.Struct"/> value.
    /// Available on both null and non-null struct values.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called on a non-struct value.</exception>
    public short StructFieldCount
    {
        get
        {
            if (_kind != DataKind.Struct)
            {
                throw new InvalidOperationException(
                    $"Cannot read StructFieldCount on a {_kind} value.");
            }

            return _meta;
        }
    }

    // ───────────────────────── Equality ─────────────────────────

    /// <inheritdoc/>
    public override bool Equals(object? other) => other is DataValue dv && Equals(dv);

    /// <inheritdoc/>
    public bool Equals(DataValue other)
    {
        if (_kind != other._kind) return false;
        if (IsNull && other.IsNull) return true;
        if (IsNull != other.IsNull) return false;

        // Byte arrays (UInt8 + IsArray) use offset-equality on (_p0, _p1) like other
        // arena-backed reference types — must come before the scalar UInt8 arm below.
        if (IsByteArrayKind)
        {
            return other.IsByteArrayKind && _p0 == other._p0 && _p1 == other._p1;
        }

        return _kind switch
        {
            // Unknown sentinel: no payload, so non-null Unknown values are always equal.
            DataKind.Unknown => true,

            // Small integer types: compare _p0 directly (no -0 ambiguity for integers).
            DataKind.UInt8 or DataKind.Int8 or DataKind.Int16 or DataKind.UInt16
            or DataKind.Int32 or DataKind.UInt32 or DataKind.Boolean or DataKind.Date
            or DataKind.Type
                => _p0 == other._p0,

            // 64-bit integer/temporal types: compare _p0 + _p1.
            DataKind.Int64 or DataKind.UInt64 or DataKind.Time or DataKind.Duration
                => _p0 == other._p0 && _p1 == other._p1,

            // Float types: recover the actual float so IEEE semantics (NaN != NaN, -0 == 0) are preserved.
            DataKind.Float16
                => BitConverter.UInt16BitsToHalf((ushort)_p0) == BitConverter.UInt16BitsToHalf((ushort)other._p0),
            DataKind.Float32
                => BitConverter.Int32BitsToSingle(_p0) == BitConverter.Int32BitsToSingle(other._p0),
            DataKind.Float64
                => BitConverter.Int64BitsToDouble(ReadLong()) == BitConverter.Int64BitsToDouble(other.ReadLong()),

            // Decimal: 16 bytes inline; bit-equality is value-equality (decimal has no -0/NaN).
            DataKind.Decimal
                => _p0 == other._p0 && _p1 == other._p1 && _p2 == other._p2 && _p3 == other._p3,

            // 128-bit integers: 16 bytes inline, bit-equality = value-equality.
            DataKind.UInt128 or DataKind.Int128
                => _p0 == other._p0 && _p1 == other._p1 && _p2 == other._p2 && _p3 == other._p3,

            // Reference types:
            DataKind.String
                => CompareStrings(in this, in other),
            DataKind.Uuid
                => _p0 == other._p0 && _p1 == other._p1 && _p2 == other._p2 && _p3 == other._p3,
            DataKind.DateTime
                => _p0 == other._p0 && _p1 == other._p1 && _p2 == other._p2,
            // For reference types without a store, use offset-equality: same (_p0,_p1) in the
            // same store means identical content. Different offsets → unknown, return false.
            DataKind.Image
                => _p0 == other._p0 && _p1 == other._p1,
            DataKind.Array
                => _meta == other._meta && _p0 == other._p0 && _p1 == other._p1,
            DataKind.Struct
                => _meta == other._meta && _p0 == other._p0 && _p1 == other._p1,
            _ => false,
        };
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        if (IsNull) return HashCode.Combine(_kind, true);

        // Byte arrays (UInt8 + IsArray) hash on (_p0, _p1) — must come before the
        // scalar UInt8 arm below.
        if (IsByteArrayKind)
        {
            return HashCode.Combine(_kind, true, _p0, _p1);
        }

        return _kind switch
        {
            // Small integer types: hash _p0 directly.
            DataKind.UInt8 or DataKind.Int8 or DataKind.Int16 or DataKind.UInt16
            or DataKind.Int32 or DataKind.UInt32 or DataKind.Boolean or DataKind.Date
            or DataKind.Type
                => HashCode.Combine(_kind, _p0),

            // 64-bit integer/temporal types: hash _p0 + _p1.
            DataKind.Int64 or DataKind.UInt64 or DataKind.Time or DataKind.Duration
                => HashCode.Combine(_kind, _p0, _p1),

            // Float types: delegate to float/double GetHashCode so -0 == 0 hashing is preserved.
            DataKind.Float16
                => HashCode.Combine(_kind, BitConverter.UInt16BitsToHalf((ushort)_p0)),
            DataKind.Float32
                => HashCode.Combine(_kind, BitConverter.Int32BitsToSingle(_p0)),
            DataKind.Float64
                => HashCode.Combine(_kind, BitConverter.Int64BitsToDouble(ReadLong())),

            // Decimal: 16 bytes inline; combine all four ints.
            DataKind.Decimal
                => HashCode.Combine(_kind, _p0, _p1, _p2, _p3),

            // 128-bit integers: same — four-int hash.
            DataKind.UInt128 or DataKind.Int128
                => HashCode.Combine(_kind, _p0, _p1, _p2, _p3),

            // Reference types:
            // RawContentHash returns XxHash64-over-UTF-8 for both inline and cached-hash
            // values, so equal-content strings across the two modes hash to the same code —
            // matching CompareStrings' mixed-mode hash match.
            DataKind.String
                => RawContentHash != 0
                    ? HashCode.Combine(_kind, RawContentHash)
                    : ComputeStringHashCode(),
            DataKind.DateTime
                => HashCode.Combine(_kind, _p0, _p1, _p2),
            DataKind.Uuid
                => HashCode.Combine(_kind, _p0, _p1, _p2, _p3),
            // Offset-based hashing: consistent with offset-equality in Equals.
            DataKind.Image
                => HashCode.Combine(_kind, _p0, _p1),
            DataKind.Array
                => HashCode.Combine(_kind, _p0, _p1, _meta),
            DataKind.Struct
                => HashCode.Combine(_kind, _p0, _p1, _meta),
            _ => HashCode.Combine(_kind),
        };
    }

    /// <inheritdoc/>
    public static bool operator ==(DataValue left, DataValue right) => left.Equals(right);

    /// <inheritdoc/>
    public static bool operator !=(DataValue left, DataValue right) => !left.Equals(right);

    // ───────────────────────── Helpers ─────────────────────────

    /// <summary>
    /// Compares two string or JSON values, handling the case where one or both
    /// are arena-backed (no reference in the store). Arena-backed values from the
    /// same batch share offset/length identity; cross-arena comparison requires
    /// materialisation before calling <see cref="Equals(DataValue)"/>.
    /// </summary>
    private static bool CompareStrings(in DataValue left, in DataValue right)
    {
        bool leftInline = left.IsInline;
        bool rightInline = right.IsInline;

        // Both inline: bitwise compare payload + byte length (unused bytes are zeroed at construction).
        if (leftInline && rightInline)
        {
            return left._charCount == right._charCount
                && left._p0 == right._p0 && left._p1 == right._p1
                && left._p2 == right._p2 && left._p3 == right._p3;
        }

        // Mixed inline/non-inline: compare via RawContentHash. Inline computes XxHash64 over
        // its bytes; reference-store values return their cached XxHash64. Both use the same
        // hash function over UTF-8 bytes, so equal content hashes equal. Arena-slice values
        // return 0 for RawContentHash and cannot be compared here without a store.
        if (leftInline || rightInline)
        {
            ulong leftHash = left.RawContentHash;
            ulong rightHash = right.RawContentHash;
            return leftHash != 0 && rightHash != 0 && leftHash == rightHash;
        }

        // Both non-inline: existing offset + cached-hash comparison.
        // Fast path: same offset + same byte length → identical bytes in the same store.
        if (left._p0 == right._p0 && left._p1 == right._p1)
            return true;

        bool leftHasHash = (left._p2 | left._p3) != 0;
        bool rightHasHash = (right._p2 | right._p3) != 0;

        // Both have hashes: compare directly.
        if (leftHasHash && rightHasHash)
            return left._p2 == right._p2 && left._p3 == right._p3;

        // Mixed or neither has hash: without a store we cannot resolve content, return false.
        // Callers should use the store-based Equals overloads for cross-origin comparison.
        return false;
    }

    // CompareTensors removed — tensor equality now uses offset-equality in Equals().

    private void ThrowIfNullOrWrongKind(DataKind expected)
    {
        if ((_flags & DataValueFlags.IsNull) != 0)
        {
            throw new InvalidOperationException(
                $"Cannot read a null {_kind} value.");
        }

        if (_kind != expected)
        {
            throw new InvalidOperationException(
                $"Cannot read {_kind} as {expected}.");
        }
    }

    private static string FormatStructFields(DataValue[] fields)
    {
        return string.Join(", ", (IEnumerable<DataValue>)fields);
    }

    private static int CombineFloatArrayHash(DataKind kind, float[] data)
    {
        HashCode hash = new();
        hash.Add(kind);

        foreach (float element in data)
        {
            hash.Add(element);
        }

        return hash.ToHashCode();
    }

    private static int CombineFloatArrayHash(DataKind kind, float[] data, int rows, int columns)
    {
        HashCode hash = new();
        hash.Add(kind);
        hash.Add(rows);
        hash.Add(columns);

        foreach (float element in data)
        {
            hash.Add(element);
        }

        return hash.ToHashCode();
    }

    // CombineTensorHash removed — tensor hashing now uses offset-based approach in GetHashCode().

    private static int CombineByteArrayHash(DataKind kind, byte[] data)
    {
        HashCode hash = new();
        hash.Add(kind);

        foreach (byte element in data)
        {
            hash.Add(element);
        }

        return hash.ToHashCode();
    }

    private static int CombineArrayHash(DataKind kind, DataValue[] elements, short elementKindMeta)
    {
        HashCode hash = new();
        hash.Add(kind);
        hash.Add(elementKindMeta);

        foreach (DataValue element in elements)
        {
            hash.Add(element);
        }

        return hash.ToHashCode();
    }

    // ───────────────────────── Display ─────────────────────────

    /// <inheritdoc/>
    public override string ToString()
    {
        if (IsNull) return $"NULL({_kind})";

        if (IsByteArrayKind) return $"UInt8[{_p1} bytes]";

        return _kind switch
        {
            DataKind.Float32 => BitConverter.Int32BitsToSingle(_p0).ToString("G"),
            DataKind.UInt8 => ((byte)_p0).ToString(),
            DataKind.Int8 => ((sbyte)_p0).ToString(),
            DataKind.Int16 => ((short)_p0).ToString(),
            DataKind.UInt16 => ((ushort)_p0).ToString(),
            DataKind.Int32 => _p0.ToString(),
            DataKind.UInt32 => unchecked((uint)_p0).ToString(),
            DataKind.Int64 => ReadLong().ToString(),
            DataKind.UInt64 => unchecked((ulong)ReadLong()).ToString(),
            DataKind.Float64 => BitConverter.Int64BitsToDouble(ReadLong()).ToString("G"),
            DataKind.Float16 => BitConverter.UInt16BitsToHalf((ushort)_p0).ToString("G"),
            DataKind.Decimal => AsDecimal().ToString(System.Globalization.CultureInfo.InvariantCulture),
            DataKind.UInt128 => AsUInt128().ToString(System.Globalization.CultureInfo.InvariantCulture),
            DataKind.Int128 => AsInt128().ToString(System.Globalization.CultureInfo.InvariantCulture),
            DataKind.String => IsInline
                ? $"String[\"{System.Text.Encoding.UTF8.GetString(InlineUtf8Span)}\"]"
                : $"String[arena@{_p0}+{_p1}]",
            DataKind.Date => DateOnly.FromDayNumber(_p0).ToString("yyyy-MM-dd"),
            DataKind.DateTime => AsDateTime().ToString("O"),
            DataKind.Uuid => AsUuid().ToString("D"),
            DataKind.Boolean => _p0 != 0 ? "true" : "false",
            DataKind.Time => new TimeOnly(ReadLong()).ToString("HH:mm:ss.FFFFFFF"),
            DataKind.Duration => new TimeSpan(ReadLong()).ToString("c"),
            DataKind.Image => $"Image[offset={_p0}, len={_p1}]",
            DataKind.Array => $"Array<{(DataKind)_meta}>",
            DataKind.Struct => $"Struct({_meta} fields)",
            _ => _kind.ToString(),
        };
    }
}
