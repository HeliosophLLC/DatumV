using System.IO.Hashing;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Heliosoph.DatumV.DatumFile.Sidecar;

namespace Heliosoph.DatumV.Model;

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
[StructLayout(LayoutKind.Explicit, Size = SizeBytes)]
public readonly partial struct DataValue : IEquatable<DataValue>
{
    /// <summary>
    /// Size of one <see cref="DataValue"/> in bytes. Single source of truth for
    /// row-stride computations, raw spill-buffer sizing, and reinterpret-cast
    /// validation. Changing this requires re-laying-out the <c>[FieldOffset]</c>
    /// attributes below; the <c>DataValue_StructSizeMatchesSizeBytesConstant</c>
    /// test guards the invariant.
    /// </summary>
    public const int SizeBytes = 32;

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
        /// All typed-array kinds (UInt8[], Int32[], Float64[], Date[], String[],
        /// Image[], Struct[], …) come into existence via <c>Kind + IsArray</c>.
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

        /// <summary>
        /// This typed-array value carries an explicit multi-dimensional shape as an
        /// <c>int32 × ndim</c> prefix at the head of its payload bytes (arena / sidecar /
        /// inline alike). <c>ndim</c> lives in the high byte of <c>_charCount</c>. Only
        /// set for <c>ndim ≥ 2</c> — 1-D arrays remain represented as today (no flag, no
        /// prefix, element count derived from byte length). Element accessors
        /// (<see cref="AsArraySpan{T}"/>, <see cref="AsInlineArraySpan{T}"/>,
        /// <see cref="InlineArrayBytes"/>, <see cref="ElementCount"/>) skip the prefix
        /// transparently; <see cref="GetShape"/> exposes the dims.
        ///
        /// Incompatible with byte-array kinds (<c>UInt8 + IsArray</c>) and with reference-
        /// element arrays (<c>String[]</c>, <c>Image[]</c>, <c>Struct[]</c>) — those use
        /// <c>_charCount</c> for storeId / TypeId already.
        /// </summary>
        IsMultiDim = 0x20,

        // 0x40, 0x80 reserved for future use.
    }

    /// <summary>Maximum representable length for a sidecar-backed value (40-bit cap, ~1 TiB).</summary>
    private const long SidecarLengthMax = (1L << 40) - 1;

    /// <summary>
    /// Maximum UTF-8 byte capacity for an inline <see cref="DataKind.String"/> /
    /// <see cref="DataKind.Json"/> value. Strings whose UTF-8 form fits in this many
    /// bytes are stored directly in the struct's payload region (struct bytes 4-30,
    /// spanning <c>_p0</c>-<c>_p6</c> low 3 bytes); longer strings spill to an
    /// <see cref="IValueStore"/>. Bumped from 16 → 27 in PR6 once the struct widened
    /// to 32 bytes — common short-format strings (datetimes, identifiers, short
    /// labels) now skip the arena round-trip.
    /// </summary>
    public const int MaxInlineUtf8Bytes = 27;

    // ───────────────────────── Fields (32 bytes) ─────────────────────────

    // Header (4 bytes)
    [FieldOffset(0)]  private readonly DataKind _kind;     //  1 byte  — type discriminator
    [FieldOffset(1)]  private readonly DataValueFlags _flags; //  1 byte  — see DataValueFlags
    // ushort at offset 2 carries string/JSON sizing info, interpreted by storage mode:
    //   Non-inline (reference-store / arena-slice): full char count (0 = unknown, 65535 = overflow sentinel)
    //   Inline: low byte  = UTF-8 byte length (0-16)
    //           high byte = char count         (0-16)
    [FieldOffset(2)]  private readonly ushort _charCount;

    // Payload (28 bytes). Interpretation depends on Kind + Flags:
    //   Inline scalars: bytes 4..N hold the value.
    //   Arena/sidecar: 64-bit offset at bytes 4-11 (overlaps _p0+_p1),
    //                  40-bit length at bytes 12-16 (overlaps _p2 + low byte _p3),
    //                  hash (Strings/JSON) at bytes 20-27 (overlaps _p4+_p5).
    //                  Kind-specific metadata fills bytes 18-31 for other reference kinds.
    [FieldOffset(4)]  private readonly int _p0;            //  4 bytes — payload word 0
    [FieldOffset(8)]  private readonly int _p1;            //  4 bytes — payload word 1
    [FieldOffset(12)] private readonly int _p2;            //  4 bytes — payload word 2
    [FieldOffset(16)] private readonly int _p3;            //  4 bytes — payload word 3
    [FieldOffset(20)] private readonly int _p4;            //  4 bytes — payload word 4 (hash low for strings)
    [FieldOffset(24)] private readonly int _p5;            //  4 bytes — payload word 5 (hash high for strings)
    [FieldOffset(28)] private readonly int _p6;            //  4 bytes — payload word 6 (kind-specific tail)

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
        _p4 = 0;
        _p5 = 0;
        _p6 = 0;
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
        _p4 = 0;
        _p5 = 0;
        _p6 = 0;
    }

    /// <summary>
    /// Constructor for struct / Type DataValues that carry a <see cref="TypeId"/>. The
    /// type-id rides in the low 16 bits of <c>_p6</c> — a dedicated slot promoted out
    /// of <c>_charCount</c> in PR5 so that struct values no longer alias their type-id
    /// with inline-string byte counts, element counts, ndim, or sidecar storeIds.
    /// The <c>ushort</c> 3rd parameter distinguishes this overload from the <c>int</c>-based
    /// reference constructor.
    /// </summary>
    private DataValue(DataKind kind, DataValueFlags flags, ushort typeId, int p0, int p1)
    {
        Unsafe.SkipInit(out this);
        _kind = kind;
        _flags = flags;
        _charCount = 0;
        _p0 = p0;
        _p1 = p1;
        _p2 = 0;
        _p3 = 0;
        _p4 = 0;
        _p5 = 0;
        _p6 = unchecked((int)(uint)typeId);
    }

    /// <summary>
    /// Constructor for arena/sidecar-backed struct values that carry a <see cref="TypeId"/>.
    /// Encodes offset across <c>_p0+_p1</c>, length across <c>_p2</c> + low byte of <c>_p3</c>,
    /// and the type-id in the low 16 bits of <c>_p6</c>. Used by struct factories
    /// (<see cref="FromStruct"/>, <see cref="FromStructArray"/>, <see cref="SynthesiseArenaStruct"/>).
    /// The <c>ushort typeId</c> parameter slotted before <c>long offset</c> distinguishes
    /// this overload from the generic arena-backed constructor.
    /// </summary>
    private DataValue(DataKind kind, DataValueFlags flags, ushort typeId, long offset, long length)
    {
        Unsafe.SkipInit(out this);
        _kind = kind;
        _flags = flags;
        _charCount = 0;
        _p0 = unchecked((int)offset);
        _p1 = unchecked((int)(offset >> 32));
        _p2 = unchecked((int)length);
        _p3 = unchecked((int)((length >> 32) & 0xFF));
        _p4 = 0;
        _p5 = 0;
        _p6 = unchecked((int)(uint)typeId);
    }

    /// <summary>
    /// Constructor for arena/sidecar-backed values with a 64-bit offset and 40-bit length.
    /// Encodes offset across <c>_p0+_p1</c> and length across <c>_p2</c> + low byte of <c>_p3</c>,
    /// matching the unified reference-backed payload layout. Used by factories for Image,
    /// Audio, Video, PointCloud, Mesh, byte arrays, and similar blob kinds that do not need
    /// a cached content hash. For Strings/JSON that carry an XxHash64, use the overload that
    /// also takes a <c>hash</c> argument.
    /// </summary>
    private DataValue(DataKind kind, DataValueFlags flags, long offset, long length, ushort charCount = 0)
    {
        Unsafe.SkipInit(out this);
        _kind = kind;
        _flags = flags;
        _charCount = charCount;
        _p0 = unchecked((int)offset);
        _p1 = unchecked((int)(offset >> 32));
        _p2 = unchecked((int)length);
        _p3 = unchecked((int)((length >> 32) & 0xFF));
        _p4 = 0;
        _p5 = 0;
        _p6 = 0;
    }

    /// <summary>
    /// Generic constructor for arena/sidecar-backed values that carry inline kind-specific
    /// metadata in <c>_p4</c>/<c>_p5</c>/<c>_p6</c>. The 12 metadata bytes are packed per-kind
    /// — Image (W/H/channels), Audio (sample_rate/channels/bit_depth/frame_count), Video
    /// (W/H/fps/frame_count/codec), PointCloud (point_count/flags), Mesh (vertex_count/
    /// triangle_count/flags). The caller is responsible for the bit layout; accessor
    /// properties (<see cref="ImageWidth"/>, <c>AudioSampleRate</c>, etc.) read the same
    /// bits back via shifts/masks.
    /// </summary>
    private DataValue(DataKind kind, DataValueFlags flags, long offset, long length, int p4, int p5, int p6, ushort charCount = 0)
    {
        Unsafe.SkipInit(out this);
        _kind = kind;
        _flags = flags;
        _charCount = charCount;
        _p0 = unchecked((int)offset);
        _p1 = unchecked((int)(offset >> 32));
        _p2 = unchecked((int)length);
        _p3 = unchecked((int)((length >> 32) & 0xFF));
        _p4 = p4;
        _p5 = p5;
        _p6 = p6;
    }

    /// <summary>
    /// Image-specific overload that packs <c>(width, height)</c> across <c>_p4</c>
    /// and channels into the low byte of <c>_p5</c>. Forwards to the generic
    /// metadata constructor. Optional <paramref name="hash32"/> stamps the low 32
    /// bits of XxHash64-over-encoded-bytes into <c>_p6</c> so Equals / GetHashCode
    /// can short-circuit on cross-arena / cross-sidecar comparisons; pass 0 when
    /// the bytes aren't available at construction time (legacy fallback path).
    /// </summary>
    private DataValue(DataKind kind, DataValueFlags flags, long offset, long length, ushort width, ushort height, byte channels, ushort charCount = 0, uint hash32 = 0)
        : this(kind, flags, offset, length,
            p4: unchecked((int)((uint)width | ((uint)height << 16))),
            p5: channels,
            p6: unchecked((int)hash32),
            charCount: charCount)
    { }

    /// <summary>
    /// Constructor for arena/sidecar-backed Strings/JSON and arena-backed typed
    /// arrays (Float32[]/Float64[]/Int*[]/byte[]). Encodes offset + length per the
    /// shared reference-backed layout and stamps a cached XxHash64 across <c>_p4+_p5</c>.
    /// The <paramref name="hash"/> is the XxHash64 of the value's UTF-8/CBOR/raw bytes;
    /// a value of zero is the "no cached hash" sentinel honored by
    /// <see cref="RawContentHash"/> and by the cached-hash branches of Equals /
    /// GetHashCode for arrays.
    /// </summary>
    private DataValue(DataKind kind, DataValueFlags flags, long offset, long length, ulong hash, ushort charCount = 0)
    {
        Unsafe.SkipInit(out this);
        _kind = kind;
        _flags = flags;
        _charCount = charCount;
        _p0 = unchecked((int)offset);
        _p1 = unchecked((int)(offset >> 32));
        _p2 = unchecked((int)length);
        _p3 = unchecked((int)((length >> 32) & 0xFF));
        _p4 = unchecked((int)hash);
        _p5 = unchecked((int)(hash >> 32));
        _p6 = 0;
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
    /// Whether this value is a typed array of <see cref="Kind"/> elements (rather
    /// than a scalar). Returns <c>true</c> when the <c>IsArray</c> flag is set —
    /// used by typed arrays of arbitrary element kinds (<c>Int32[]</c>,
    /// <c>Float64[]</c>, <c>String[]</c>, <c>Struct[]</c>, …). Switch dispatch
    /// can use <c>case DataKind.UInt8 when value.IsArray:</c> to handle byte
    /// arrays without relying on a separate kind enum value.
    /// </summary>
    public bool IsArray => (_flags & DataValueFlags.IsArray) != 0;

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
    /// Only meaningful when <see cref="IsInlineArray"/> is <c>true</c>. Independent of
    /// <see cref="IsMultiDim"/> — the count is always the number of logical elements;
    /// the shape prefix is overhead, not counted.
    /// </summary>
    internal byte InlineArrayElementCount => (byte)(_charCount & 0xFF);

    /// <summary>
    /// Whether this value is a typed array carrying an explicit multi-dimensional shape
    /// (<see cref="DataValueFlags.IsMultiDim"/>). Set for <c>ndim ≥ 2</c> arrays produced
    /// by <see cref="FromArenaMultiDimArray{T}"/>, <see cref="FromInlineMultiDimArray{T}"/>,
    /// <see cref="FromMultiDimArrayInSidecar"/>, or attached at INSERT-time by the
    /// shape-coercion path for fixed-shape columns. 1-D arrays leave this <c>false</c>.
    /// </summary>
    public bool IsMultiDim => (_flags & DataValueFlags.IsMultiDim) != 0;

    /// <summary>
    /// Number of dimensions for a multi-dim array value (≥2); <c>0</c> for non-multi-dim
    /// values. Stored in the high byte of <c>_charCount</c>. Use <see cref="GetShape"/>
    /// to read the actual dimensions.
    /// </summary>
    public int Ndim => IsMultiDim ? (_charCount >> 8) : 0;

    /// <summary>
    /// Number of bytes consumed by the shape prefix at the head of this value's payload
    /// (<c>4 × ndim</c>). Zero when <see cref="IsMultiDim"/> is <c>false</c>. Used by
    /// element accessors to skip the prefix and by length-aware encoders to compute
    /// element vs. total byte counts.
    /// </summary>
    private int ShapePrefixByteCount => IsMultiDim ? (_charCount >> 8) * sizeof(int) : 0;

    /// <summary>
    /// Per-query <see cref="TypeRegistry"/> id for this struct or Type value;
    /// 0 (<see cref="TypeRegistry.NoType"/>) when no type has been registered.
    /// Lives in the low 16 bits of <c>_p6</c> — a dedicated slot promoted out of
    /// <c>_charCount</c> in PR5 so struct values no longer alias their type-id with
    /// inline-string byte counts, element counts, ndim, or sidecar storeIds. Inline
    /// reference-array containers (N=0/N=1) and arrays of non-struct elements carry
    /// no overall TypeId and return 0; <c>Array&lt;Struct&gt;</c> elements carry their
    /// per-element TypeId in the slot bytes, not on the array container.
    /// </summary>
    public ushort TypeId =>
        (_kind == DataKind.Struct || _kind == DataKind.Type)
            ? unchecked((ushort)_p6)
            : (ushort)0;

    /// <summary>
    /// Looks up this value's <see cref="TypeDescriptor"/> in <paramref name="registry"/>.
    /// Returns <c>null</c> when <see cref="TypeId"/> is 0 or the registry is <c>null</c>.
    /// </summary>
    public TypeDescriptor? GetTypeDescriptor(TypeRegistry? registry) =>
        registry?.GetDescriptor(TypeId);

    /// <summary>
    /// Retrieves a named field from this struct value using the per-query type registry.
    /// Throws when this value has no type registered or the field name is not found.
    /// </summary>
    public DataValue GetField(string name, TypeRegistry registry, IValueStore store)
    {
        TypeDescriptor? type = registry.GetDescriptor(TypeId);
        if (type is null)
            throw new InvalidOperationException(
                $"Cannot look up field '{name}' by name: this struct value has no type registered (TypeId=0). " +
                "Stamp a type-id at the construction site before calling GetField.");
        int idx = type.FindFieldIndex(name);
        if (idx < 0)
            throw new InvalidOperationException(
                $"Field '{name}' not found in struct type (kind={type.Kind}, fields=[{string.Join(", ", type.Fields?.Select(f => f.Name) ?? [])}]).");
        return AsStruct(store)[idx];
    }

    /// <summary>
    /// Decodes the 64-bit offset for reference-backed values (arena or sidecar), packed
    /// across <c>_p0</c> and <c>_p1</c>. Only meaningful when <see cref="IsArenaBacked"/>
    /// or <see cref="IsInSidecar"/> is <c>true</c>.
    /// </summary>
    internal long BackedOffset => Unsafe.As<int, long>(ref Unsafe.AsRef(in _p0));

    /// <summary>
    /// Sidecar offset alias for <see cref="BackedOffset"/>. Both arena- and sidecar-backed
    /// values share the same on-struct offset encoding under the 32-byte layout.
    /// </summary>
    internal long SidecarOffset => BackedOffset;

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
    /// Decodes the 40-bit length for reference-backed values (arena or sidecar), packed
    /// across <c>_p2</c> and the low byte of <c>_p3</c>. Only meaningful when
    /// <see cref="IsArenaBacked"/> or <see cref="IsInSidecar"/> is <c>true</c>.
    /// </summary>
    // The (long)(uint)_p2 cast looks redundant but is load-bearing: int → uint
    // strips sign extension before widening to long, so a negative _p2 (high bit
    // set in the low 32-bit word) doesn't sign-extend through the bitwise OR with
    // the high-byte shift. Tools that flag this as "cast is redundant" are wrong.
    internal long BackedLength => (long)(uint)_p2 | ((long)(_p3 & 0xFF) << 32);

    /// <summary>
    /// Sidecar length alias for <see cref="BackedLength"/>. Both arena- and sidecar-backed
    /// values share the same on-struct length encoding under the 32-byte layout.
    /// </summary>
    internal long SidecarLength => BackedLength;

    /// <summary>
    /// For inline strings, the UTF-8 byte length (0-27) stored in the low byte of <c>_charCount</c>.
    /// </summary>
    private byte InlineByteLength => (byte)(_charCount & 0xFF);

    /// <summary>
    /// For inline strings, the char count (0-27) stored in the high byte of <c>_charCount</c>.
    /// </summary>
    private byte InlineCharCount => (byte)(_charCount >> 8);

    /// <summary>
    /// UTF-8 byte length of a <see cref="DataKind.String"/> value, for any
    /// storage tier (inline, arena-backed, sidecar-backed). Returns 0 for
    /// non-string kinds — callers gate on <see cref="Kind"/> first, or check
    /// <see cref="IsNull"/>. The byte count is a stored field on every tier
    /// (inline: low byte of <c>_charCount</c>; arena/sidecar:
    /// <see cref="BackedLength"/>), so this is a constant-time read with
    /// no UTF-8 walking.
    /// </summary>
    /// <remarks>
    /// Backs <c>octet_length(text)</c> and the per-tier fast path for
    /// <c>length(text)</c> on inline strings. Internal because the only
    /// consumers are the string-length function bodies and the
    /// elider-emitted accessor branch in <see cref="Heliosoph.DatumV.Execution.ExpressionEvaluator"/>;
    /// they live in this assembly so no public surface is required.
    /// </remarks>
    internal int StringUtf8ByteLength
    {
        get
        {
            if (_kind != DataKind.String) return 0;
            return IsInline ? InlineByteLength : checked((int)BackedLength);
        }
    }

    /// <summary>
    /// Unicode code-point count of an <em>inline</em> <see cref="DataKind.String"/>
    /// value, read from the high byte of <c>_charCount</c>. Returns 0 for arena- /
    /// sidecar-backed strings (whose count lives in the wider <c>_charCount</c>
    /// ushort, not this byte) and for non-string kinds — callers fall back to
    /// walking the UTF-8 bytes when the inline tier doesn't apply. Inline strings
    /// always carry this field, populated at construction
    /// (see <see cref="FromInlineUtf8"/>).
    /// </summary>
    internal byte InlineStringCodePointCount =>
        _kind == DataKind.String && IsInline ? InlineCharCount : (byte)0;

    /// <summary>
    /// Counts Unicode code points in <paramref name="utf8"/> by counting
    /// non-continuation bytes (every UTF-8 leading byte starts a new code point).
    /// Single-pass byte walk, no decoding, no allocations. The result matches PG
    /// <c>length(text)</c> semantics — surrogate-pair characters (e.g. emoji)
    /// count as 1, in contrast to <see cref="string.Length"/> /
    /// <see cref="System.Text.Encoding.GetCharCount(System.ReadOnlySpan{byte})"/>
    /// which count UTF-16 code units.
    /// </summary>
    /// <remarks>
    /// A byte is a continuation byte iff its top two bits are <c>10</c>
    /// (mask <c>0xC0</c> → <c>0x80</c>); every other byte is the first byte
    /// of a code point. Assumes well-formed UTF-8.
    /// </remarks>
    internal static int CountUtf8CodePoints(ReadOnlySpan<byte> utf8)
    {
        int count = 0;
        for (int i = 0; i < utf8.Length; i++)
        {
            if ((utf8[i] & 0xC0) != 0x80) count++;
        }
        return count;
    }

    /// <summary>
    /// Code-point count of <paramref name="chars"/>, matching PG <c>length(text)</c>:
    /// surrogate pairs count as 1. Use this when stamping <c>_charCount</c> for a
    /// String built from a char span; <see cref="System.ReadOnlySpan{T}.Length"/>
    /// counts UTF-16 code units instead and is wrong for surrogate-pair characters.
    /// </summary>
    internal static int CountCharSpanCodePoints(ReadOnlySpan<char> chars)
    {
        int count = 0;
        foreach (System.Text.Rune _ in chars.EnumerateRunes())
        {
            count++;
        }
        return count;
    }

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
            offset: p0.Value, length: p1.Value);
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
            var (elementP0, elementP1) = store.StoreString(elements[0]);
            Span<byte> slotBytes = stackalloc byte[ArraySlot.SizeBytes];
            ArraySlot.Write(slotBytes, elementP0.Value, elementP1.Value);
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
            var (elementP0, elementP1) = store.StoreString(elements[i]);
            ArraySlot.Write(
                slotBlock.AsSpan(i * ArraySlot.SizeBytes, ArraySlot.SizeBytes),
                elementP0.Value,
                elementP1.Value);
        }
        var (blockP0, blockP1) = store.StoreBytes(slotBlock);
        return new(
            DataKind.String,
            flags: DataValueFlags.IsArray | DataValueFlags.InArena,
            offset: blockP0.Value,
            length: blockP1.Value);
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

        // Multi-dim values prepend a [shape × int32] prefix to the slot block on
        // both sidecar and arena tiers; element accessors skip it transparently.
        int shapePrefix = ShapePrefixByteCount;

        // Sidecar-backed: read slot block AND per-element bytes through the
        // registry's IBlobSource. No per-query arena copy at access time —
        // sidecar arrays stay sidecar-resident until a caller explicitly
        // Stabilizes them.
        if (IsInSidecar)
        {
            IBlobSource src = ResolveSidecarSource(registry);
            ReadOnlySpan<byte> blockBytes = ReadSidecarBytes(registry)[shapePrefix..];
            int elementCount = blockBytes.Length / ArraySlot.SizeBytes;
            string[] result = new string[elementCount];
            for (int i = 0; i < elementCount; i++)
            {
                ArraySlot.Read(
                    blockBytes.Slice(i * ArraySlot.SizeBytes, ArraySlot.SizeBytes),
                    out long elementOffset,
                    out long elementLength,
                    out _,
                    out _);
                ReadOnlySpan<byte> utf8 = src.Read(elementOffset, elementLength);
                result[i] = System.Text.Encoding.UTF8.GetString(utf8);
            }
            return result;
        }

        // N = 0 / N = 1 inline path (in-memory only — sidecar-backed arrays are
        // never inline). _charCount = element count (0 or 1). Multi-dim is
        // unreachable here: minimum shape product (2×2 = 4 slots × 16 bytes)
        // already exceeds the 16-byte inline payload, so multi-dim String[]
        // values always flow through the arena branch below.
        if (IsInline)
        {
            if (_charCount == 0) return [];

            // _charCount == 1 — parse the one inline slot from _p0–_p3.
            Span<byte> slotBytes = stackalloc byte[ArraySlot.SizeBytes];
            MemoryMarshal.Write(slotBytes[..4], _p0);
            MemoryMarshal.Write(slotBytes[4..8], _p1);
            MemoryMarshal.Write(slotBytes[8..12], _p2);
            MemoryMarshal.Write(slotBytes[12..16], _p3);
            ArraySlot.Read(slotBytes, out long elementOffset, out long elementLength, out _, out _);
            return [store.RetrieveString(new ArenaOffset((int)elementOffset), new ArenaLength((int)elementLength))];
        }

        // N ≥ 2 arena-backed — slot block lives at (_p0, _p1) in the array's store.
        ReadOnlySpan<byte> arenaBlock = store.RetrieveUtf8Span(new ArenaOffset(BackedOffset), new ArenaLength(BackedLength))[shapePrefix..];
        int n = arenaBlock.Length / ArraySlot.SizeBytes;
        string[] arenaResult = new string[n];
        for (int i = 0; i < n; i++)
        {
            ArraySlot.Read(
                arenaBlock.Slice(i * ArraySlot.SizeBytes, ArraySlot.SizeBytes),
                out long elementOffset,
                out long elementLength,
                out _,
                out _);
            arenaResult[i] = store.RetrieveString(new ArenaOffset((int)elementOffset), new ArenaLength((int)elementLength));
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

        DataValueFlags flags = DataValueFlags.InSidecar;
        if (isArray) flags |= DataValueFlags.IsArray;

        // No cached hash: sidecar-backed Strings/JSON read as RawContentHash=0 today,
        // forcing CompareStrings down its no-hash path. The hash slot at _p4/_p5 stays zero.
        return new(kind, flags: flags, offset: offset, length: length, charCount: storeId);
    }

    /// <summary>
    /// Returns a copy of this sidecar-backed value re-pointed at
    /// (<paramref name="offset"/>, <paramref name="length"/>) with
    /// <paramref name="storeId"/> as its registry store id. Kind, flags,
    /// kind-specific metadata (<c>_p4</c>/<c>_p5</c>/<c>_p6</c>) and the
    /// multi-dim ndim byte carry over unchanged. Used when payload bytes
    /// are copied out of one sidecar into another (e.g. a writer importing
    /// a value scanned from a different table's blob file).
    /// </summary>
    internal DataValue WithSidecarLocation(long offset, long length, byte storeId)
    {
        if (!IsInSidecar)
        {
            throw new InvalidOperationException(
                $"WithSidecarLocation requires a sidecar-backed value; got kind={_kind} flags={_flags}.");
        }
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

        ushort charCount = (ushort)((_charCount & 0xFF00) | storeId);
        return new(_kind, _flags, offset, length, _p4, _p5, _p6, charCount);
    }





    private static ulong HashString(ReadOnlySpan<char> chars)
    {
        int maxBytes = System.Text.Encoding.UTF8.GetMaxByteCount(chars.Length);
        byte[]? rented = null;
        Span<byte> utf8 = maxBytes <= 256
            ? stackalloc byte[maxBytes]
            : (rented = System.Buffers.ArrayPool<byte>.Shared.Rent(maxBytes));
        int written = System.Text.Encoding.UTF8.GetBytes(chars, utf8);
        ulong result = HashUtf8(utf8[..written]);
        if (rented is not null) System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        return result;
    }

    /// <summary>Computes XxHash64 over raw UTF-8 bytes.</summary>
    private static ulong HashUtf8(ReadOnlySpan<byte> utf8) => XxHash64.HashToUInt64(utf8);


    // ───────────────────────── Arena state ─────────────────────────

    /// <summary>
    /// Whether this value's payload lives in an external <see cref="IValueStore"/>
    /// (typically an <see cref="Arena"/>) rather than inline. The inline payload holds an
    /// offset/length pair (<c>_p0</c>/<c>_p1</c>) into the store. True for variable-size
    /// reference types (vectors, matrices, images, arrays, structs, byte arrays) and for
    /// strings/JSON whose UTF-8 form exceeds 16 bytes or were produced via
    /// <see cref="FromStringSlice(long, long)"/>.
    /// </summary>
    public bool IsArenaBacked => (_flags & DataValueFlags.InArena) != 0;

    /// <summary>
    /// Returns a new arena-backed <see cref="DataValue"/> whose offset has been shifted by
    /// <paramref name="delta"/> bytes. Used when merging per-column private arenas into a
    /// shared batch arena after parallel decode.
    /// </summary>
    /// <remarks>
    /// Preserves every other byte of the value verbatim — kind-specific metadata
    /// (String hash, Image dimensions, future Audio/Video metadata in <c>_p4</c>-<c>_p6</c>)
    /// rounds through unchanged. Done by struct-copying <c>this</c> and overwriting only
    /// the two offset words (<c>_p0</c>, <c>_p1</c>) via <c>Unsafe.AsRef</c>.
    /// </remarks>
    /// <param name="delta">Number of bytes to add to the current offset.</param>
    /// <returns>An adjusted value whose length and kind-specific metadata are unchanged.</returns>
    internal DataValue WithArenaOffset(long delta)
    {
        long newOffset = BackedOffset + delta;
        DataValue result = this;
        Unsafe.AsRef(in result._p0) = unchecked((int)newOffset);
        Unsafe.AsRef(in result._p1) = unchecked((int)(newOffset >> 32));
        return result;
    }


    /// <summary>
    /// Creates a struct value with a registered <see cref="TypeRegistry"/> id stamped on it.
    /// </summary>


    /// <summary>
    /// Reads <c>_p0</c> and <c>_p1</c> as a single <see cref="long"/> value.
    /// Used by 64-bit types (Int64, UInt64, Float64, DateTime, Time, Duration).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long ReadLong() =>
        Unsafe.As<int, long>(ref Unsafe.AsRef(in _p0));








    /// <summary>
    /// Returns the element <see cref="DataKind"/> for an array value. With the
    /// typed-array shape (<see cref="DataValueFlags.IsArray"/> flag set with a
    /// concrete element kind), the element kind <em>is</em> <see cref="Kind"/>.
    /// Available on both null and non-null array values.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called on a non-array value.</exception>
    public DataKind ArrayElementKind
    {
        get
        {
            if (!IsArray)
            {
                throw new InvalidOperationException(
                    $"Cannot read ArrayElementKind on a {_kind} value (IsArray=false).");
            }
            return _kind;
        }
    }




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
}
