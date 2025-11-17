using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using DatumIngest.DatumFile.Sidecar;
using DatumIngest.Model;

namespace DatumIngest.DatumFile.V2.Encoding;

/// <summary>
/// V2 fixed-stride scalar encoder. Lays out one page as
/// (optional) null bitmap followed by <c>stride × rowCount</c> raw payload
/// bytes. Null cells store zero in their payload slot; the bitmap is
/// authoritative.
/// </summary>
/// <remarks>
/// <para>
/// Drives all numeric scalars, <see cref="DataKind.Date"/>,
/// <see cref="DataKind.Time"/>, <see cref="DataKind.Duration"/>,
/// <see cref="DataKind.DateTime"/>, and <see cref="DataKind.Uuid"/>. The
/// stride per row is determined by
/// <see cref="ColumnDescriptorV2.FixedWidthStrideBytes"/>.
/// </para>
/// <para>
/// Booleans go through <c>BitPackedBooleanPageEncoderV2</c>; arrays
/// and variable-length kinds go through <c>VariableSlotPageEncoderV2</c>.
/// </para>
/// </remarks>
internal sealed class FixedWidthPageEncoderV2 : IPageEncoderV2
{
    private readonly DataKind _kind;
    private readonly int _strideBytes;
    private readonly bool _isNullable;
    private readonly int _pageSize;
    private readonly byte[] _payload;
    private readonly byte[]? _nullBitmap;
    private readonly PageZoneMapBuilderV2 _zoneMap = new();
    private int _rowCount;

    public FixedWidthPageEncoderV2(ColumnDescriptorV2 column, int pageSize)
    {
        if (column.Encoder != EncoderKind.FixedWidth)
        {
            throw new ArgumentException(
                $"FixedWidthPageEncoderV2 requires EncoderKind.FixedWidth, got {column.Encoder}.",
                nameof(column));
        }

        _kind = column.Kind;
        _strideBytes = column.FixedWidthStrideBytes;
        _isNullable = column.IsNullable;
        _pageSize = pageSize;
        _payload = new byte[checked(_strideBytes * pageSize)];
        _nullBitmap = _isNullable ? new byte[DatumNullBitmap.ByteCount(pageSize)] : null;
    }

    public bool IsFull => _rowCount >= _pageSize;
    public int RowCount => _rowCount;

    public void Append(DataValue value, IValueStore? store, IBlobSink? sidecar)
    {
        _ = store;
        _ = sidecar;

        if (_rowCount >= _pageSize)
        {
            throw new InvalidOperationException(
                "FixedWidthPageEncoderV2 page is full; flush before appending.");
        }

        Span<byte> dst = _payload.AsSpan(_rowCount * _strideBytes, _strideBytes);

        if (value.IsNull)
        {
            if (!_isNullable)
            {
                throw new InvalidOperationException(
                    $"Cannot write null to non-nullable column (kind={_kind}).");
            }
            _nullBitmap![_rowCount >> 3] |= (byte)(1 << (_rowCount & 7));
            // payload already zero-initialised; bitmap is authoritative.
            _zoneMap.RecordNull();
        }
        else
        {
            WriteFixedWidth(value, dst);
            _zoneMap.Record(value, store: null);
        }

        _rowCount++;
    }

    public EncodedPageV2 Flush()
    {
        int payloadBytes = _rowCount * _strideBytes;
        int bitmapBytes = _isNullable ? DatumNullBitmap.ByteCount(_rowCount) : 0;

        byte[] page = new byte[bitmapBytes + payloadBytes];
        if (_isNullable)
        {
            Buffer.BlockCopy(_nullBitmap!, 0, page, 0, bitmapBytes);
        }
        Buffer.BlockCopy(_payload, 0, page, bitmapBytes, payloadBytes);

        DatumZoneMap zoneMap = _zoneMap.Build();
        EncodedPageV2 result = new(page, _rowCount, zoneMap);

        Reset();
        return result;
    }

    private void Reset()
    {
        Array.Clear(_payload, 0, _rowCount * _strideBytes);
        if (_nullBitmap is not null)
        {
            Array.Clear(_nullBitmap, 0, DatumNullBitmap.ByteCount(_rowCount));
        }
        _zoneMap.Reset();
        _rowCount = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteFixedWidth(DataValue v, Span<byte> dst)
    {
        switch (_kind)
        {
            case DataKind.Int8:
                dst[0] = unchecked((byte)v.AsInt8());
                break;
            case DataKind.UInt8:
                dst[0] = v.AsUInt8();
                break;
            case DataKind.Int16:
                BinaryPrimitives.WriteInt16LittleEndian(dst, v.AsInt16());
                break;
            case DataKind.UInt16:
                BinaryPrimitives.WriteUInt16LittleEndian(dst, v.AsUInt16());
                break;
            case DataKind.Int32:
                BinaryPrimitives.WriteInt32LittleEndian(dst, v.AsInt32());
                break;
            case DataKind.UInt32:
                BinaryPrimitives.WriteUInt32LittleEndian(dst, v.AsUInt32());
                break;
            case DataKind.Int64:
                BinaryPrimitives.WriteInt64LittleEndian(dst, v.AsInt64());
                break;
            case DataKind.UInt64:
                BinaryPrimitives.WriteUInt64LittleEndian(dst, v.AsUInt64());
                break;
            case DataKind.Float32:
                BinaryPrimitives.WriteSingleLittleEndian(dst, v.AsFloat32());
                break;
            case DataKind.Float64:
                BinaryPrimitives.WriteDoubleLittleEndian(dst, v.AsFloat64());
                break;
            case DataKind.Date:
                BinaryPrimitives.WriteInt32LittleEndian(dst, v.AsDate().DayNumber);
                break;
            case DataKind.Time:
                BinaryPrimitives.WriteInt64LittleEndian(dst, v.AsTime().Ticks);
                break;
            case DataKind.Duration:
                BinaryPrimitives.WriteInt64LittleEndian(dst, v.AsDuration().Ticks);
                break;
            case DataKind.DateTime:
                DateTimeOffset dto = v.AsDateTime();
                // Layout matches DataValue.AsDateTime decode side: 8 bytes
                // ticks (the local-tick value DataValue stores in _p0+_p1)
                // followed by 2 bytes offset minutes (in DataValue's _p2).
                BinaryPrimitives.WriteInt64LittleEndian(dst[..8], dto.Ticks);
                BinaryPrimitives.WriteInt16LittleEndian(
                    dst.Slice(8, 2),
                    (short)(dto.Offset.Ticks / TimeSpan.TicksPerMinute));
                break;
            case DataKind.Uuid:
                if (!v.AsUuid().TryWriteBytes(dst))
                {
                    throw new InvalidOperationException(
                        "Failed to write UUID into a 16-byte fixed-width slot.");
                }
                break;
            default:
                throw new InvalidOperationException(
                    $"FixedWidth encoder cannot encode DataKind.{_kind}.");
        }
    }
}
