using DatumIngest.DatumFile.Sidecar;
using DatumIngest.Model;

namespace DatumIngest.DatumFile.V2.Encoding;

/// <summary>
/// V2 boolean encoder. Lays out one page as two parallel bitmaps —
/// a null bitmap (when nullable) followed by a value bitmap. Bit
/// <c>i</c> of either bitmap corresponds to row <c>i</c>: a set bit in
/// the null bitmap means the row is null; a set bit in the value bitmap
/// means the boolean is <c>true</c>.
/// </summary>
/// <remarks>
/// At 1024 rows per page the page is 256 bytes when nullable (128 nulls
/// + 128 values) and 128 bytes when non-nullable. The 8× density reduction
/// over <see cref="FixedWidthPageEncoderV2"/> (which would write a full
/// byte per boolean) is the reason booleans get their own encoder rather
/// than going through the fixed-width path.
/// </remarks>
internal sealed class BitPackedBooleanPageEncoderV2 : IPageEncoderV2
{
    private readonly bool _isNullable;
    private readonly int _pageSize;
    private readonly byte[]? _nullBitmap;
    private readonly byte[] _valueBitmap;
    private readonly PageZoneMapBuilderV2 _zoneMap = new();
    private int _rowCount;

    public BitPackedBooleanPageEncoderV2(ColumnDescriptorV2 column, int pageSize)
    {
        if (column.Encoder != EncoderKind.BitPackedBoolean)
        {
            throw new ArgumentException(
                $"BitPackedBooleanPageEncoderV2 requires EncoderKind.BitPackedBoolean, got {column.Encoder}.",
                nameof(column));
        }
        if (column.Kind != DataKind.Boolean)
        {
            throw new ArgumentException(
                $"BitPackedBooleanPageEncoderV2 requires DataKind.Boolean, got {column.Kind}.",
                nameof(column));
        }

        _isNullable = column.IsNullable;
        _pageSize = pageSize;
        _nullBitmap = _isNullable ? new byte[DatumNullBitmap.ByteCount(pageSize)] : null;
        _valueBitmap = new byte[DatumNullBitmap.ByteCount(pageSize)];
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
                "BitPackedBooleanPageEncoderV2 page is full; flush before appending.");
        }

        int byteIndex = _rowCount >> 3;
        int bitMask = 1 << (_rowCount & 7);

        if (value.IsNull)
        {
            if (!_isNullable)
            {
                throw new InvalidOperationException(
                    "Cannot write null to non-nullable Boolean column.");
            }
            _nullBitmap![byteIndex] |= (byte)bitMask;
            // Value bit left zero; null bitmap is authoritative.
            _zoneMap.RecordNull();
        }
        else
        {
            if (value.AsBoolean())
            {
                _valueBitmap[byteIndex] |= (byte)bitMask;
            }
            _zoneMap.Record(value, store: null);
        }

        _rowCount++;
    }

    public EncodedPageV2 Flush()
    {
        int bitmapBytes = DatumNullBitmap.ByteCount(_rowCount);
        int totalBytes = (_isNullable ? bitmapBytes : 0) + bitmapBytes;
        byte[] page = new byte[totalBytes];

        int offset = 0;
        if (_isNullable)
        {
            Buffer.BlockCopy(_nullBitmap!, 0, page, offset, bitmapBytes);
            offset += bitmapBytes;
        }
        Buffer.BlockCopy(_valueBitmap, 0, page, offset, bitmapBytes);

        DatumZoneMap zoneMap = _zoneMap.Build();
        EncodedPageV2 result = new(page, _rowCount, zoneMap);

        Reset();
        return result;
    }

    private void Reset()
    {
        int clearedBytes = DatumNullBitmap.ByteCount(_rowCount);
        if (_nullBitmap is not null)
        {
            Array.Clear(_nullBitmap, 0, clearedBytes);
        }
        Array.Clear(_valueBitmap, 0, clearedBytes);
        _zoneMap.Reset();
        _rowCount = 0;
    }
}
