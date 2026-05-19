using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.DatumFile.V2.Decoding;

/// <summary>
/// V2 boolean page decoder. Random-access reader over a page laid out by
/// <see cref="V2.Encoding.BitPackedBooleanPageEncoderV2"/>: optional null
/// bitmap followed by a value bitmap.
/// </summary>
internal sealed class BitPackedBooleanPageDecoderV2 : IPageDecoderV2
{
    private readonly bool _isNullable;
    private readonly ReadOnlyMemory<byte> _pageBytes;
    private readonly int _valueBitmapOffset;

    public BitPackedBooleanPageDecoderV2(ColumnDescriptorV2 column, ReadOnlyMemory<byte> pageBytes, int rowCount, bool hasNullBitmap)
    {
        if (column.Encoder != EncoderKind.BitPackedBoolean)
        {
            throw new ArgumentException(
                $"BitPackedBooleanPageDecoderV2 requires EncoderKind.BitPackedBoolean, got {column.Encoder}.",
                nameof(column));
        }

        // Per-page bitmap-presence flag (from PageDescriptorV2); see
        // FixedWidthPageDecoderV2 for why this is per-page now.
        _isNullable = hasNullBitmap;
        _pageBytes = pageBytes;
        RowCount = rowCount;

        int bitmapBytes = DatumNullBitmap.ByteCount(rowCount);
        int requiredBytes = (_isNullable ? bitmapBytes : 0) + bitmapBytes;
        if (pageBytes.Length < requiredBytes)
        {
            throw new InvalidDataException(
                $"BitPackedBoolean page is shorter ({pageBytes.Length} bytes) than expected ({requiredBytes}).");
        }

        _valueBitmapOffset = _isNullable ? bitmapBytes : 0;
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
            return DataValue.Null(DataKind.Boolean);
        }

        bool value = (bytes[_valueBitmapOffset + byteIndex] & bitMask) != 0;
        return DataValue.FromBoolean(value);
    }
}
