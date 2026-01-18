using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using DatumIngest.Model;

namespace DatumIngest.DatumFile.V2.Decoding;

/// <summary>
/// V2 fixed-width page decoder. Random-access reader over a page laid out
/// by <see cref="V2.Encoding.FixedWidthPageEncoderV2"/>: optional null
/// bitmap followed by <c>stride × rowCount</c> raw payload bytes.
/// </summary>
internal sealed class FixedWidthPageDecoderV2 : IPageDecoderV2
{
    private readonly DataKind _kind;
    private readonly int _strideBytes;
    private readonly bool _isNullable;
    private readonly ReadOnlyMemory<byte> _pageBytes;
    private readonly int _payloadOffset;

    public FixedWidthPageDecoderV2(ColumnDescriptorV2 column, ReadOnlyMemory<byte> pageBytes, int rowCount)
    {
        if (column.Encoder != EncoderKind.FixedWidth)
        {
            throw new ArgumentException(
                $"FixedWidthPageDecoderV2 requires EncoderKind.FixedWidth, got {column.Encoder}.",
                nameof(column));
        }

        _kind = column.Kind;
        _strideBytes = column.FixedWidthStrideBytes;
        _isNullable = column.IsNullable;
        _pageBytes = pageBytes;
        RowCount = rowCount;

        int bitmapBytes = _isNullable ? DatumNullBitmap.ByteCount(rowCount) : 0;
        int requiredBytes = bitmapBytes + rowCount * _strideBytes;
        if (pageBytes.Length < requiredBytes)
        {
            throw new InvalidDataException(
                $"FixedWidth page is shorter ({pageBytes.Length} bytes) than expected " +
                $"({requiredBytes} for {rowCount} rows × {_strideBytes} stride).");
        }

        _payloadOffset = bitmapBytes;
    }

    public int RowCount { get; }

    public DataValue ReadValue(int rowIndex)
    {
        if ((uint)rowIndex >= (uint)RowCount)
        {
            throw new ArgumentOutOfRangeException(nameof(rowIndex), rowIndex, $"Row index out of range (0..{RowCount - 1}).");
        }

        if (_isNullable && IsNullBitSet(rowIndex))
        {
            return DataValue.Null(_kind);
        }

        ReadOnlySpan<byte> slot = _pageBytes.Span.Slice(_payloadOffset + rowIndex * _strideBytes, _strideBytes);
        return DecodeFixedWidth(slot);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsNullBitSet(int rowIndex)
    {
        byte b = _pageBytes.Span[rowIndex >> 3];
        return (b & (1 << (rowIndex & 7))) != 0;
    }

    private DataValue DecodeFixedWidth(ReadOnlySpan<byte> slot)
    {
        return _kind switch
        {
            DataKind.Int8 => DataValue.FromInt8(unchecked((sbyte)slot[0])),
            DataKind.UInt8 => DataValue.FromUInt8(slot[0]),
            DataKind.Int16 => DataValue.FromInt16(BinaryPrimitives.ReadInt16LittleEndian(slot)),
            DataKind.UInt16 => DataValue.FromUInt16(BinaryPrimitives.ReadUInt16LittleEndian(slot)),
            DataKind.Int32 => DataValue.FromInt32(BinaryPrimitives.ReadInt32LittleEndian(slot)),
            DataKind.UInt32 => DataValue.FromUInt32(BinaryPrimitives.ReadUInt32LittleEndian(slot)),
            DataKind.Int64 => DataValue.FromInt64(BinaryPrimitives.ReadInt64LittleEndian(slot)),
            DataKind.UInt64 => DataValue.FromUInt64(BinaryPrimitives.ReadUInt64LittleEndian(slot)),
            DataKind.Float32 => DataValue.FromFloat32(BinaryPrimitives.ReadSingleLittleEndian(slot)),
            DataKind.Float64 => DataValue.FromFloat64(BinaryPrimitives.ReadDoubleLittleEndian(slot)),
            DataKind.Date => DataValue.FromDate(DateOnly.FromDayNumber(BinaryPrimitives.ReadInt32LittleEndian(slot))),
            DataKind.Time => DataValue.FromTime(new TimeOnly(BinaryPrimitives.ReadInt64LittleEndian(slot))),
            DataKind.Duration => DataValue.FromDuration(new TimeSpan(BinaryPrimitives.ReadInt64LittleEndian(slot))),
            DataKind.DateTime => DecodeDateTime(slot),
            DataKind.Uuid => DataValue.FromUuid(new Guid(slot[..16])),
            DataKind.Point2D => DataValue.FromPoint2D(
                BinaryPrimitives.ReadSingleLittleEndian(slot[..4]),
                BinaryPrimitives.ReadSingleLittleEndian(slot.Slice(4, 4))),
            DataKind.Point3D => DataValue.FromPoint3D(
                BinaryPrimitives.ReadSingleLittleEndian(slot[..4]),
                BinaryPrimitives.ReadSingleLittleEndian(slot.Slice(4, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(slot.Slice(8, 4))),
            _ => throw new InvalidDataException(
                $"FixedWidth decoder cannot decode DataKind.{_kind}."),
        };
    }

    private static DataValue DecodeDateTime(ReadOnlySpan<byte> slot)
    {
        long ticks = BinaryPrimitives.ReadInt64LittleEndian(slot[..8]);
        short offsetMinutes = BinaryPrimitives.ReadInt16LittleEndian(slot.Slice(8, 2));
        return DataValue.FromDateTime(new DateTimeOffset(ticks, new TimeSpan(offsetMinutes * TimeSpan.TicksPerMinute)));
    }
}
