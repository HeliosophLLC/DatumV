using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Heliosoph.DatumV.DatumFile.Sidecar;

namespace Heliosoph.DatumV.Model;

public readonly partial struct DataValue
{
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
    /// from <paramref name="bytes"/>.Length and <see cref="ScalarByteSize"/>(<paramref name="elementKind"/>).
    /// Used by the v2 decoder and any caller that already has the byte payload — the
    /// <see cref="FromInlineArray{T}"/> overload is a typed wrapper for callers with
    /// element-typed spans.
    /// </summary>
    /// <remarks>
    /// The flag layout, payload packing, and 16-byte cap are identical to
    /// <see cref="FromInlineArray{T}"/>; this is the single packing implementation both
    /// factories share. Callers must ensure <paramref name="bytes"/>.Length is a
    /// multiple of <see cref="ScalarByteSize"/>(<paramref name="elementKind"/>).
    /// </remarks>
    public static DataValue FromInlineArrayBytes(ReadOnlySpan<byte> bytes, DataKind elementKind)
    {
        int elementSize = ScalarByteSize(elementKind);
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
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(elements);
        ulong hash = XxHash64.HashToUInt64(bytes);
        var (p0, p1) = store.StoreBytes(bytes);
        // Stamp the content hash across _p4+_p5 (matches the String/JSON
        // cached-hash layout) so cross-arena equal-content arrays compare equal
        // through the hash fast path in Equals / GetHashCode without the
        // dictionary blowing up on per-batch offset drift.
        return new(
            elementKind,
            flags: DataValueFlags.InArena | DataValueFlags.IsArray,
            offset: p0.Value, length: p1.Value,
            hash: hash);
    }

    /// <summary>
    /// Byte-level entry point for arena-backed typed arrays. The caller has
    /// already packed the element bytes contiguously (matching the per-element
    /// <see cref="ScalarByteSize"/> for <paramref name="elementKind"/>); this
    /// stores them and stamps the IsArray DataValue. Mirrors
    /// <see cref="FromArenaArray{T}"/> for callers without a typed span in
    /// hand (e.g. ValueRef materialisation that walks heterogeneous primitives
    /// via <see cref="CopyInlineScalarBytes"/>).
    /// </summary>
    public static DataValue FromArenaArrayBytes(
        ReadOnlySpan<byte> bytes,
        DataKind elementKind,
        IValueStore store)
    {
        ulong hash = XxHash64.HashToUInt64(bytes);
        var (p0, p1) = store.StoreBytes(bytes);
        return new(
            elementKind,
            flags: DataValueFlags.InArena | DataValueFlags.IsArray,
            offset: p0.Value, length: p1.Value,
            hash: hash);
    }

    // ───────────────────────── Multi-dim arrays ─────────────────────────

    /// <summary>
    /// Maximum representable <c>ndim</c> for a multi-dim array — <c>ndim</c> is packed
    /// into the high byte of <c>_charCount</c>, so the cap is 255. Real tensor shapes
    /// are typically ≤4 dims; this cap is effectively unreachable.
    /// </summary>
    private const int MultiDimMaxNdim = byte.MaxValue;

    /// <summary>
    /// Rejects element kinds for which the fixed-width multi-dim factory does not
    /// apply: reference / blob kinds whose multi-dim form lives in a slot block,
    /// not a flat byte run. Reference-element multi-dim has its own factory
    /// (<see cref="FromArenaMultiDimStringArray"/> for String); kinds without a
    /// dedicated factory still reject here. Byte arrays (<see cref="DataKind.UInt8"/>
    /// + <see cref="DataValueFlags.IsArray"/>) pass through — accessors subtract the
    /// shape prefix when reading.
    /// </summary>
    private static void RejectReferenceElementKind(DataKind elementKind)
    {
        if (elementKind is DataKind.Struct)
        {
            throw new ArgumentException(
                $"Multi-dim is not supported for reference-element kind {elementKind} in this version. " +
                "Use the per-kind multi-dim factory once it lands.",
                nameof(elementKind));
        }
    }

    /// <summary>
    /// Shared shape-validation used by the multi-dim factories. Rejects <c>ndim &lt; 2</c>,
    /// non-positive dims, byte-array kinds, reference-element kinds (String / Struct / blob
    /// kinds), and product-of-dims mismatching the supplied element count.
    /// </summary>
    private static void ValidateMultiDimShape(
        ReadOnlySpan<int> shape, int elementCount, DataKind elementKind)
    {
        if (shape.Length < 2)
        {
            throw new ArgumentException(
                "Multi-dim array requires ndim >= 2. Use FromArenaArray / FromInlineArray for 1-D arrays.",
                nameof(shape));
        }
        if (shape.Length > MultiDimMaxNdim)
        {
            throw new ArgumentOutOfRangeException(
                nameof(shape), shape.Length,
                $"Multi-dim ndim {shape.Length} exceeds the {MultiDimMaxNdim}-dimension cap.");
        }
        RejectReferenceElementKind(elementKind);

        long product = 1;
        for (int i = 0; i < shape.Length; i++)
        {
            int dim = shape[i];
            if (dim <= 0)
            {
                throw new ArgumentException(
                    $"Shape dimension {i} must be positive; got {dim}.", nameof(shape));
            }
            product *= dim;
            if (product > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(shape), product,
                    "Product of shape dimensions overflows Int32.");
            }
        }
        if (product != elementCount)
        {
            throw new ArgumentException(
                $"Product of shape dimensions ({product}) does not equal element count ({elementCount}).",
                nameof(shape));
        }
    }

    /// <summary>
    /// Creates an arena-backed multi-dimensional typed-array value. Packs the shape
    /// (<c>int32 × ndim</c>) followed by the element bytes into a single contiguous arena
    /// allocation; the resulting <see cref="DataValue"/> carries <see cref="DataValueFlags.IsArray"/> |
    /// <see cref="DataValueFlags.InArena"/> | <see cref="DataValueFlags.IsMultiDim"/>, with
    /// <c>ndim</c> in the high byte of <c>_charCount</c>. Element accessors
    /// (<see cref="AsArraySpan{T}"/>, <see cref="ElementCount"/>) transparently skip the
    /// prefix; <see cref="GetShape"/> exposes the dims.
    /// </summary>
    /// <typeparam name="T">Unmanaged element type; must match <paramref name="elementKind"/>.</typeparam>
    /// <param name="elements">Flat element data in row-major order. Length must equal product of <paramref name="shape"/>.</param>
    /// <param name="shape">Per-dim sizes; <c>shape.Length ≥ 2</c>, all positive.</param>
    /// <param name="elementKind">Element kind; cannot be a reference / byte-array kind.</param>
    /// <param name="store">Arena to receive the packed bytes.</param>
    public static DataValue FromArenaMultiDimArray<T>(
        ReadOnlySpan<T> elements,
        ReadOnlySpan<int> shape,
        DataKind elementKind,
        IValueStore store)
        where T : unmanaged
        => FromArenaMultiDimArrayBytes(MemoryMarshal.AsBytes(elements), shape, elementKind, store);

    /// <summary>
    /// Byte-level entry point for arena-backed multi-dim arrays. Mirrors
    /// <see cref="FromArenaArrayBytes"/>: the caller has already packed flat element
    /// bytes (matching <see cref="ScalarByteSize"/> for <paramref name="elementKind"/>);
    /// this prepends the shape prefix and stores the combined block. Used by
    /// <c>LiteralCoercion.EnforceFixedShape</c> to promote a flat fixed-shape INSERT
    /// payload into multi-dim without needing a typed span.
    /// </summary>
    public static DataValue FromArenaMultiDimArrayBytes(
        ReadOnlySpan<byte> elementBytes,
        ReadOnlySpan<int> shape,
        DataKind elementKind,
        IValueStore store)
    {
        // Reject reference / byte-array kinds before calling ScalarByteSize —
        // it throws InvalidOperationException for kinds without a fixed scalar
        // size, which would mask the cleaner ArgumentException from
        // ValidateMultiDimShape.
        RejectReferenceElementKind(elementKind);

        int elementSize = ScalarByteSize(elementKind);
        if (elementBytes.Length % elementSize != 0)
        {
            throw new ArgumentException(
                $"Element byte length {elementBytes.Length} is not a multiple of size " +
                $"{elementSize} for DataKind.{elementKind}.",
                nameof(elementBytes));
        }

        int elementCount = elementBytes.Length / elementSize;
        ValidateMultiDimShape(shape, elementCount, elementKind);

        int shapeBytes = shape.Length * sizeof(int);
        int totalBytes = shapeBytes + elementBytes.Length;

        // Assemble shape + elements into one buffer so the arena store sees a single
        // contiguous block — its address is what (_p0, _p1) records. Two sequential
        // StoreBytes calls would risk a non-back-to-back placement under contention.
        byte[] buffer = new byte[totalBytes];
        MemoryMarshal.AsBytes(shape).CopyTo(buffer);
        elementBytes.CopyTo(buffer.AsSpan(shapeBytes));
        var (p0, p1) = store.StoreBytes(buffer);

        // Hash the full shape+elements block: same-elements-different-shape arrays
        // (e.g. [2,3] vs [3,2]) carry the same ndim in _charCount and would
        // collide in the hash-fast-path of Equals if hashed by elements alone.
        // Cross-arena dedup correctness in hash joins / GROUP BY depends on this.
        ulong hash = XxHash64.HashToUInt64(buffer);
        ushort cc = (ushort)(shape.Length << 8);
        return new(
            elementKind,
            flags: DataValueFlags.InArena | DataValueFlags.IsArray | DataValueFlags.IsMultiDim,
            offset: p0.Value, length: p1.Value, hash: hash, charCount: cc);
    }

    /// <summary>
    /// Creates an inline multi-dimensional typed-array value. Packs the shape prefix and
    /// element bytes into the struct's 16-byte payload region — only viable when
    /// <c>4 × ndim + sizeof(T) × elements.Length ≤ 16</c>. Falls into the same flag
    /// shape as <see cref="FromArenaMultiDimArray{T}"/> but with <see cref="DataValueFlags.InlineArray"/>
    /// set and <c>_charCount</c> packing both ndim (high byte) and element count (low byte).
    /// </summary>
    /// <param name="elements">Flat element data; length must equal product of <paramref name="shape"/> and fit alongside the shape in 16 bytes.</param>
    /// <param name="shape">Per-dim sizes; <c>shape.Length ≥ 2</c>.</param>
    /// <param name="elementKind">Element kind; cannot be a reference / byte-array kind.</param>
    /// <exception cref="ArgumentOutOfRangeException">Total inline bytes exceed 16, or element count exceeds 255.</exception>
    public static DataValue FromInlineMultiDimArray<T>(
        ReadOnlySpan<T> elements,
        ReadOnlySpan<int> shape,
        DataKind elementKind)
        where T : unmanaged
    {
        ValidateMultiDimShape(shape, elements.Length, elementKind);

        int shapeBytes = shape.Length * sizeof(int);
        int elementBytes = elements.Length * Unsafe.SizeOf<T>();
        int totalBytes = shapeBytes + elementBytes;

        if (totalBytes > InlineArrayMaxBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(elements), totalBytes,
                $"Inline multi-dim payload {totalBytes} bytes ({shapeBytes} shape + {elementBytes} elements) " +
                $"exceeds the {InlineArrayMaxBytes}-byte cap. Use FromArenaMultiDimArray.");
        }
        if (elements.Length > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(elements), elements.Length,
                "Inline array element count exceeds the 255-element field cap.");
        }

        Span<byte> buffer = stackalloc byte[InlineArrayMaxBytes];
        MemoryMarshal.AsBytes(shape).CopyTo(buffer);
        MemoryMarshal.AsBytes(elements).CopyTo(buffer[shapeBytes..]);

        int p0 = MemoryMarshal.Read<int>(buffer[..4]);
        int p1 = MemoryMarshal.Read<int>(buffer[4..8]);
        int p2 = MemoryMarshal.Read<int>(buffer[8..12]);
        int p3 = MemoryMarshal.Read<int>(buffer[12..16]);

        ushort cc = (ushort)((shape.Length << 8) | (byte)elements.Length);
        return new(
            elementKind,
            flags: DataValueFlags.IsArray | DataValueFlags.InlineArray | DataValueFlags.IsMultiDim,
            p0: p0, p1: p1, p2: p2, p3: p3, charCount: cc);
    }

    /// <summary>
    /// Creates a sidecar-backed multi-dimensional typed-array value. The caller is
    /// responsible for having written the combined <c>shape (int32 × ndim) + element bytes</c>
    /// block to the sidecar; <paramref name="offset"/> points at the start of the shape
    /// prefix and <paramref name="length"/> covers both prefix and elements.
    /// </summary>
    /// <param name="elementKind">Element kind; cannot be a reference / byte-array kind.</param>
    /// <param name="offset">Absolute byte offset of the shape prefix inside the sidecar file.</param>
    /// <param name="length">Total bytes (shape prefix + elements); 0 ≤ length ≤ 2^40 − 1.</param>
    /// <param name="ndim">Number of dimensions (2..255). Caller must keep this consistent with the bytes at <paramref name="offset"/>.</param>
    /// <param name="storeId">Per-query <see cref="SidecarRegistry"/> id for the backing blob source.</param>
    public static DataValue FromMultiDimArrayInSidecar(
        DataKind elementKind, long offset, long length, int ndim, byte storeId = 0)
    {
        if (ndim < 2 || ndim > MultiDimMaxNdim)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ndim), ndim,
                $"Multi-dim ndim must be in [2, {MultiDimMaxNdim}].");
        }
        if (elementKind is DataKind.Struct)
        {
            throw new ArgumentException(
                $"Multi-dim is not supported for element kind {elementKind} (reference / blob kinds) " +
                "in this version.",
                nameof(elementKind));
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

        ushort cc = (ushort)((ndim << 8) | storeId);
        return new(
            elementKind,
            flags: DataValueFlags.InSidecar | DataValueFlags.IsArray | DataValueFlags.IsMultiDim,
            p0: (int)offset,
            p1: (int)(offset >> 32),
            p2: (int)length,
            p3: (int)((length >> 32) & 0xFF),
            charCount: cc);
    }

    /// <summary>
    /// Creates an arena-backed multi-dimensional <c>Array&lt;String&gt;</c> value.
    /// Each element's UTF-8 bytes are written to <paramref name="store"/>; a slot
    /// block of <c>elements.Length × 16 bytes</c> is then prepended with an
    /// <c>int32 × ndim</c> shape prefix and stored as a single contiguous block.
    /// The resulting <see cref="DataValue"/> carries
    /// <see cref="DataValueFlags.InArena"/> | <see cref="DataValueFlags.IsArray"/> |
    /// <see cref="DataValueFlags.IsMultiDim"/>, with <c>ndim</c> in the high byte
    /// of <c>_charCount</c>. <see cref="AsStringArray"/> transparently skips the
    /// shape prefix when reading; <see cref="GetShape"/> exposes the dims.
    /// </summary>
    /// <remarks>
    /// Inline multi-dim String is impossible by construction — minimum shape
    /// (2×2 = 4 slots × 16 bytes) already exceeds the 16-byte inline payload.
    /// There is no inline counterpart to this factory.
    /// </remarks>
    public static DataValue FromArenaMultiDimStringArray(
        ReadOnlySpan<string> elements,
        ReadOnlySpan<int> shape,
        IValueStore store)
    {
        if (shape.Length < 2 || shape.Length > MultiDimMaxNdim)
        {
            throw new ArgumentOutOfRangeException(
                nameof(shape), shape.Length,
                $"Multi-dim ndim must be in [2, {MultiDimMaxNdim}].");
        }
        long product = 1;
        for (int i = 0; i < shape.Length; i++)
        {
            int dim = shape[i];
            if (dim <= 0)
            {
                throw new ArgumentException(
                    $"Shape dimension {i} must be positive; got {dim}.", nameof(shape));
            }
            product *= dim;
            if (product > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(shape), product,
                    "Product of shape dimensions overflows Int32.");
            }
        }
        if (product != elements.Length)
        {
            throw new ArgumentException(
                $"Product of shape dimensions ({product}) does not equal element count ({elements.Length}).",
                nameof(shape));
        }

        int shapeBytes = shape.Length * sizeof(int);
        int slotBlockBytes = elements.Length * ArraySlot.SizeBytes;
        byte[] buffer = new byte[shapeBytes + slotBlockBytes];
        MemoryMarshal.AsBytes(shape).CopyTo(buffer);

        // Element bytes go to the store first; the resulting (offset, length) pairs
        // populate slot bytes 0..15. Slot block sits immediately after the shape
        // prefix in the same buffer so the whole [shape][slots] block lands in one
        // contiguous arena allocation — the (_p0, _p1) the DataValue records.
        for (int i = 0; i < elements.Length; i++)
        {
            string? element = elements[i];
            if (element is null)
            {
                throw new ArgumentException(
                    $"Element {i} is null. Array<String> elements must be non-null; " +
                    "use a typed null DataValue for SQL NULL semantics at the column level.",
                    nameof(elements));
            }
            var (elementP0, elementP1) = store.StoreString(element);
            ArraySlot.Write(
                buffer.AsSpan(shapeBytes + i * ArraySlot.SizeBytes, ArraySlot.SizeBytes),
                elementP0.Value,
                elementP1.Value);
        }

        var (blockP0, blockP1) = store.StoreBytes(buffer);
        ushort cc = (ushort)(shape.Length << 8);
        return new(
            DataKind.String,
            flags: DataValueFlags.InArena | DataValueFlags.IsArray | DataValueFlags.IsMultiDim,
            offset: blockP0.Value, length: blockP1.Value, charCount: cc);
    }

    /// <summary>
    /// Returns a flat 1-D view of a multi-dim array without copying the
    /// element bytes. Pure descriptor rewrite for arena and sidecar storage —
    /// the new value's offset advances past the shape prefix, length shrinks
    /// by the same amount, and the multi-dim flag clears. For inline values
    /// the prefix bytes live inside the 16-byte payload region, so the
    /// element bytes are re-packed at the head of a fresh inline value.
    /// Returns <c>this</c> unchanged when not multi-dim.
    /// </summary>
    /// <param name="store">Required for arena-backed values (used to read the
    /// element bytes for rehashing); ignored for inline and sidecar.</param>
    /// <remarks>
    /// Arena flat arrays carry an XxHash64 over element bytes in <c>_p4+_p5</c>
    /// (see <see cref="FromArenaArray{T}"/>); multi-dim arrays carry an XxHash64
    /// over the full shape+elements block. The slice path rehashes the element
    /// bytes only so the resulting flat value is indistinguishable in the
    /// hash-fast-path from one constructed via <see cref="FromArenaArray{T}"/>.
    /// Element bytes are read but not copied — the underlying arena allocation
    /// stays in place and is shared with the source DataValue.
    /// </remarks>
    public DataValue SliceMultiDimAsFlat(IValueStore? store = null)
    {
        if (!IsMultiDim) return this;

        DataValueFlags flatFlags = _flags & ~DataValueFlags.IsMultiDim;
        int shapeBytes = ShapePrefixByteCount;

        if (IsInlineArray)
        {
            // Inline payload holds [shape prefix][elements] in 16 bytes; rebuild
            // with no prefix. InlineArrayBytes already strips the prefix.
            return FromInlineArrayBytes(InlineArrayBytes, _kind);
        }

        if (IsInSidecar)
        {
            // Sidecar: descriptor slice. _charCount low byte carries storeId
            // (preserved); high byte was ndim (cleared by dropping IsMultiDim).
            // No cached hash on sidecar arrays today, so nothing to rehash.
            long newOffset = SidecarOffset + shapeBytes;
            long newLength = SidecarLength - shapeBytes;
            return new(
                _kind, flatFlags,
                offset: newOffset, length: newLength,
                charCount: SidecarStoreId);
        }

        // Arena-backed: descriptor slice + element-bytes rehash so the flat
        // value's _p4+_p5 matches FromArenaArray's hash domain.
        if (store is null)
        {
            throw new InvalidOperationException(
                "SliceMultiDimAsFlat: arena-backed multi-dim array requires an IValueStore. " +
                "Pass the frame's Source arena.");
        }
        long arenaOffset = BackedOffset;
        long arenaLength = BackedLength;
        ReadOnlySpan<byte> allBytes = store.RetrieveUtf8Span(
            new ArenaOffset(arenaOffset), new ArenaLength(arenaLength));
        ReadOnlySpan<byte> elementBytes = allBytes[shapeBytes..];
        ulong hash = XxHash64.HashToUInt64(elementBytes);
        return new(
            _kind, flatFlags,
            offset: arenaOffset + shapeBytes,
            length: arenaLength - shapeBytes,
            hash: hash);
    }

    /// <summary>
    /// Returns the shape dimensions for a multi-dim array value. Reads the
    /// <c>int32 × ndim</c> prefix from the head of the payload (inline / arena / sidecar
    /// alike). Returns an empty span when <see cref="IsMultiDim"/> is <c>false</c>.
    /// </summary>
    /// <param name="store">Required for arena-backed values; ignored for inline / sidecar.</param>
    /// <param name="registry">Required for sidecar-backed values; ignored otherwise.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when an arena value is missing its store, or a sidecar value is missing its
    /// registry — same contract as <see cref="AsArraySpan{T}"/>.
    /// </exception>
    public ReadOnlySpan<int> GetShape(IValueStore? store = null, SidecarRegistry? registry = null)
    {
        if (!IsMultiDim) return ReadOnlySpan<int>.Empty;
        int ndim = _charCount >> 8;
        int shapeBytes = ndim * sizeof(int);

        if (IsInlineArray)
        {
            ref int head = ref Unsafe.AsRef(in _p0);
            return MemoryMarshal.CreateReadOnlySpan(ref head, ndim);
        }
        if (IsInSidecar)
        {
            ReadOnlySpan<byte> all = ReadSidecarBytes(registry);
            return MemoryMarshal.Cast<byte, int>(all[..shapeBytes]);
        }
        // Arena-backed.
        if (store is null)
        {
            throw new InvalidOperationException(
                "GetShape: arena-backed multi-dim array requires an IValueStore. " +
                "Pass the frame's Source arena.");
        }
        ReadOnlySpan<byte> bytes = store.RetrieveUtf8Span(new ArenaOffset(BackedOffset), new ArenaLength(shapeBytes));
        return MemoryMarshal.Cast<byte, int>(bytes);
    }

    /// <summary>
    /// Returns the raw payload bytes of a typed-array value, including any shape
    /// prefix (i.e. the full <c>[shape × int32][elements]</c> block for multi-dim
    /// values, or just <c>[elements]</c> for flat 1-D arrays). Used by the
    /// <c>VariableSlotPageEncoderV2</c> sidecar/inline persistence paths, which
    /// must persist the whole on-wire payload so the decoder can resurrect it.
    /// Element-only readers (<see cref="AsArraySpan{T}"/>, <see cref="InlineArrayBytes"/>)
    /// strip the prefix and are wrong for this purpose.
    /// </summary>
    /// <param name="store">Required for arena-backed values; ignored for inline / sidecar.</param>
    /// <param name="registry">Required for sidecar-backed values; ignored otherwise.</param>
    internal ReadOnlySpan<byte> RawArrayBytes(IValueStore? store, SidecarRegistry? registry = null)
    {
        if (!IsArray)
        {
            throw new InvalidOperationException(
                $"RawArrayBytes called on a non-array value (Kind={_kind}, IsArray=false).");
        }
        if (IsInlineArray)
        {
            int totalBytes = InlineArrayElementCount * ScalarByteSize(_kind) + ShapePrefixByteCount;
            ref byte head = ref Unsafe.As<int, byte>(ref Unsafe.AsRef(in _p0));
            return MemoryMarshal.CreateReadOnlySpan(ref head, totalBytes);
        }
        if (IsInSidecar)
        {
            return ReadSidecarBytes(registry);
        }
        if (store is null)
        {
            throw new InvalidOperationException(
                "RawArrayBytes: arena-backed array requires an IValueStore.");
        }
        return store.RetrieveUtf8Span(new ArenaOffset(BackedOffset), new ArenaLength(BackedLength));
    }

    /// <summary>
    /// Decoder-side constructor for an inline multi-dim array whose <paramref name="rawBytes"/>
    /// already include the shape prefix followed by element bytes — the exact form the
    /// encoder persisted. <paramref name="ndim"/> is supplied by the column's
    /// <c>FixedShape.Length</c>; the prefix is not re-parsed here, just framed by
    /// <c>_charCount</c>.
    /// </summary>
    internal static DataValue FromInlineMultiDimRawBytes(
        ReadOnlySpan<byte> rawBytes, DataKind elementKind, int ndim)
    {
        if (ndim < 2 || ndim > MultiDimMaxNdim)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ndim), ndim,
                $"Multi-dim ndim must be in [2, {MultiDimMaxNdim}].");
        }
        RejectReferenceElementKind(elementKind);

        int prefixBytes = ndim * sizeof(int);
        if (rawBytes.Length < prefixBytes || rawBytes.Length > InlineArrayMaxBytes)
        {
            throw new ArgumentException(
                $"Inline multi-dim raw payload {rawBytes.Length} bytes is outside the valid range " +
                $"[{prefixBytes}, {InlineArrayMaxBytes}] for ndim={ndim}, kind={elementKind}.",
                nameof(rawBytes));
        }
        int elementSize = ScalarByteSize(elementKind);
        int elementBytes = rawBytes.Length - prefixBytes;
        if (elementBytes % elementSize != 0)
        {
            throw new ArgumentException(
                $"Inline multi-dim element byte length {elementBytes} is not a multiple of " +
                $"size {elementSize} for {elementKind}.",
                nameof(rawBytes));
        }
        int elementCount = elementBytes / elementSize;

        Span<byte> buffer = stackalloc byte[InlineArrayMaxBytes];
        rawBytes.CopyTo(buffer);
        int p0 = MemoryMarshal.Read<int>(buffer[..4]);
        int p1 = MemoryMarshal.Read<int>(buffer[4..8]);
        int p2 = MemoryMarshal.Read<int>(buffer[8..12]);
        int p3 = MemoryMarshal.Read<int>(buffer[12..16]);

        ushort cc = (ushort)((ndim << 8) | (byte)elementCount);
        return new(
            elementKind,
            flags: DataValueFlags.IsArray | DataValueFlags.InlineArray | DataValueFlags.IsMultiDim,
            p0: p0, p1: p1, p2: p2, p3: p3, charCount: cc);
    }

    /// <summary>
    /// Decoder-side constructor for an arena-backed multi-dim array whose
    /// <paramref name="rawBytes"/> already include the shape prefix followed by
    /// element bytes. The full byte block is written into <paramref name="store"/>
    /// as a single contiguous allocation; <paramref name="ndim"/> is supplied by
    /// the column's <c>FixedShape.Length</c>.
    /// </summary>
    internal static DataValue FromArenaMultiDimRawBytes(
        ReadOnlySpan<byte> rawBytes, DataKind elementKind, int ndim, IValueStore store)
    {
        if (ndim < 2 || ndim > MultiDimMaxNdim)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ndim), ndim,
                $"Multi-dim ndim must be in [2, {MultiDimMaxNdim}].");
        }
        RejectReferenceElementKind(elementKind);

        var (p0, p1) = store.StoreBytes(rawBytes);
        // Hash the full shape+elements block — matches FromArenaMultiDimArrayBytes
        // so values built via either path hash identically.
        ulong hash = XxHash64.HashToUInt64(rawBytes);
        ushort cc = (ushort)(ndim << 8);
        return new(
            elementKind,
            flags: DataValueFlags.InArena | DataValueFlags.IsArray | DataValueFlags.IsMultiDim,
            offset: p0.Value, length: p1.Value, hash: hash, charCount: cc);
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
        // Reinterpret the payload region's first byte as ref byte, skip the shape
        // prefix (zero bytes when !IsMultiDim), then build a typed span over the
        // element region. Works without `fixed` because the ref keeps the struct
        // tracked by the GC via Unsafe.AsRef on a readonly field.
        ref byte head = ref Unsafe.As<int, byte>(ref Unsafe.AsRef(in _p0));
        ref T elements = ref Unsafe.As<byte, T>(ref Unsafe.Add(ref head, ShapePrefixByteCount));
        return MemoryMarshal.CreateReadOnlySpan(ref elements, InlineArrayElementCount);
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
            int byteCount = InlineArrayElementCount * ScalarByteSize(_kind);
            ref byte head = ref Unsafe.As<int, byte>(ref Unsafe.AsRef(in _p0));
            ref byte elements = ref Unsafe.Add(ref head, ShapePrefixByteCount);
            return MemoryMarshal.CreateReadOnlySpan(ref elements, byteCount);
        }
    }

    /// <summary>
    /// Byte size of a single element of the given primitive <see cref="DataKind"/>.
    /// Used by <see cref="InlineArrayBytes"/> to compute the active byte count from
    /// the stored element count, and by external callers (e.g.
    /// <c>ValueRef.BuildFixedWidthArray</c>) packing primitives into typed arrays.
    /// Throws for kinds without a fixed element size.
    /// </summary>
    internal static int ScalarByteSize(DataKind kind) => kind switch
    {
        DataKind.UInt8 or DataKind.Int8 or DataKind.Boolean => 1,
        DataKind.UInt16 or DataKind.Int16 or DataKind.Float16 => 2,
        DataKind.UInt32 or DataKind.Int32 or DataKind.Float32 or DataKind.Date => 4,
        DataKind.UInt64 or DataKind.Int64 or DataKind.Float64
            or DataKind.Timestamp or DataKind.TimestampTz
            or DataKind.Time or DataKind.Duration
            or DataKind.Point2D => 8,
        DataKind.Point3D => 12,
        DataKind.Uuid or DataKind.Decimal or DataKind.UInt128 or DataKind.Int128 => 16,
        _ => throw new InvalidOperationException(
            $"DataKind.{kind} has no fixed element byte size — inline arrays of this kind are not supported."),
    };

    /// <summary>
    /// Copies the inline scalar bytes of this value (a fixed-width primitive)
    /// into <paramref name="dest"/>. The number of bytes written is
    /// <see cref="ScalarByteSize"/>(<see cref="Kind"/>); the caller must
    /// supply at least that many bytes. Used by typed-array builders that
    /// need to pack heterogeneous-kind primitives without a typed span.
    /// </summary>
    /// <remarks>
    /// Reads directly from the union payload words <c>_p0..._p3</c>. Does
    /// not check <see cref="IsNull"/> or storage flags — callers must pass
    /// non-null inline scalar values.
    /// </remarks>
    internal void CopyInlineScalarBytes(Span<byte> dest)
    {
        int byteCount = ScalarByteSize(_kind);
        // Reinterpret the four payload int fields as a contiguous 16-byte block
        // and copy the leading byteCount bytes. Same idiom as AsInlineArraySpan.
        ref byte head = ref Unsafe.As<int, byte>(ref Unsafe.AsRef(in _p0));
        ReadOnlySpan<byte> source = MemoryMarshal.CreateReadOnlySpan(ref head, byteCount);
        source.CopyTo(dest);
    }

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
    ///   <item><description>Arena-backed values produced via the <c>IsArray</c> flag model: byte-level read from <paramref name="store"/>.</description></item>
    /// </list>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the value isn't an array, or when a required parameter is missing
    /// for the resolved storage path.
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

        int shapePrefix = ShapePrefixByteCount;

        if (IsInSidecar)
        {
            ReadOnlySpan<byte> sidecarBytes = ReadSidecarBytes(registry);
            return MemoryMarshal.Cast<byte, T>(sidecarBytes[shapePrefix..]);
        }

        // Arena-backed via the IsArray flag model: bytes live at (_p0, _p1) in
        // the store. Reinterpret the byte span as ReadOnlySpan<T>, skipping the
        // shape prefix when this is a multi-dim array.
        if (store is null)
        {
            throw new InvalidOperationException(
                "AsArraySpan: arena-backed array requires an IValueStore. " +
                "Pass the frame's Source arena.");
        }
        ReadOnlySpan<byte> arenaBytes = store.RetrieveUtf8Span(new ArenaOffset(BackedOffset), new ArenaLength(BackedLength));
        return MemoryMarshal.Cast<byte, T>(arenaBytes[shapePrefix..]);
    }
}
