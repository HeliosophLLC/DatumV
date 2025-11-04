using System.Buffers;
using System.Buffers.Binary;
using DatumIngest.DatumFile.Compression;
using DatumIngest.Model;

namespace DatumIngest.DatumFile.Encoding;

/// <summary>
/// Encodes <see cref="DataKind.UInt8Array"/> and <see cref="DataKind.Image"/> column pages
/// using either inline <see cref="DatumEncoding.VariableBytes"/> layout (bytes embedded in
/// the page pool) or pointer-only <see cref="DatumEncoding.SidecarBlobs"/> layout (bytes
/// routed to the companion <c>.datum-blob</c> sidecar at ingest time).
/// </summary>
/// <remarks>
/// <para>
/// Inline mode layout is identical to <see cref="StringColumnEncoder"/> with raw bytes in
/// the pool: <c>nullBitmap[ceil(N/8)] | offsets:uint32[N+1] | pool:byte[offsets[N]]</c>.
/// Sidecar mode (selected via <see cref="DatumColumnFlags.SidecarBlobs"/> on the descriptor)
/// emits only the per-row pointer pairs; see <see cref="EncodeSidecar"/>.
/// </para>
/// <para>
/// Image pages use <see cref="DatumCompression.None"/> because the image data is already
/// compressed (JPEG, PNG, etc.). UInt8Array pages use Zstd.
/// </para>
/// </remarks>
internal sealed class BinaryColumnEncoder : DatumColumnEncoder
{
    /// <inheritdoc/>
    public override DatumEncodedPage Encode(
        IReadOnlyList<DataValue> values,
        DatumColumnDescriptor descriptor,
        DatumEncoderContext context)
    {
        bool isImage = descriptor.Kind == DataKind.Image;
        DatumCompression compression = isImage ? DatumCompression.None : DatumCompression.Zstd;

        int rowCount = values.Count;
        DatumNullBitmap nullBitmap = new(rowCount);

        // Pass 1: sum per-row byte lengths and mark nulls. Read _p1 directly via the
        // new per-value byte-span accessor — no managed byte[] allocation per row.
        // For image columns (JPEGs averaging ≫85KB) this eliminates a large-object-heap
        // allocation per entry.
        int totalPoolBytes = 0;
        uint nullCount = 0;
        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            DataValue value = values[rowIndex];
            if (value.IsNull)
            {
                nullBitmap.SetNull(rowIndex);
                nullCount++;
                continue;
            }

            // _p1 holds the byte length for UInt8Array and Image payloads.
            int blobLength = value.ContentByteLength;
            totalPoolBytes += blobLength;
        }

        DatumZoneMap zoneMap = new(nullCount);

        // Sidecar-backed columns carry their (offset, length) into the .datum-blob
        // companion file. Schema is the source of truth: when the column descriptor
        // declares the column sidecar-bound, the page only preserves pointer pairs and
        // no payload bytes are copied here.
        if (descriptor.UsesSidecar)
        {
            return EncodeSidecar(values, nullBitmap, rowCount, zoneMap, descriptor);
        }

        return EncodeInline(values, context, nullBitmap, rowCount, nullCount, totalPoolBytes, zoneMap, compression);
    }

    private static DatumEncodedPage EncodeSidecar(
        IReadOnlyList<DataValue> values,
        DatumNullBitmap nullBitmap,
        int rowCount,
        DatumZoneMap zoneMap,
        DatumColumnDescriptor descriptor)
    {
        byte[] bitmapBytes = nullBitmap.ToBytes();
        int rawLength = bitmapBytes.Length + 16 * rowCount;
        byte[] raw = ArrayPool<byte>.Shared.Rent(rawLength);

        try
        {
            Buffer.BlockCopy(bitmapBytes, 0, raw, 0, bitmapBytes.Length);

            int offsetsStart = bitmapBytes.Length;
            int lengthsStart = offsetsStart + 8 * rowCount;

            for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                DataValue value = values[rowIndex];
                long offset = 0, length = 0;
                if (!value.IsNull)
                {
                    if (!value.IsInSidecar)
                    {
                        throw new InvalidOperationException(
                            $"Column '{descriptor.Name}' is declared sidecar-bound (DatumColumnFlags.SidecarBlobs) " +
                            $"but row {rowIndex} carries an in-arena DataValue. The deserializer must route this " +
                            "column's payload through the SerializationContext.LboStore.");
                    }
                    offset = value.SidecarOffset;
                    length = value.SidecarLength;
                }
                BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(offsetsStart + 8 * rowIndex), offset);
                BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(lengthsStart + 8 * rowIndex), length);
            }

            (byte[] payload, int payloadLength) = DatumCompressor.Compress(raw.AsSpan(0, rawLength), DatumCompression.Zstd);
            return new DatumEncodedPage(payload, payloadLength, DatumEncoding.SidecarBlobs, DatumCompression.Zstd, rawLength, zoneMap);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
        }
    }

    private static DatumEncodedPage EncodeInline(
        IReadOnlyList<DataValue> values,
        DatumEncoderContext context,
        DatumNullBitmap nullBitmap,
        int rowCount,
        uint nullCount,
        int totalPoolBytes,
        DatumZoneMap zoneMap,
        DatumCompression compression)
    {
        byte[] bitmapBytes = nullBitmap.ToBytes();
        int offsetsSize = (rowCount + 1) * 4;
        int rawLength = bitmapBytes.Length + offsetsSize + totalPoolBytes;
        byte[] raw = ArrayPool<byte>.Shared.Rent(rawLength);
        // When compression is None, the raw buffer is handed straight to the page
        // (image pages can be ~128 MB — the Compress(None) round-trip would rent
        // and memcpy a full duplicate of this into another LOH buffer).
        bool handedOff = false;

        try
        {
            Buffer.BlockCopy(bitmapBytes, 0, raw, 0, bitmapBytes.Length);

            int offsetWrite = bitmapBytes.Length;
            int poolWrite = offsetWrite + offsetsSize;
            uint runningOffset = 0;

            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(offsetWrite), runningOffset);
            offsetWrite += 4;

            // Pass 2: copy bytes directly from each page's store into the pooled raw
            // buffer. No per-row byte[] materialization.
            foreach (PageSpan page in context.Pages)
            {
                IValueStore pageStore = page.ArenaLength > 0
                    ? context.Store.Slice(page.ArenaBase, page.ArenaLength)
                    : context.Store;

                int endRow = page.RowStart + page.RowCount;
                for (int rowIndex = page.RowStart; rowIndex < endRow; rowIndex++)
                {
                    DataValue value = values[rowIndex];

                    if (!value.IsNull)
                    {
                        ReadOnlySpan<byte> bytes = value.AsByteSpan(pageStore);
                        bytes.CopyTo(raw.AsSpan(poolWrite, bytes.Length));
                        poolWrite += bytes.Length;
                        runningOffset += (uint)bytes.Length;
                    }

                    BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(offsetWrite), runningOffset);
                    offsetWrite += 4;
                }
            }

            if (compression == DatumCompression.None)
            {
                handedOff = true;
                return new DatumEncodedPage(raw, rawLength, DatumEncoding.VariableBytes, compression, rawLength, zoneMap);
            }

            (byte[] payload, int payloadLength) = DatumCompressor.Compress(raw.AsSpan(0, rawLength), compression);
            return new DatumEncodedPage(payload, payloadLength, DatumEncoding.VariableBytes, compression, rawLength, zoneMap);
        }
        finally
        {
            if (!handedOff) ArrayPool<byte>.Shared.Return(raw);
        }
    }

}
