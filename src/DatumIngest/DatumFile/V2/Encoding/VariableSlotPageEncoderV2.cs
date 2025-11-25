using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DatumIngest.DatumFile.Sidecar;
using DatumIngest.Model;

namespace DatumIngest.DatumFile.V2.Encoding;

/// <summary>
/// V2 variable-length encoder. Each row gets a 16-byte slot whose
/// interpretation is per-row: <em>inline</em> when the slot bytes ARE the
/// payload, or <em>pointer</em> when they decode to a sidecar
/// (offset, length, codec) coordinate.
/// </summary>
/// <remarks>
/// <para>
/// Page layout:
/// </para>
/// <code>
/// [null bitmap         : ⌈rows/8⌉ bytes]   (omitted when column is non-nullable)
/// [inline bitmap       : ⌈rows/8⌉ bytes]   bit i = 1 means row i's slot is inline
/// [inline length array : rows × 1 byte ]   per-row inline-payload length (0..16); meaningful only when inline bit is set
/// [slots               : rows × 16 bytes]  raw inline payload OR pointer struct
/// </code>
/// <para>
/// The inline-length array is required because the spec's "16 bytes ARE
/// the DataValue's <c>_p0</c>-<c>_p3</c> region" rule loses
/// <c>DataValue._charCount</c>, which is what tells the reader how many of
/// the 16 bytes are actual payload (vs zero padding) for variable-length
/// kinds. 1 KiB per 1024-row page is negligible overhead.
/// </para>
/// <para>
/// Pointer slot format (matches the format-v2 spec exactly):
/// </para>
/// <code>
/// bytes 0-7  : int64  absolute offset into .datum-blob
/// bytes 8-12 : 5-byte payload length (40-bit; max ~1 TiB)
/// bytes 13-14: reserved (zero in v1)
/// byte  15   : codec (0=Raw; only legal value in v1)
/// </code>
/// </remarks>
internal sealed class VariableSlotPageEncoderV2 : IPageEncoderV2
{
    private readonly ColumnDescriptorV2 _column;
    private readonly bool _isNullable;
    private readonly int _pageSize;
    private readonly byte[]? _nullBitmap;
    private readonly byte[] _inlineBitmap;
    private readonly byte[] _inlineLengths;
    private readonly byte[] _slots;
    private readonly PageZoneMapBuilderV2 _zoneMap = new();
    private int _rowCount;

    public VariableSlotPageEncoderV2(ColumnDescriptorV2 column, int pageSize)
    {
        if (column.Encoder != EncoderKind.VariableSlot)
        {
            throw new ArgumentException(
                $"VariableSlotPageEncoderV2 requires EncoderKind.VariableSlot, got {column.Encoder}.",
                nameof(column));
        }

        _column = column;
        _isNullable = column.IsNullable;
        _pageSize = pageSize;
        _nullBitmap = _isNullable ? new byte[DatumNullBitmap.ByteCount(pageSize)] : null;
        _inlineBitmap = new byte[DatumNullBitmap.ByteCount(pageSize)];
        _inlineLengths = new byte[pageSize];
        _slots = new byte[checked(pageSize * DatumFormatV2.VariableSlotBytes)];
    }

    public bool IsFull => _rowCount >= _pageSize;
    public int RowCount => _rowCount;

    public void Append(DataValue value, IValueStore? store, IBlobSink? sidecar)
    {
        if (_rowCount >= _pageSize)
        {
            throw new InvalidOperationException(
                "VariableSlotPageEncoderV2 page is full; flush before appending.");
        }

        int byteIndex = _rowCount >> 3;
        int bitMask = 1 << (_rowCount & 7);
        Span<byte> slot = _slots.AsSpan(_rowCount * DatumFormatV2.VariableSlotBytes, DatumFormatV2.VariableSlotBytes);

        if (value.IsNull)
        {
            if (!_isNullable)
            {
                throw new InvalidOperationException(
                    $"Cannot write null to non-nullable column (kind={_column.Kind}).");
            }
            _nullBitmap![byteIndex] |= (byte)bitMask;
            // Slot left zero; null bitmap is authoritative.
            _zoneMap.RecordNull();
        }
        else if (IsReferenceTypeArray(value))
        {
            // Reference-type arrays (Array<String|Image|Struct>) ALWAYS take the
            // sidecar path on encode — even N=0 / N=1 inline forms whose slot
            // offsets reference a transient source store. Multi-step encode:
            // each element's bytes are appended individually, the slot block is
            // built with sidecar offsets, then the slot block is appended.
            // Returns the (offset, length) of the slot block, which becomes the
            // page-level pointer slot.
            if (sidecar is null)
            {
                throw new InvalidOperationException(
                    $"Column kind {_column.Kind} produced a reference-type array DataValue but no IBlobSink " +
                    "was supplied. Reference arrays cannot be encoded inline.");
            }
            if (store is null)
            {
                throw new InvalidOperationException(
                    $"Column kind {_column.Kind} produced a reference-type array DataValue but no IValueStore " +
                    "was supplied. The encoder needs the store to read element bytes before sidecaring them.");
            }

            (long blockOffset, long blockLength) = EncodeReferenceArrayToSidecar(value, store, sidecar);
            EncodePointerSlot(slot, blockOffset, blockLength, codec: SidecarBlobCodec.Raw);
            _zoneMap.Record(value, store);
        }
        else if (value.IsInline)
        {
            // Inline path — copy the inline payload bytes into the slot.
            // The inline length is kind-specific; the helper extracts it.
            int inlineLength = ExtractInlinePayload(value, slot);
            _inlineBitmap[byteIndex] |= (byte)bitMask;
            _inlineLengths[_rowCount] = (byte)inlineLength;
            _zoneMap.Record(value, store);
        }
        else if (value.IsInSidecar)
        {
            // Fast path: the DataValue already points into a sidecar, and
            // the ingest pipeline guarantees that's THIS writer's sidecar
            // (the deserializer received it via SerializationContext.lboStore
            // and stamped storeId=0 on the value). No re-encode needed —
            // just emit a pointer slot with the existing (offset, length).
            // This is the common path for image columns where the
            // deserializer streams blob bytes directly to the sidecar
            // rather than landing them in an arena first.
            EncodePointerSlot(
                slot,
                value.SidecarOffset,
                value.SidecarLength,
                codec: SidecarBlobCodec.Raw);
            // inline bit stays 0; inline length stays 0.
            _zoneMap.Record(value, store);
        }
        else
        {
            // Arena-backed path — resolve bytes via the source store and
            // append to the sidecar. The pointer slot encodes (offset,
            // length, codec).
            if (sidecar is null)
            {
                throw new InvalidOperationException(
                    $"Column kind {_column.Kind} produced an arena-backed DataValue but no IBlobSink " +
                    "was supplied. VariableSlotPageEncoderV2 requires a sidecar to absorb non-inline payloads.");
            }
            if (store is null)
            {
                throw new InvalidOperationException(
                    $"Column kind {_column.Kind} produced an arena-backed DataValue but no IValueStore " +
                    "was supplied. The encoder needs the store to resolve the payload before sidecaring it.");
            }

            ReadOnlySpan<byte> bytes = ResolveNonInlinePayload(value, store);
            (long offset, long length) = sidecar.Append(bytes);
            EncodePointerSlot(slot, offset, length, codec: SidecarBlobCodec.Raw);
            _zoneMap.Record(value, store);
        }

        _rowCount++;
    }

    public EncodedPageV2 Flush()
    {
        int bitmapBytes = DatumNullBitmap.ByteCount(_rowCount);
        int inlineLenBytes = _rowCount;
        int slotBytes = _rowCount * DatumFormatV2.VariableSlotBytes;

        int totalBytes = (_isNullable ? bitmapBytes : 0) + bitmapBytes + inlineLenBytes + slotBytes;
        byte[] page = new byte[totalBytes];
        int offset = 0;

        if (_isNullable)
        {
            Buffer.BlockCopy(_nullBitmap!, 0, page, offset, bitmapBytes);
            offset += bitmapBytes;
        }
        Buffer.BlockCopy(_inlineBitmap, 0, page, offset, bitmapBytes);
        offset += bitmapBytes;
        Buffer.BlockCopy(_inlineLengths, 0, page, offset, inlineLenBytes);
        offset += inlineLenBytes;
        Buffer.BlockCopy(_slots, 0, page, offset, slotBytes);

        DatumZoneMap zoneMap = _zoneMap.Build();
        EncodedPageV2 result = new(page, _rowCount, zoneMap);

        Reset();
        return result;
    }

    private void Reset()
    {
        int bitmapBytes = DatumNullBitmap.ByteCount(_rowCount);
        if (_nullBitmap is not null)
        {
            Array.Clear(_nullBitmap, 0, bitmapBytes);
        }
        Array.Clear(_inlineBitmap, 0, bitmapBytes);
        Array.Clear(_inlineLengths, 0, _rowCount);
        Array.Clear(_slots, 0, _rowCount * DatumFormatV2.VariableSlotBytes);
        _zoneMap.Reset();
        _rowCount = 0;
    }

    /// <summary>
    /// Pulls the inline payload bytes out of <paramref name="value"/>
    /// and copies them into the first bytes of <paramref name="slot"/>.
    /// Returns the byte length actually written; remaining slot bytes are
    /// left zero.
    /// </summary>
    private static int ExtractInlinePayload(DataValue value, Span<byte> slot)
    {
        // Strings: UTF-8 bytes from the inline tier. AsUtf8Span
        // handles inline without consulting any store.
        if (value.Kind == DataKind.String)
        {
            ReadOnlySpan<byte> utf8 = value.AsUtf8Span(store: null!);
            utf8.CopyTo(slot);
            return utf8.Length;
        }

        // Typed inline arrays (UInt8 + IsArray + InlineArray, Float32 + IsArray + InlineArray, …).
        // InlineArrayBytes returns exactly the active payload bytes.
        if (value.IsInlineArray)
        {
            ReadOnlySpan<byte> bytes = value.InlineArrayBytes;
            bytes.CopyTo(slot);
            return bytes.Length;
        }

        throw new NotSupportedException(
            $"Inline payload extraction not implemented for DataKind.{value.Kind} " +
            "(IsArray={value.IsArray}, IsInlineArray={value.IsInlineArray}). Add a case when a column " +
            "of this kind needs an inline VariableSlot path.");
    }

    /// <summary>
    /// Resolves <paramref name="value"/>'s payload bytes for the
    /// sidecar-pointer write path. <paramref name="store"/> is required
    /// for arena-backed values; sidecar-backed inputs are not supported in
    /// v1 (would need an <see cref="IBlobSource"/> to read through).
    /// </summary>
    private ReadOnlySpan<byte> ResolveNonInlinePayload(DataValue value, IValueStore? store)
    {
        if (value.IsInSidecar)
        {
            // Caller's invariant: sidecar-backed values are handled by the
            // pass-through branch in Append above. Reaching here means a
            // future caller routed a sidecar value into the arena-resolution
            // path by mistake.
            throw new InvalidOperationException(
                $"ResolveNonInlinePayload received a sidecar-backed DataValue (kind={value.Kind}); " +
                "sidecar values should pass through Append's IsInSidecar branch, not arrive here.");
        }

        // Any fixed-width typed-array value (Kind + IsArray, arena-backed) — bytes
        // already live contiguously at (_p0, _p1) in the store, regardless of element
        // kind. AsArraySpan<byte> is the universal byte-level reader. Must run before
        // the per-kind arms so byte arrays (UInt8 + IsArray) hit this path rather than
        // a scalar arm.
        if (value.IsArray)
        {
            return value.AsArraySpan<byte>(store!);
        }

        // Arena-backed paths — kind dispatch picks the right read-side accessor.
        return value.Kind switch
        {
            DataKind.String
                => value.AsUtf8Span(store!),

            DataKind.Image
                => value.AsByteSpan(store!),

            DataKind.Struct
                => SerializeStructFields(value, store!),

            // Legacy heterogeneous-element DataKind.Array follows the same
            // packed-fields encoding as Struct.
            DataKind.Array
                => SerializeArrayFields(value, store!),

            _ => throw new NotSupportedException(
                $"VariableSlot non-inline payload extraction not implemented for DataKind.{value.Kind}. " +
                "Add a case when a column of this kind needs sidecar storage."),
        };
    }

    /// <summary>
    /// Serializes a struct's fields to a byte buffer using the existing
    /// <see cref="IO.DataValueWriter"/> wire format. Layout: uint16
    /// fieldCount + N records (kind byte + payload). The decoder reverses
    /// it using <see cref="IO.DataValueReader.ReadDataValue(BinaryReader, IValueStore)"/>
    /// against a per-query arena.
    /// </summary>
    private static byte[] SerializeStructFields(DataValue value, IValueStore store)
    {
        DataValue[] fields = value.AsStruct(store);
        using MemoryStream ms = new();
        using BinaryWriter bw = new(ms, System.Text.Encoding.UTF8, leaveOpen: false);
        bw.Write((ushort)fields.Length);
        foreach (DataValue field in fields)
        {
            IO.DataValueWriter.WriteDataValue(bw, field, store);
        }
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// Serializes a typed array's elements to a byte buffer. Layout:
    /// byte elementKind + uint32 elementCount + N records. Mirrors the
    /// Struct encoding above but tags the homogeneous element kind so the
    /// decoder can reconstruct the array's element-kind field.
    /// </summary>
    private static byte[] SerializeArrayFields(DataValue value, IValueStore store)
    {
        DataValue[] elements = value.AsArray(store);
        using MemoryStream ms = new();
        using BinaryWriter bw = new(ms, System.Text.Encoding.UTF8, leaveOpen: false);
        bw.Write((byte)value.ArrayElementKind);
        bw.Write((uint)elements.Length);
        foreach (DataValue element in elements)
        {
            DatumIngest.IO.DataValueWriter.WriteDataValue(bw, element, store);
        }
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// Writes the 16-byte sidecar pointer struct into <paramref name="slot"/>.
    /// Layout matches <see cref="DatumFormatV2.PointerSlot"/>.
    /// </summary>
    internal static void EncodePointerSlot(Span<byte> slot, long offset, long length, SidecarBlobCodec codec)
    {
        if (slot.Length < DatumFormatV2.VariableSlotBytes)
        {
            throw new ArgumentException(
                $"Pointer slot must be {DatumFormatV2.VariableSlotBytes} bytes.", nameof(slot));
        }
        const long lengthMax = (1L << 40) - 1;
        if (length < 0 || length > lengthMax)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length), length,
                $"Sidecar payload length must be in [0, {lengthMax}] (5-byte cap).");
        }
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset), offset, "Sidecar payload offset must be non-negative.");
        }

        BinaryPrimitives.WriteInt64LittleEndian(slot[..8], offset);

        // 5-byte length: write low 4 bytes as uint32, then byte 4 as the high byte.
        BinaryPrimitives.WriteUInt32LittleEndian(slot.Slice(8, 4), unchecked((uint)length));
        slot[12] = (byte)((length >> 32) & 0xFF);

        // bytes 13-14 reserved (already zero from page init / Reset).
        slot[13] = 0;
        slot[14] = 0;

        slot[15] = (byte)codec;
    }

    /// <summary>
    /// Returns whether <paramref name="value"/> is a reference-type array — one
    /// whose <see cref="DataKind"/> indicates per-element variable-length payloads
    /// referenced by slot pointers (<see cref="DataKind.String"/>,
    /// <see cref="DataKind.Image"/>, <see cref="DataKind.Struct"/> — each plus
    /// <see cref="DataValue.IsArray"/>). Distinct from fixed-width typed arrays
    /// (UInt8 / Int32 / Float32 / etc. + IsArray) where slot bytes are the
    /// element bytes directly.
    /// </summary>
    private static bool IsReferenceTypeArray(DataValue value)
    {
        if (!value.IsArray) return false;
        return value.Kind is DataKind.String or DataKind.Image or DataKind.Struct;
    }

    /// <summary>
    /// Multi-step encoder for reference-type arrays: appends each element's bytes
    /// to <paramref name="sidecar"/> individually, builds a slot block with the
    /// resulting sidecar offsets, and appends the slot block. Returns the slot
    /// block's (offset, length) which becomes the page-level pointer slot.
    /// </summary>
    /// <remarks>
    /// Per <c>project_reference_type_arrays.md</c>: an array's slots all resolve
    /// against its single backing store. After this encode, that store is the
    /// sidecar — every element's bytes and the slot block itself are sidecar-
    /// resident.
    /// </remarks>
    private static (long offset, long length) EncodeReferenceArrayToSidecar(
        DataValue value,
        IValueStore store,
        IBlobSink sidecar)
    {
        return value.Kind switch
        {
            DataKind.String => EncodeStringArrayToSidecar(value.AsStringArray(store), sidecar),
            DataKind.Image => EncodeImageArrayToSidecar(value.AsImageArray(store), sidecar),
            DataKind.Struct => EncodeStructArrayToSidecar(value.AsStructArray(store), store, sidecar),
            _ => throw new NotSupportedException(
                $"EncodeReferenceArrayToSidecar does not handle Array<{value.Kind}>."),
        };
    }

    private static (long offset, long length) EncodeStringArrayToSidecar(
        string[] elements,
        IBlobSink sidecar)
    {
        byte[] slotBlock = new byte[elements.Length * ArraySlot.SizeBytes];
        for (int i = 0; i < elements.Length; i++)
        {
            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(elements[i]);
            (long elementOffset, long elementLength) = sidecar.Append(utf8);
            ArraySlot.Write(
                slotBlock.AsSpan(i * ArraySlot.SizeBytes, ArraySlot.SizeBytes),
                elementOffset,
                elementLength);
        }
        return sidecar.Append(slotBlock);
    }

    private static (long offset, long length) EncodeImageArrayToSidecar(
        byte[][] elements,
        IBlobSink sidecar)
    {
        byte[] slotBlock = new byte[elements.Length * ArraySlot.SizeBytes];
        for (int i = 0; i < elements.Length; i++)
        {
            (long elementOffset, long elementLength) = sidecar.Append(elements[i]);
            ArraySlot.Write(
                slotBlock.AsSpan(i * ArraySlot.SizeBytes, ArraySlot.SizeBytes),
                elementOffset,
                elementLength);
        }
        return sidecar.Append(slotBlock);
    }

    private static (long offset, long length) EncodeStructArrayToSidecar(
        DataValue[][] elements,
        IValueStore store,
        IBlobSink sidecar)
    {
        byte[] slotBlock = new byte[elements.Length * ArraySlot.SizeBytes];
        for (int i = 0; i < elements.Length; i++)
        {
            // Reuse the existing self-describing struct serialisation
            // (uint16 fieldCount + N records). Each struct element ends up as
            // its own opaque byte payload in the sidecar.
            byte[] structBytes = SerializeStructFieldArray(elements[i], store);
            (long elementOffset, long elementLength) = sidecar.Append(structBytes);
            ArraySlot.Write(
                slotBlock.AsSpan(i * ArraySlot.SizeBytes, ArraySlot.SizeBytes),
                elementOffset,
                elementLength);
        }
        return sidecar.Append(slotBlock);
    }

    /// <summary>
    /// Serialises a struct's <see cref="DataValue"/>[] fields to the same
    /// uint16-fieldCount + per-field self-describing format used by
    /// <see cref="SerializeStructFields(DataValue, IValueStore)"/>. Split out so
    /// reference-array encode paths can call it directly without round-tripping
    /// through a synthetic struct DataValue.
    /// </summary>
    private static byte[] SerializeStructFieldArray(DataValue[] fields, IValueStore store)
    {
        using MemoryStream ms = new();
        using BinaryWriter bw = new(ms, System.Text.Encoding.UTF8, leaveOpen: false);
        bw.Write((ushort)fields.Length);
        foreach (DataValue field in fields)
        {
            IO.DataValueWriter.WriteDataValue(bw, field, store);
        }
        bw.Flush();
        return ms.ToArray();
    }
}
