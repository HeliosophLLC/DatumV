using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using DatumIngest.DatumFile.Compression;
using DatumIngest.Model;

namespace DatumIngest.DatumFile.Encoding;

/// <summary>
/// Encodes <see cref="DataKind.UInt8Array"/> and <see cref="DataKind.Image"/> column pages
/// using <see cref="DatumEncoding.VariableBytes"/> layout.
/// </summary>
/// <remarks>
/// <para>
/// When no blob in the row group exceeds the column's externalization threshold, the layout is
/// identical to <see cref="StringColumnEncoder"/> with raw bytes in the pool:
/// <c>nullBitmap[ceil(N/8)] | offsets:uint32[N+1] | pool:byte[offsets[N]]</c>.
/// </para>
/// <para>
/// When any blob in the row group exceeds <see cref="DatumColumnDescriptor.ExternalizationThresholdBytes"/>,
/// the entire column page is externalized: each blob is written to a sidecar file at
/// <c>{DatumFilePath}.datum_blobs/{columnName}/{rowGroupIndex}_{blobIndex}{ext}</c>, and the pool
/// stores the relative UTF-8 path strings instead of the raw bytes. The
/// <see cref="DatumColumnFlags.ExternBlobs"/> flag in the descriptor signals this mode to the decoder.
/// </para>
/// <para>
/// Image pages use <see cref="DatumCompression.None"/> because the image data is already
/// compressed (JPEG, PNG, etc.). UInt8Array pages use Zstd.
/// </para>
/// </remarks>
internal sealed class BinaryColumnEncoder : DatumColumnEncoder
{
    private static readonly string ImageExtension = ".dat";

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
        long maxBlobSize = 0;
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
            int blobLength = value.StringOrBinaryByteLength;
            totalPoolBytes += blobLength;
            if (blobLength > maxBlobSize) maxBlobSize = blobLength;
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

        bool externalize = descriptor.ExternalizesBlobs && maxBlobSize > descriptor.ExternalizationThresholdBytes;

        if (externalize)
        {
            return EncodeExternalized(values, descriptor, context, nullBitmap, rowCount, nullCount, zoneMap, isImage, compression);
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

    private static DatumEncodedPage EncodeExternalized(
        IReadOnlyList<DataValue> values,
        DatumColumnDescriptor descriptor,
        DatumEncoderContext context,
        DatumNullBitmap nullBitmap,
        int rowCount,
        uint nullCount,
        DatumZoneMap zoneMap,
        bool isImage,
        DatumCompression compression)
    {
        string sidecarRoot = context.DatumFilePath + DatumFileConstants.BlobsFolderSuffix;
        string columnSidecarDir = Path.Combine(sidecarRoot, descriptor.Name);
        Directory.CreateDirectory(columnSidecarDir);

        // Paths stored in the page pool are relative to the .datum file's directory.
        string datumFileDir = Path.GetDirectoryName(context.DatumFilePath) ?? string.Empty;
        byte[][] pathBytes = ArrayPool<byte[]>.Shared.Rent(rowCount);
        int blobIndex = 0;

        try
        {
            // Walk pages once: for each non-null entry, stream its bytes straight to the
            // sidecar file via FileStream.Write(span) — no managed byte[] materialization
            // of the blob itself. Only the path is materialized (UTF-8 encoded) for
            // inclusion in the column-page pool.
            foreach (PageSpan page in context.Pages)
            {
                IValueStore pageStore = page.ArenaLength > 0
                    ? context.Store.Slice(page.ArenaBase, page.ArenaLength)
                    : context.Store;

                int endRow = page.RowStart + page.RowCount;
                for (int rowIndex = page.RowStart; rowIndex < endRow; rowIndex++)
                {
                    if (nullBitmap.IsNull(rowIndex))
                    {
                        pathBytes[rowIndex] = [];
                        continue;
                    }

                    ReadOnlySpan<byte> blob = values[rowIndex].AsByteSpan(pageStore);
                    string fileName = $"{context.RowGroupIndex}_{blobIndex++}{ImageExtension}";
                    string absolutePath = Path.Combine(columnSidecarDir, fileName);
                    using (FileStream sidecarStream = File.Create(absolutePath))
                    {
                        sidecarStream.Write(blob);
                    }

                    string relativePath = Path.GetRelativePath(datumFileDir, absolutePath);
                    pathBytes[rowIndex] = System.Text.Encoding.UTF8.GetBytes(relativePath);
                }
            }

            int totalPoolBytes = 0;
            for (int i = 0; i < rowCount; i++) totalPoolBytes += pathBytes[i].Length;

            byte[] bitmapBytes = nullBitmap.ToBytes();
            int offsetsSize = (rowCount + 1) * 4;
            int rawLength = bitmapBytes.Length + offsetsSize + totalPoolBytes;
            byte[] raw = ArrayPool<byte>.Shared.Rent(rawLength);

            try
            {
                Buffer.BlockCopy(bitmapBytes, 0, raw, 0, bitmapBytes.Length);

                int offsetWrite = bitmapBytes.Length;
                int poolWrite = bitmapBytes.Length + offsetsSize;
                uint runningOffset = 0;

                BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(offsetWrite), runningOffset);
                offsetWrite += 4;

                for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
                {
                    byte[] pathBuf = pathBytes[rowIndex];
                    runningOffset += (uint)pathBuf.Length;
                    BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(offsetWrite), runningOffset);
                    offsetWrite += 4;

                    if (pathBuf.Length > 0)
                    {
                        Buffer.BlockCopy(pathBuf, 0, raw, poolWrite, pathBuf.Length);
                        poolWrite += pathBuf.Length;
                    }
                }

                // Externalized pages store paths (ASCII/UTF-8), which compress well.
                (byte[] payload, int payloadLength) = DatumCompressor.Compress(raw.AsSpan(0, rawLength), DatumCompression.Zstd);

                return new DatumEncodedPage(payload, payloadLength, DatumEncoding.ExternalBytes, DatumCompression.Zstd, rawLength, zoneMap);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(raw);
            }
        }
        finally
        {
            ArrayPool<byte[]>.Shared.Return(pathBytes, clearArray: true);
        }
    }
}
