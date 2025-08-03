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
        DatumNullBitmap nullBitmap = new(rowCount);
        byte[] guidBytes = new byte[rowCount * GuidByteSize];
        uint nullCount = 0;

        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            DataValue value = values[rowIndex];

            if (value.IsNull)
            {
                nullBitmap.SetNull(rowIndex);
                nullCount++;
                // Destination bytes are already zeroed by array initialisation.
            }
            else
            {
                Guid guid = value.AsUuid();
                // MemoryMarshal.TryWrite gives a direct 16-byte copy without heap allocation.
                MemoryMarshal.TryWrite(guidBytes.AsSpan(rowIndex * GuidByteSize), in guid);
            }
        }

        // UUIDs are effectively random — zone map min/max would have no predicate pushdown value.
        DatumZoneMap zoneMap = new(nullCount, null, null);

        byte[] bitmapBytes = nullBitmap.ToBytes();
        byte[] raw = new byte[bitmapBytes.Length + guidBytes.Length];
        bitmapBytes.CopyTo(raw, 0);
        guidBytes.CopyTo(raw, bitmapBytes.Length);

        byte[] compressed = DatumCompressor.Compress(raw, DatumCompression.Zstd);

        return new DatumEncodedPage(compressed, DatumEncoding.Raw, DatumCompression.Zstd, raw.Length, zoneMap);
    }
}
