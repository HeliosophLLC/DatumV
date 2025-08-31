using System.Buffers;
using System.Runtime.InteropServices;
using DatumIngest.DatumFile.Compression;
using DatumIngest.Model;

namespace DatumIngest.DatumFile.Encoding;

/// <summary>
/// Encodes a <see cref="DataKind.Uuid"/> column page using <see cref="DatumEncoding.Raw"/>
/// layout with Zstd compression.
/// </summary>
/// <remarks>
/// Layout of the uncompressed payload: <c>nullBitmap[ceil(N/8)] | bytes[N * 16]</c>.
/// Each UUID occupies exactly 16 bytes in little-endian <see cref="Guid"/> layout
/// (matching the in-memory representation on .NET). Null rows store 16 zero bytes.
/// Uuid columns do not carry useful min/max zone maps; only the null count is tracked.
/// </remarks>
internal sealed class UuidColumnEncoder : DatumColumnEncoder
{
    private const int GuidByteSize = 16;

    /// <inheritdoc/>
    public override DatumEncodedPage Encode(
        IReadOnlyList<DataValue> values,
        DatumColumnDescriptor descriptor,
        DatumEncoderContext context)
    {
        int rowCount = values.Count;
        int bitmapLength = DatumNullBitmap.ByteCount(rowCount);
        int guidDataLength = rowCount * GuidByteSize;
        int rawLength = bitmapLength + guidDataLength;

        DatumNullBitmap nullBitmap = new(rowCount);
        byte[] raw = ArrayPool<byte>.Shared.Rent(rawLength);

        try
        {
            // Zero the GUID region; null rows must store 16 zero bytes.
            Array.Clear(raw, bitmapLength, guidDataLength);
            uint nullCount = 0;

            for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                DataValue value = values[rowIndex];

                if (value.IsNull)
                {
                    nullBitmap.SetNull(rowIndex);
                    nullCount++;
                }
                else
                {
                    Guid guid = value.AsUuid();
                    MemoryMarshal.TryWrite(raw.AsSpan(bitmapLength + rowIndex * GuidByteSize), in guid);
                }
            }

            // UUIDs are effectively random — zone map min/max would have no predicate pushdown value.
            DatumZoneMap zoneMap = new(nullCount, null, null);

            Buffer.BlockCopy(nullBitmap.ToBytes(), 0, raw, 0, bitmapLength);

            byte[] compressed = DatumCompressor.Compress(raw.AsSpan(0, rawLength), DatumCompression.Zstd);

            return new DatumEncodedPage(compressed, DatumEncoding.Raw, DatumCompression.Zstd, rawLength, zoneMap);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
        }
    }
}
