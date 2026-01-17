using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using DatumIngest.DatumFile.Sidecar;
using DatumIngest.IO;
using DatumIngest.Model;

namespace DatumIngest.DatumFile.V2.Decoding;

/// <summary>
/// V2 variable-length page decoder. Random-access reader over a page laid
/// out by <see cref="V2.Encoding.VariableSlotPageEncoderV2"/>: null bitmap
/// (when nullable) + inline bitmap + per-row inline-length array +
/// 16-byte slots.
/// </summary>
/// <remarks>
/// For inline rows the decoder constructs a self-contained DataValue from
/// the slot bytes (sliced to the row's inline-length entry). For pointer
/// rows it constructs a sidecar-backed DataValue carrying offset/length/
/// codec — the caller resolves bytes through the per-query
/// <see cref="DatumFile.Sidecar.SidecarRegistry"/> using
/// <paramref>storeId</paramref>.
/// </remarks>
internal sealed class VariableSlotPageDecoderV2 : IPageDecoderV2
{
    private readonly ColumnDescriptorV2 _column;
    private readonly ReadOnlyMemory<byte> _pageBytes;
    private readonly bool _isNullable;
    private readonly int _inlineBitmapOffset;
    private readonly int _inlineLengthsOffset;
    private readonly int _slotsOffset;
    private readonly byte _sidecarStoreId;
    private readonly IBlobSource? _sidecarSource;
    private readonly IValueStore? _eagerStore;
    private readonly ushort _columnRuntimeStructTypeId;

    public VariableSlotPageDecoderV2(
        ColumnDescriptorV2 column,
        ReadOnlyMemory<byte> pageBytes,
        int rowCount,
        byte sidecarStoreId,
        IBlobSource? sidecarSource = null,
        IValueStore? eagerStore = null,
        ushort columnRuntimeStructTypeId = 0)
    {
        _sidecarSource = sidecarSource;
        _eagerStore = eagerStore;
        _columnRuntimeStructTypeId = columnRuntimeStructTypeId;

        if (column.Encoder != EncoderKind.VariableSlot)
        {
            throw new ArgumentException(
                $"VariableSlotPageDecoderV2 requires EncoderKind.VariableSlot, got {column.Encoder}.",
                nameof(column));
        }

        _column = column;
        _pageBytes = pageBytes;
        RowCount = rowCount;
        _isNullable = column.IsNullable;
        _sidecarStoreId = sidecarStoreId;

        int bitmapBytes = DatumNullBitmap.ByteCount(rowCount);
        int offset = 0;
        if (_isNullable) offset += bitmapBytes;
        _inlineBitmapOffset = offset;
        offset += bitmapBytes;
        _inlineLengthsOffset = offset;
        offset += rowCount;
        _slotsOffset = offset;

        int requiredBytes = offset + rowCount * DatumFormatV2.VariableSlotBytes;
        if (pageBytes.Length < requiredBytes)
        {
            throw new InvalidDataException(
                $"VariableSlot page is shorter ({pageBytes.Length} bytes) than expected ({requiredBytes}).");
        }
    }

    public int RowCount { get; }

    public DataValue ReadValue(int rowIndex)
    {
        if ((uint)rowIndex >= (uint)RowCount)
        {
            throw new ArgumentOutOfRangeException(nameof(rowIndex), rowIndex, $"Row index out of range (0..{RowCount - 1}).");
        }

        ReadOnlySpan<byte> bytes = _pageBytes.Span;
        int byteIndex = rowIndex >> 3;
        int bitMask = 1 << (rowIndex & 7);

        if (_isNullable && (bytes[byteIndex] & bitMask) != 0)
        {
            return DataValue.Null(_column.Kind);
        }

        ReadOnlySpan<byte> slot = bytes.Slice(_slotsOffset + rowIndex * DatumFormatV2.VariableSlotBytes, DatumFormatV2.VariableSlotBytes);
        bool inline = (bytes[_inlineBitmapOffset + byteIndex] & bitMask) != 0;

        if (inline)
        {
            byte length = bytes[_inlineLengthsOffset + rowIndex];
            return DecodeInline(slot[..length]);
        }
        return DecodePointer(slot);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private DataValue DecodeInline(ReadOnlySpan<byte> payload)
    {
        if (_column.Kind == DataKind.String)
        {
            int charCount = System.Text.Encoding.UTF8.GetCharCount(payload);
            return DataValue.FromUtf8Span(payload, charCount, store: null!);
        }

        // All fixed-width typed-array kinds (UInt8/Int8/.../Float64, plus the new
        // Float16/Decimal/Int128/UInt128) share the same inline payload layout: a
        // contiguous run of element bytes packed into _p0–_p3. FromInlineArrayBytes
        // derives the element count from payload length and the kind's element size.
        if (_column.IsArray)
        {
            return DataValue.FromInlineArrayBytes(payload, _column.Kind);
        }

        throw new InvalidDataException(
            $"VariableSlot inline decode not implemented for column '{_column.Name}' " +
            $"(kind={_column.Kind}, isArray={_column.IsArray}).");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private DataValue DecodePointer(ReadOnlySpan<byte> slot)
    {
        long offset = BinaryPrimitives.ReadInt64LittleEndian(slot[..8]);
        long length = BinaryPrimitives.ReadUInt32LittleEndian(slot.Slice(8, 4))
            | ((long)slot[12] << 32);
        SidecarBlobCodec codec = (SidecarBlobCodec)slot[15];
        if (codec != SidecarBlobCodec.Raw)
        {
            throw new InvalidDataException(
                $"Sidecar pointer at column '{_column.Name}' carries codec={codec}; v1 readers only support Raw. " +
                "This file was probably written by a future v2.x writer.");
        }

        // Per-kind sidecar DataValue construction. Kinds without a sidecar
        // factory throw — adding one is a small DataValue.cs change when the
        // first scenario lands.
        // Any typed-array column (Kind + IsArray) — fixed-width OR reference-type —
        // routes through the generic sidecar factory: the bytes (or slot block) live
        // at (offset, length) in the sidecar. Reference-type accessors
        // (AsStringArray / AsImageArray / AsStructArray) resolve per-element bytes
        // through the SidecarRegistry on demand, so no eager copy into an arena is
        // needed at decode time. Pass-through queries that don't materialise the
        // array stay zero-copy.
        if (_column.IsArray)
        {
            return DataValue.FromArrayInSidecar(_column.Kind, offset, length, _sidecarStoreId);
        }

        return _column.Kind switch
        {
            DataKind.String
                => DataValue.FromStringInSidecar(offset, length, _sidecarStoreId),
            DataKind.Image
                => DataValue.FromImageInSidecar(offset, length, _sidecarStoreId),
            DataKind.Audio
                => DataValue.FromAudioInSidecar(offset, length, _sidecarStoreId),
            DataKind.Video
                => DataValue.FromVideoInSidecar(offset, length, _sidecarStoreId),
            DataKind.Json
                => DataValue.FromJsonInSidecar(offset, length, _sidecarStoreId),
            DataKind.Struct
                => DecodeStructEagerly(offset, length),
            _ => throw new InvalidDataException(
                $"VariableSlot pointer decode not implemented for column '{_column.Name}' " +
                $"(kind={_column.Kind}, isArray={_column.IsArray}). Add a sidecar DataValue factory for this kind."),
        };
    }

    /// <summary>
    /// Eagerly materializes a struct from sidecar bytes: reads the
    /// uint16 field count, deserializes each field via
    /// <see cref="DataValueReader.ReadDataValue(BinaryReader, IValueStore)"/>
    /// against <see cref="_eagerStore"/>, and stores the field array in
    /// the same store. The returned DataValue is arena-backed (kind =
    /// Struct, payload = (offset, count) into <see cref="_eagerStore"/>),
    /// so subsequent <c>AsStruct(eagerStore)</c> calls work normally.
    /// </summary>
    private DataValue DecodeStructEagerly(long offset, long length)
    {
        if (_sidecarSource is null || _eagerStore is null)
        {
            throw new InvalidOperationException(
                "Decoding a sidecar-backed Struct requires both an IBlobSource and an " +
                "IValueStore at decoder construction. Pass sidecarSource and eagerStore " +
                "to OpenPageDecoder when reading Struct columns.");
        }

        ReadOnlySpan<byte> bytes = _sidecarSource.Read(offset, length);
        byte[] copy = bytes.ToArray();
        using MemoryStream ms = new(copy, writable: false);
        using BinaryReader br = new(ms, System.Text.Encoding.UTF8, leaveOpen: false);
        ushort fieldCount = br.ReadUInt16();
        DataValue[] fields = new DataValue[fieldCount];
        for (int i = 0; i < fieldCount; i++)
        {
            fields[i] = DataValueReader.ReadDataValue(br, _eagerStore);
        }
        // Stamp the column's runtime struct TypeId — translated from the
        // file's on-disk id at scan-init via EnsureTypeTableLoaded. Falls
        // back to FromUntypedStruct when the column has no registered type
        // (v4 file with no type table, or v5 file whose type table didn't
        // include this column for some reason).
        if (_columnRuntimeStructTypeId == 0)
        {
            return DataValue.FromUntypedStruct(fields, _eagerStore);
        }
        return DataValue.FromStruct(fields, _eagerStore, _columnRuntimeStructTypeId);
    }

}
