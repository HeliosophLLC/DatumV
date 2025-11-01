using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DatumIngest.DatumFile.Sidecar;
using DatumIngest.Functions.Image;

namespace DatumIngest.Model;

/// <summary>
/// An immutable, discriminated union value that carries typed data through the query pipeline.
/// Use the static factory methods (<see cref="FromFloat32"/>, <see cref="FromVector(float[])"/>, etc.)
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

    /// <summary>Bit mask for the null flag in <see cref="_flags"/>.</summary>
    private const byte FlagIsNull = 0x01;

    /// <summary>
    /// Bit mask indicating the value's payload lives in an external <see cref="IValueStore"/>
    /// (typically an <see cref="Arena"/>) rather than inline in <c>_p0</c>-<c>_p3</c>.
    /// Set for reference-type payloads (vectors, matrices, arrays, images, …) and for
    /// strings/JSON whose UTF-8 form exceeds 16 bytes. Inline is the default — fixed-size
    /// scalars and strings/JSON ≤ 16 bytes carry no flag.
    /// </summary>
    private const byte FlagInArena = 0x02;

    /// <summary>
    /// Bit mask indicating the value's payload lives in a <c>.datum-blob</c> sidecar
    /// addressed by a 64-bit absolute offset (<c>_p0</c>+<c>_p1</c>) and a 40-bit length
    /// (<c>_p2</c> + low byte of <c>_p3</c>). The high 24 bits of <c>_p3</c> are reserved.
    /// Mutually exclusive with <see cref="FlagInArena"/>; resolution requires an
    /// <see cref="IBlobSource"/> rather than an <see cref="IValueStore"/>.
    /// </summary>
    private const byte FlagInSidecar = 0x04;

    /// <summary>Maximum representable length for a sidecar-backed value (40-bit cap, ~1 TiB).</summary>
    private const long SidecarLengthMax = (1L << 40) - 1;

    // ───────────────────────── Fields (20 bytes) ─────────────────────────

    // Header (4 bytes)
    [FieldOffset(0)]  private readonly DataKind _kind;     //  1 byte  — type discriminator
    [FieldOffset(1)]  private readonly byte _flags;        //  1 byte  — bit 0: IsNull, bit 1: InArena
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

    private DataValue(DataKind kind, byte flags, int p0, int p1 = 0, int p2 = 0, int p3 = 0, ushort charCount = 0)
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
    private DataValue(DataKind kind, byte flags, int referenceIndex, short meta)
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
    public bool IsNull => (_flags & FlagIsNull) != 0;

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
    public bool IsInline => (_flags & (FlagIsNull | FlagInArena | FlagInSidecar)) == 0;

    /// <summary>
    /// Whether this value's payload lives in a <c>.datum-blob</c> sidecar, addressed by
    /// a 64-bit absolute offset and a 40-bit length. Resolution requires the table
    /// provider's <see cref="IBlobSource"/>; the standard <see cref="IValueStore"/>
    /// cannot satisfy reads against sidecar-backed values because the coordinate space
    /// is 64-bit, not 32-bit.
    /// </summary>
    public bool IsInSidecar => (_flags & FlagInSidecar) != 0;

    /// <summary>
    /// Decodes the 64-bit sidecar offset packed across <c>_p0</c> and <c>_p1</c>. Only
    /// meaningful when <see cref="IsInSidecar"/> is <c>true</c>.
    /// </summary>
    private long SidecarOffset => Unsafe.As<int, long>(ref Unsafe.AsRef(in _p0));

    /// <summary>
    /// Decodes the 40-bit sidecar length packed across <c>_p2</c> and the low byte of
    /// <c>_p3</c>. Only meaningful when <see cref="IsInSidecar"/> is <c>true</c>.
    /// </summary>
    private long SidecarLength => (long)(uint)_p2 | ((long)(_p3 & 0xFF) << 32);

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
    private static readonly DataValue NullUnknown = new(DataKind.Unknown, flags: FlagIsNull, p0: 0);
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

    /// <summary>Creates a value from a byte array.</summary>
    /// <remarks>Obsolete: ReferenceStore has been removed. Use <see cref="FromUInt8Array(byte[], IValueStore)"/> instead.</remarks>
    public static DataValue FromUInt8Array(byte[] value) =>
        throw new InvalidOperationException("Use FromUInt8Array(value, store). ReferenceStore is no longer available.");

    /// <summary>Creates a value from a byte array using an explicit <see cref="IValueStore"/>.</summary>
    public static DataValue FromUInt8Array(byte[] value, IValueStore store)
    {
        var (p0, p1) = store.StoreBytes(value);
        return new(DataKind.UInt8Array, flags: FlagInArena, p0: p0, p1: p1);
    }

    /// <summary>
    /// Creates a <see cref="DataKind.UInt8Array"/> value whose bytes live in a
    /// <c>.datum-blob</c> sidecar. The DataValue carries 64-bit absolute offset and
    /// 40-bit length; resolution requires an <see cref="IBlobSource"/>, typically the
    /// table provider's sidecar read store.
    /// </summary>
    /// <param name="offset">Absolute byte offset into the sidecar file (includes header).</param>
    /// <param name="length">Number of bytes; 0 ≤ length ≤ <c>2^40 − 1</c> (~1 TiB).</param>
    public static DataValue FromUInt8ArrayInSidecar(long offset, long length) =>
        BuildSidecar(DataKind.UInt8Array, offset, length);

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
        return new(DataKind.String, flags: FlagInArena, p0: p0, p1: p1, p2: hashLo, p3: hashHi, charCount: cc);
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
        return new(DataKind.String, flags: FlagInArena, p0: p0, p1: p1, p2: hashLo, p3: hashHi, charCount: cc);
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
        return new(DataKind.String, flags: FlagInArena, p0: p0, p1: p1, p2: hashLo, p3: hashHi, charCount: cc);
    }

    /// <summary>
    /// Creates an inline <see cref="DataKind.String"/> or <see cref="DataKind.JsonValue"/>
    /// whose UTF-8 bytes live directly in <c>_p0</c>-<c>_p3</c>. Requires
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
        new(DataKind.String, flags: FlagInArena, p0: offset, p1: length);

    /// <summary>Creates a rank-1 tensor (vector) from a float array.</summary>
    /// <remarks>Obsolete: ReferenceStore has been removed. Use <see cref="FromVector(float[], IValueStore)"/> instead.</remarks>
    public static DataValue FromVector(float[] value) =>
        throw new InvalidOperationException("Use FromVector(value, store). ReferenceStore is no longer available.");

    /// <summary>Creates a rank-1 tensor (vector) from a float array using an explicit <see cref="IValueStore"/>.</summary>
    public static DataValue FromVector(float[] value, IValueStore store)
    {
        var (p0, p1) = store.StoreFloats(value);
        return new(DataKind.Vector, flags: FlagInArena, p0: p0, p1: p1);
    }

    /// <summary>Creates a rank-2 tensor (matrix) from a flat float array and its dimensions.</summary>
    /// <remarks>Obsolete: ReferenceStore has been removed. Use <see cref="FromMatrix(float[], int, int, IValueStore)"/> instead.</remarks>
    public static DataValue FromMatrix(float[] data, int rows, int columns) =>
        throw new InvalidOperationException("Use FromMatrix(data, rows, columns, store). ReferenceStore is no longer available.");

    /// <summary>Creates a rank-2 tensor (matrix) using an explicit <see cref="IValueStore"/>.</summary>
    public static DataValue FromMatrix(float[] data, int rows, int columns, IValueStore store)
    {
        if (data.Length != rows * columns)
        {
            throw new ArgumentException(
                $"Data length {data.Length} does not match shape {rows}x{columns}.");
        }

        var (p0, _) = store.StoreFloats(data);
        return new(DataKind.Matrix, flags: FlagInArena, p0: p0, p1: rows, p2: columns);
    }

    /// <summary>Creates an arbitrary-rank tensor from a flat float array and its shape.</summary>
    /// <remarks>Obsolete: ReferenceStore has been removed. Use <see cref="FromTensor(float[], int[], IValueStore)"/> instead.</remarks>
    public static DataValue FromTensor(float[] data, int[] shape) =>
        throw new InvalidOperationException("Use FromTensor(data, shape, store). ReferenceStore is no longer available.");

    /// <summary>Creates an arbitrary-rank tensor using an explicit <see cref="IValueStore"/>.</summary>
    public static DataValue FromTensor(float[] data, int[] shape, IValueStore store)
    {
        int expectedLength = 1;
        foreach (int dimension in shape)
            expectedLength *= dimension;

        if (data.Length != expectedLength)
        {
            throw new ArgumentException(
                $"Data length {data.Length} does not match shape [{string.Join(", ", shape)}].");
        }

        var (p0, p1) = store.StoreTensor(data, shape);
        return new(DataKind.Tensor, flags: FlagInArena, p0: p0, p1: p1, p2: expectedLength);
    }

    /// <summary>Creates a value from encoded image bytes.</summary>
    /// <remarks>Obsolete: ReferenceStore has been removed. Use <see cref="FromImage(byte[], IValueStore)"/> instead.</remarks>
    public static DataValue FromImage(byte[] value) =>
        throw new InvalidOperationException("Use FromImage(value, store). ReferenceStore is no longer available.");

    /// <summary>Creates a value from encoded image bytes using an explicit <see cref="IValueStore"/>.</summary>
    public static DataValue FromImage(byte[] value, IValueStore store)
    {
        var (p0, p1) = store.StoreBytes(value);
        return new(DataKind.Image, flags: FlagInArena, p0: p0, p1: p1);
    }

    /// <summary>
    /// Creates a <see cref="DataKind.Image"/> value whose encoded bytes live in a
    /// <c>.datum-blob</c> sidecar. The DataValue carries 64-bit absolute offset and
    /// 40-bit length; resolution requires an <see cref="IBlobSource"/>, typically the
    /// table provider's sidecar read store. <see cref="AsImage(IValueStore, IBlobSource?)"/>
    /// dispatches based on the sidecar flag.
    /// </summary>
    /// <param name="offset">Absolute byte offset into the sidecar file (includes header).</param>
    /// <param name="length">Number of bytes; 0 ≤ length ≤ <c>2^40 − 1</c> (~1 TiB).</param>
    public static DataValue FromImageInSidecar(long offset, long length) =>
        BuildSidecar(DataKind.Image, offset, length);

    /// <summary>
    /// Packs a sidecar coordinate into the DataValue payload. <c>_p0</c>+<c>_p1</c>
    /// hold the 64-bit offset, <c>_p2</c> + low byte of <c>_p3</c> hold the 40-bit
    /// length, the high 24 bits of <c>_p3</c> are reserved (zero in v1).
    /// </summary>
    private static DataValue BuildSidecar(DataKind kind, long offset, long length)
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

        return new(kind, flags: FlagInSidecar, p0: p0, p1: p1, p2: p2, p3: p3);
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
        new(DataKind.Image, flags: FlagInArena, p0: offset, p1: length);

    /// <summary>
    /// Creates a <see cref="DataKind.UInt8Array"/> value that references bytes
    /// already written to an <see cref="IValueStore"/> at the given offset and
    /// length. Parallel to <see cref="FromImageAtOffset"/>, for generic binary
    /// payloads where the bytes are already arena-resident.
    /// </summary>
    public static DataValue FromUInt8ArrayAtOffset(int offset, int length) =>
        new(DataKind.UInt8Array, flags: FlagInArena, p0: offset, p1: length);

    /// <summary>Creates a value from an <see cref="ImageHandle"/>.</summary>
    /// <remarks>Obsolete: ReferenceStore has been removed. Use <see cref="FromImageHandle(ImageHandle, IValueStore)"/> instead.</remarks>
    internal static DataValue FromImageHandle(ImageHandle handle) =>
        throw new InvalidOperationException("Use FromImageHandle(handle, store). ReferenceStore is no longer available.");

    /// <summary>Creates a value from an <see cref="ImageHandle"/> using an explicit store.</summary>
    internal static DataValue FromImageHandle(ImageHandle handle, IValueStore store)
    {
        var (p0, p1) = store.StoreObject(handle);
        return new(DataKind.Image, flags: FlagInArena, p0: p0, p1: p1);
    }

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
    /// Creates a value from a raw JSON string without a store. Works only when the string's
    /// UTF-8 form fits in 16 bytes (inline path); longer strings require a store.
    /// </summary>
    public static DataValue FromJsonValue(string value)
    {
        Span<byte> scratch = stackalloc byte[16];
        if (System.Text.Encoding.UTF8.TryGetBytes(value, scratch, out int written))
        {
            return FromInlineUtf8(DataKind.JsonValue, scratch[..written], value.Length);
        }
        throw new InvalidOperationException(
            "FromJsonValue(value) without a store only supports values whose UTF-8 form fits in 16 bytes. " +
            "Use FromJsonValue(value, store) for longer values.");
    }

    /// <summary>
    /// Creates a value from a raw JSON string using an explicit <see cref="IValueStore"/>.
    /// Values whose UTF-8 form fits in 16 bytes are stored inline; longer values are
    /// written to <paramref name="store"/>.
    /// </summary>
    public static DataValue FromJsonValue(string value, IValueStore store)
    {
        Span<byte> scratch = stackalloc byte[16];
        if (System.Text.Encoding.UTF8.TryGetBytes(value, scratch, out int written))
        {
            return FromInlineUtf8(DataKind.JsonValue, scratch[..written], value.Length);
        }

        var (p0, p1) = store.StoreString(value);
        var (hashLo, hashHi) = HashString(value.AsSpan());
        ushort cc = value.Length <= ushort.MaxValue ? (ushort)value.Length : ushort.MaxValue;
        return new(DataKind.JsonValue, flags: FlagInArena, p0: p0, p1: p1, p2: hashLo, p3: hashHi, charCount: cc);
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
    public bool IsArenaBacked => (_flags & FlagInArena) != 0;

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
            DataKind.JsonValue => FromJsonValue(arena.GetString(_p0, _p1), store),
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
        return new(DataKind.Array, flags: FlagInArena, p0: p0, p1: p1, p2: (int)elementKind);
    }

    /// <summary>Creates a typed array value using an explicit <see cref="IValueStore"/>.</summary>
    public static DataValue FromArray(DataKind elementKind, List<DataValue> elements, IValueStore store)
    {
        var (p0, p1) = store.StoreDataValues(CollectionsMarshal.AsSpan(elements));
        return new(DataKind.Array, flags: FlagInArena, p0: p0, p1: p1, p2: (int)elementKind);
    }

    /// <summary>Creates a typed null array with the given element kind.</summary>
    /// <param name="elementKind">The element kind of the null array.</param>
    public static DataValue NullArray(DataKind elementKind) =>
        new(DataKind.Array, FlagIsNull, referenceIndex: 0, meta: (short)elementKind);

    /// <summary>Creates a struct value from a positional array of field values.</summary>
    /// <remarks>Obsolete: ReferenceStore has been removed. Use <see cref="FromStruct(short, DataValue[], IValueStore)"/> instead.</remarks>
    public static DataValue FromStruct(short fieldCount, DataValue[] fields) =>
        throw new InvalidOperationException("Use FromStruct(fieldCount, fields, store). ReferenceStore is no longer available.");

    /// <summary>Creates a struct value using an explicit <see cref="IValueStore"/>.</summary>
    public static DataValue FromStruct(short fieldCount, DataValue[] fields, IValueStore store)
    {
        var (p0, p1) = store.StoreDataValues(fields);
        return new(DataKind.Struct, flags: FlagInArena, p0: p0, p1: p1, p2: fieldCount);
    }

    /// <summary>Creates a typed null struct with the given field count.</summary>
    public static DataValue NullStruct(short fieldCount) =>
        new(DataKind.Struct, FlagIsNull, referenceIndex: 0, meta: fieldCount);

    /// <summary>Creates a typed null value.</summary>
    public static DataValue Null(DataKind kind)
        => new(kind, flags: FlagIsNull, p0: 0);

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
        return kind is DataKind.Float32 or DataKind.Float64
            or DataKind.UInt8 or DataKind.Int8 or DataKind.Int16 or DataKind.UInt16
            or DataKind.Int32 or DataKind.UInt32 or DataKind.Int64 or DataKind.UInt64
            or DataKind.Boolean;
    }

    /// <summary>
    /// Extracts the numeric payload as a <see cref="double"/> for coercion purposes.
    /// </summary>
    private double ToDoubleRaw()
    {
        return _kind switch
        {
            DataKind.Float32 => BitConverter.Int32BitsToSingle(_p0),
            DataKind.Float64 => BitConverter.Int64BitsToDouble(ReadLong()),
            DataKind.UInt8 => (byte)_p0,
            DataKind.Int8 => (sbyte)_p0,
            DataKind.Int16 => (short)_p0,
            DataKind.UInt16 => (ushort)_p0,
            DataKind.Int32 => _p0,
            DataKind.UInt32 => (uint)_p0,
            DataKind.Int64 => ReadLong(),
            DataKind.UInt64 => (ulong)ReadLong(),
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
            DataKind.Float32 => FromFloat32((float)value),
            DataKind.Float64 => FromFloat64(value),
            DataKind.UInt8 => FromUInt8((byte)value),
            DataKind.Int8 => FromInt8((sbyte)value),
            DataKind.Int16 => FromInt16((short)value),
            DataKind.UInt16 => FromUInt16((ushort)value),
            DataKind.Int32 => FromInt32((int)value),
            DataKind.UInt32 => FromUInt32((uint)value),
            DataKind.Int64 => FromInt64((long)value),
            DataKind.UInt64 => FromUInt64((ulong)value),
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
        kind is DataKind.Float32 or DataKind.Float64
            or DataKind.Int8 or DataKind.Int16 or DataKind.Int32 or DataKind.Int64
            or DataKind.UInt8 or DataKind.UInt16 or DataKind.UInt32 or DataKind.UInt64
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

    /// <summary>Returns the byte array payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    /// <remarks>Obsolete: ReferenceStore has been removed. Use <see cref="AsUInt8Array(IValueStore, IBlobSource?)"/> instead.</remarks>
    public byte[] AsUInt8Array()
    {
        ThrowIfNullOrWrongKind(DataKind.UInt8Array);
        throw new InvalidOperationException("Use AsUInt8Array(store). ReferenceStore is no longer available.");
    }

    /// <summary>
    /// Returns the byte array payload. For arena-backed values, reads from
    /// <paramref name="store"/>; for sidecar-backed values, reads from
    /// <paramref name="sidecar"/>. The flag on the DataValue determines which path runs.
    /// </summary>
    public byte[] AsUInt8Array(IValueStore store, IBlobSource? sidecar = null)
    {
        ThrowIfNullOrWrongKind(DataKind.UInt8Array);

        if (IsInSidecar)
        {
            return ReadSidecarBytes(sidecar).ToArray();
        }
        return store.RetrieveBytes(_p0, _p1);
    }

    /// <summary>
    /// Returns the byte payload for a <see cref="DataKind.UInt8Array"/> or
    /// <see cref="DataKind.Image"/> value as a <see cref="ReadOnlySpan{T}"/>, without
    /// materializing a managed <c>byte[]</c>. Zero-allocation hot-path reader.
    /// </summary>
    /// <remarks>
    /// For arena-backed values the span is valid only while <paramref name="store"/>'s
    /// backing arena is alive; for sidecar-backed values the span lives as long as the
    /// <paramref name="sidecar"/>'s mmap view does. Callers must consume the span
    /// before whichever store backs it goes away.
    /// </remarks>
    public ReadOnlySpan<byte> AsByteSpan(IValueStore store, IBlobSource? sidecar = null)
    {
        if (_kind is not (DataKind.UInt8Array or DataKind.Image))
        {
            throw new InvalidOperationException($"AsByteSpan is only valid for UInt8Array and Image; got {_kind}.");
        }

        if (IsInSidecar)
        {
            return ReadSidecarBytes(sidecar);
        }
        return store.RetrieveUtf8Span(_p0, _p1);
    }

    /// <summary>
    /// Resolves a sidecar-backed byte payload via the given <see cref="IBlobSource"/>.
    /// Throws when no sidecar source is provided — sidecar-backed DataValues cannot
    /// be read against an arena alone.
    /// </summary>
    private ReadOnlySpan<byte> ReadSidecarBytes(IBlobSource? sidecar)
    {
        if (sidecar is null)
        {
            throw new InvalidOperationException(
                "DataValue is sidecar-backed (FlagInSidecar) but no IBlobSource was provided. " +
                "Pass the table provider's SidecarReadStore as the sidecar argument.");
        }
        return sidecar.Read(SidecarOffset, SidecarLength);
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
    public string AsString(IValueStore store)
    {
        ThrowIfNullOrWrongKind(DataKind.String);
        if (IsInline) return System.Text.Encoding.UTF8.GetString(InlineUtf8Span);
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
    /// Returns the UTF-8 byte length of a <see cref="DataKind.String"/> or
    /// <see cref="DataKind.JsonValue"/> payload without accessing the store.
    /// </summary>
    /// <remarks>
    /// Pairs with <see cref="StringCharCount(IValueStore)"/>: that returns the decoded
    /// character count (possibly via a UTF-8 decode), this returns the encoded byte
    /// count which is always cached in the payload word. Zero-allocation hot-path reader
    /// for column encoders that need per-row byte sizes upfront.
    /// </remarks>
    public int StringByteLength
    {
        get
        {
            if (_kind is not (DataKind.String or DataKind.JsonValue))
            {
                throw new InvalidOperationException(
                    $"Cannot read StringByteLength on a {_kind} value.");
            }
            return IsInline ? InlineByteLength : _p1;
        }
    }

    /// <summary>
    /// Returns the byte length of a binary payload for
    /// <see cref="DataKind.UInt8Array"/> or <see cref="DataKind.Image"/>. Parallel
    /// to <see cref="StringByteLength"/> for binary kinds, enabling two-pass encoders
    /// to size the pooled output buffer before copying bytes.
    /// </summary>
    public int StringOrBinaryByteLength
    {
        get
        {
            if (_kind is not (DataKind.String or DataKind.JsonValue or DataKind.UInt8Array or DataKind.Image))
            {
                throw new InvalidOperationException(
                    $"Cannot read StringOrBinaryByteLength on a {_kind} value.");
            }
            return IsInline ? InlineByteLength : _p1;
        }
    }

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
    /// Returns the element count for collection-type values without accessing the store.
    /// <list type="bullet">
    /// <item><see cref="DataKind.Vector"/>: number of float elements (<c>_p1</c>)</item>
    /// <item><see cref="DataKind.UInt8Array"/>: number of bytes (<c>_p1</c>)</item>
    /// <item><see cref="DataKind.Matrix"/>: rows × columns (<c>_p1 * _p2</c>)</item>
    /// <item><see cref="DataKind.Tensor"/>: total elements (<c>_p2</c>, cached at creation)</item>
    /// </list>
    /// </summary>
    /// <returns>The element count, or -1 if not available inline.</returns>
    public int ElementCount => _kind switch
    {
        DataKind.Vector => _p1,
        DataKind.UInt8Array => _p1,
        DataKind.Matrix => _p1 * _p2,
        DataKind.Tensor when _p2 != 0 => _p2,
        _ => -1,
    };

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
        if (IsNull || (_kind is not DataKind.String and not DataKind.JsonValue))
            ThrowIfNullOrWrongKind(DataKind.String);
        if (IsInline) return InlineUtf8Span;
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
        if (IsNull || (_kind is not DataKind.String and not DataKind.JsonValue))
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

    /// <summary>Returns the vector (rank-1) float array payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    /// <remarks>Obsolete: ReferenceStore has been removed. Use <see cref="AsVector(IValueStore)"/> instead.</remarks>
    public float[] AsVector()
    {
        ThrowIfNullOrWrongKind(DataKind.Vector);
        throw new InvalidOperationException("Use AsVector(store). ReferenceStore is no longer available.");
    }

    /// <summary>Returns the vector (rank-1) float array payload from an explicit <see cref="IValueStore"/>.</summary>
    public float[] AsVector(IValueStore store)
    {
        ThrowIfNullOrWrongKind(DataKind.Vector);
        return store.RetrieveFloats(_p0, _p1);
    }

    /// <summary>Returns the matrix (rank-2) flat float array and its dimensions.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    /// <remarks>Obsolete: ReferenceStore has been removed. Use <see cref="AsMatrix(IValueStore, out int, out int)"/> instead.</remarks>
    public float[] AsMatrix(out int rows, out int columns)
    {
        ThrowIfNullOrWrongKind(DataKind.Matrix);
        rows = _p1;
        columns = _p2;
        throw new InvalidOperationException("Use AsMatrix(store, out rows, out columns). ReferenceStore is no longer available.");
    }

    /// <summary>Returns the matrix (rank-2) flat float array and its dimensions from an explicit <see cref="IValueStore"/>.</summary>
    public float[] AsMatrix(IValueStore store, out int rows, out int columns)
    {
        ThrowIfNullOrWrongKind(DataKind.Matrix);
        rows = _p1;
        columns = _p2;
        return store.RetrieveFloats(_p0, _p1 * _p2);
    }

    /// <summary>Returns the tensor flat float array and its shape.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    /// <remarks>Obsolete: ReferenceStore has been removed. Use <see cref="AsTensor(IValueStore, out int[])"/> instead.</remarks>
    public float[] AsTensor(out int[] shape)
    {
        ThrowIfNullOrWrongKind(DataKind.Tensor);
        shape = [];
        throw new InvalidOperationException("Use AsTensor(store, out shape). ReferenceStore is no longer available.");
    }

    /// <summary>Returns the tensor flat float array and its shape from an explicit <see cref="IValueStore"/>.</summary>
    public float[] AsTensor(IValueStore store, out int[] shape)
    {
        ThrowIfNullOrWrongKind(DataKind.Tensor);
        return store.RetrieveTensor(_p0, _p1, out shape);
    }

    /// <summary>
    /// Returns the encoded image byte array payload.
    /// </summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    /// <remarks>Obsolete: ReferenceStore has been removed. Use <see cref="AsImage(IValueStore, IBlobSource?)"/> instead.</remarks>
    public byte[] AsImage()
    {
        ThrowIfNullOrWrongKind(DataKind.Image);
        throw new InvalidOperationException("Use AsImage(store). ReferenceStore is no longer available.");
    }

    /// <summary>
    /// Returns the encoded image byte array. For arena-backed values, reads from
    /// <paramref name="store"/>; for sidecar-backed values, reads from
    /// <paramref name="sidecar"/>. The flag on the DataValue determines which path runs.
    /// </summary>
    public byte[] AsImage(IValueStore store, IBlobSource? sidecar = null)
    {
        ThrowIfNullOrWrongKind(DataKind.Image);

        if (IsInSidecar)
        {
            return ReadSidecarBytes(sidecar).ToArray();
        }
        return store.RetrieveBytes(_p0, _p1);
    }

    /// <summary>
    /// Returns the <see cref="ImageHandle"/> for this image value.
    /// </summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    /// <remarks>Obsolete: ReferenceStore has been removed. Use <see cref="GetImageHandle(IValueStore, IBlobSource?)"/> instead.</remarks>
    internal ImageHandle GetImageHandle()
    {
        ThrowIfNullOrWrongKind(DataKind.Image);
        throw new InvalidOperationException("Use GetImageHandle(store). ReferenceStore is no longer available.");
    }

    /// <summary>
    /// Returns an <see cref="ImageHandle"/> for this image value, reconstructing from
    /// encoded bytes stored in the given <see cref="IValueStore"/>. The bitmap is not
    /// decoded until explicitly requested.
    /// </summary>
    internal ImageHandle GetImageHandle(IValueStore store, IBlobSource? sidecar = null)
    {
        ThrowIfNullOrWrongKind(DataKind.Image);

        if (IsInSidecar)
        {
            // Sidecar payloads are always raw encoded bytes (JPEG/PNG/etc.) — no
            // object side-list, no precomputed ImageHandle. Decode directly from the
            // mmap-backed span.
            ReadOnlySpan<byte> bytes = ReadSidecarBytes(sidecar);
            byte[] copy = bytes.ToArray();
            return new ImageHandle(copy, ImageEncoder.ResolveFormat(copy, formatOverride: null));
        }

        // Try the object side-list first (ImageHandle from a previous function in the chain).
        try
        {
            object obj = store.RetrieveObject(_p0, _p1);
            if (obj is ImageHandle handle) return handle;
        }
        catch (InvalidOperationException) { /* not in object list — fall through to bytes */ }
        catch (NotSupportedException) { /* store doesn't support objects — fall through */ }

        // Fall back to byte[] storage (from deserialization or FromImage).
        byte[] bytes2 = store.RetrieveBytes(_p0, _p1);
        return new ImageHandle(bytes2, ImageEncoder.ResolveFormat(bytes2, formatOverride: null));
    }

    /// <summary>
    /// Returns the <see cref="ImageHandle"/> payload if this value already owns one,
    /// or <c>null</c> if the payload is raw bytes or no store is available.
    /// </summary>
    /// <remarks>Obsolete: ReferenceStore has been removed. Use <see cref="GetImageHandle(IValueStore, IBlobSource?)"/> and check the store instead.</remarks>
    internal ImageHandle? TryGetOwnedImageHandle() => null;

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

    /// <summary>Returns the raw JSON string payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    /// <remarks>Obsolete: ReferenceStore has been removed. Use <see cref="AsJsonValue(IValueStore)"/> instead.</remarks>
    public string AsJsonValue()
    {
        ThrowIfNullOrWrongKind(DataKind.JsonValue);
        throw new InvalidOperationException("Use AsJsonValue(store). ReferenceStore is no longer available.");
    }

    /// <summary>Returns the raw JSON string payload from an explicit <see cref="IValueStore"/>.</summary>
    public string AsJsonValue(IValueStore store)
    {
        ThrowIfNullOrWrongKind(DataKind.JsonValue);
        if (IsInline) return System.Text.Encoding.UTF8.GetString(InlineUtf8Span);
        return store.RetrieveString(_p0, _p1);
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

            return (DataKind)_meta;
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

    // ───────────────────── Zero-copy conversions ──────────────────────

    /// <summary>
    /// Converts a <see cref="DataKind.Vector"/> or <see cref="DataKind.Matrix"/> to a
    /// <see cref="DataKind.Tensor"/> using an explicit store.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called on a non-vector, non-matrix value.</exception>
    public DataValue ToTensor(IValueStore store)
    {
        return _kind switch
        {
            DataKind.Vector =>
                FromTensor(store.RetrieveFloats(_p0, _p1), [_p1], store),
            DataKind.Matrix =>
                FromTensor(store.RetrieveFloats(_p0, _p1 * _p2), [_p1, _p2], store),
            _ => throw new InvalidOperationException(
                $"Cannot convert {_kind} to Tensor. Only Vector and Matrix are supported."),
        };
    }

    /// <summary>
    /// Converts a <see cref="DataKind.Vector"/> or <see cref="DataKind.Matrix"/> to a
    /// <see cref="DataKind.Tensor"/> without copying the underlying data.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called on a non-vector, non-matrix value.</exception>
    public DataValue ToTensor() =>
        throw new InvalidOperationException("Use ToTensor(store). ReferenceStore is no longer available.");

    /// <summary>
    /// Converts a rank-1 <see cref="DataKind.Tensor"/> back to a <see cref="DataKind.Vector"/>
    /// using an explicit store.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called on a non-tensor or tensor with rank != 1.</exception>
    public DataValue ToVector(IValueStore store)
    {
        ThrowIfNullOrWrongKind(DataKind.Tensor);
        store.RetrieveTensor(_p0, _p1, out int[] shape);

        if (shape.Length != 1)
        {
            throw new InvalidOperationException(
                $"Cannot convert rank-{shape.Length} tensor to Vector. Rank must be 1.");
        }

        return FromVector(store.RetrieveFloats(_p0, _p1), store);
    }

    /// <summary>
    /// Converts a rank-1 <see cref="DataKind.Tensor"/> back to a <see cref="DataKind.Vector"/>
    /// without copying the underlying data.
    /// </summary>
    public DataValue ToVector() =>
        throw new InvalidOperationException("Use ToVector(store). ReferenceStore is no longer available.");

    /// <summary>
    /// Converts a rank-2 <see cref="DataKind.Tensor"/> back to a <see cref="DataKind.Matrix"/>
    /// using an explicit store.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called on a non-tensor or tensor with rank != 2.</exception>
    public DataValue ToMatrix(IValueStore store)
    {
        ThrowIfNullOrWrongKind(DataKind.Tensor);
        store.RetrieveTensor(_p0, _p1, out int[] shape);

        if (shape.Length != 2)
        {
            throw new InvalidOperationException(
                $"Cannot convert rank-{shape.Length} tensor to Matrix. Rank must be 2.");
        }

        return FromMatrix(store.RetrieveFloats(_p0, _p1), shape[0], shape[1], store);
    }

    /// <summary>
    /// Converts a rank-2 <see cref="DataKind.Tensor"/> back to a <see cref="DataKind.Matrix"/>
    /// without copying the underlying data.
    /// </summary>
    public DataValue ToMatrix() =>
        throw new InvalidOperationException("Use ToMatrix(store). ReferenceStore is no longer available.");

    // ───────────────────────── Equality ─────────────────────────

    /// <inheritdoc/>
    public override bool Equals(object? other) => other is DataValue dv && Equals(dv);

    /// <inheritdoc/>
    public bool Equals(DataValue other)
    {
        if (_kind != other._kind) return false;
        if (IsNull && other.IsNull) return true;
        if (IsNull != other.IsNull) return false;

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
            DataKind.Float32
                => BitConverter.Int32BitsToSingle(_p0) == BitConverter.Int32BitsToSingle(other._p0),
            DataKind.Float64
                => BitConverter.Int64BitsToDouble(ReadLong()) == BitConverter.Int64BitsToDouble(other.ReadLong()),

            // Reference types:
            DataKind.String or DataKind.JsonValue
                => CompareStrings(in this, in other),
            DataKind.Uuid
                => _p0 == other._p0 && _p1 == other._p1 && _p2 == other._p2 && _p3 == other._p3,
            DataKind.DateTime
                => _p0 == other._p0 && _p1 == other._p1 && _p2 == other._p2,
            // For reference types without a store, use offset-equality: same (_p0,_p1) in the
            // same store means identical content. Different offsets → unknown, return false.
            DataKind.Vector
                => _p0 == other._p0 && _p1 == other._p1,
            DataKind.Matrix
                => _p0 == other._p0 && _p1 == other._p1 && _p2 == other._p2,
            DataKind.Tensor
                => _p0 == other._p0 && _p1 == other._p1,
            DataKind.UInt8Array
                => _p0 == other._p0 && _p1 == other._p1,
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
            DataKind.Float32
                => HashCode.Combine(_kind, BitConverter.Int32BitsToSingle(_p0)),
            DataKind.Float64
                => HashCode.Combine(_kind, BitConverter.Int64BitsToDouble(ReadLong())),

            // Reference types:
            // RawContentHash returns XxHash64-over-UTF-8 for both inline and cached-hash
            // values, so equal-content strings across the two modes hash to the same code —
            // matching CompareStrings' mixed-mode hash match.
            DataKind.String or DataKind.JsonValue
                => RawContentHash != 0
                    ? HashCode.Combine(_kind, RawContentHash)
                    : ComputeStringHashCode(),
            DataKind.DateTime
                => HashCode.Combine(_kind, _p0, _p1, _p2),
            DataKind.Uuid
                => HashCode.Combine(_kind, _p0, _p1, _p2, _p3),
            // Offset-based hashing: consistent with offset-equality in Equals.
            DataKind.Vector
                => HashCode.Combine(_kind, _p0, _p1),
            DataKind.Matrix
                => HashCode.Combine(_kind, _p0, _p1, _p2),
            DataKind.Tensor
                => HashCode.Combine(_kind, _p0, _p1),
            DataKind.UInt8Array
                => HashCode.Combine(_kind, _p0, _p1),
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
        if ((_flags & FlagIsNull) != 0)
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
            DataKind.String => IsInline
                ? $"String[\"{System.Text.Encoding.UTF8.GetString(InlineUtf8Span)}\"]"
                : $"String[arena@{_p0}+{_p1}]",
            DataKind.Date => DateOnly.FromDayNumber(_p0).ToString("yyyy-MM-dd"),
            DataKind.DateTime => AsDateTime().ToString("O"),
            DataKind.JsonValue => IsInline
                ? $"JsonValue[\"{System.Text.Encoding.UTF8.GetString(InlineUtf8Span)}\"]"
                : $"JsonValue[arena@{_p0}+{_p1}]",
            DataKind.Uuid => AsUuid().ToString("D"),
            DataKind.Boolean => _p0 != 0 ? "true" : "false",
            DataKind.Time => new TimeOnly(ReadLong()).ToString("HH:mm:ss.FFFFFFF"),
            DataKind.Duration => new TimeSpan(ReadLong()).ToString("c"),
            DataKind.Vector => $"Vector[{_p1} elements]",
            DataKind.Matrix => $"Matrix[{_p1}x{_p2}]",
            DataKind.Tensor => $"Tensor[{_p2} elements]",
            DataKind.UInt8Array => $"UInt8Array[{_p1} bytes]",
            DataKind.Image => $"Image[offset={_p0}, len={_p1}]",
            DataKind.Array => $"Array<{(DataKind)_meta}>",
            DataKind.Struct => $"Struct({_meta} fields)",
            _ => _kind.ToString(),
        };
    }
}
