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
        byte[][] blobs = ArrayPool<byte[]>.Shared.Rent(rowCount);
        uint nullCount = 0;
        long maxBlobSize = 0;

        try
        {
            for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                DataValue value = values[rowIndex];

                if (value.IsNull)
                {
                    nullBitmap.SetNull(rowIndex);
                    blobs[rowIndex] = [];
                    nullCount++;
                }
                else
                {
                    byte[] blob = isImage ? value.AsImage() : value.AsUInt8Array();
                    blobs[rowIndex] = blob;
                    if (blob.Length > maxBlobSize) maxBlobSize = blob.Length;
                }
            }

            DatumZoneMap zoneMap = new(nullCount, null, null);

            bool externalize = descriptor.ExternalizesBlobs && maxBlobSize > descriptor.ExternalizationThresholdBytes;

            if (externalize)
            {
                return EncodeExternalized(values, descriptor, context, nullBitmap, blobs, rowCount, nullCount, zoneMap, isImage, compression);
            }

            return EncodeInline(nullBitmap, blobs, rowCount, nullCount, zoneMap, compression);
        }
        finally
        {
            ArrayPool<byte[]>.Shared.Return(blobs, clearArray: true);
        }
    }

    private static DatumEncodedPage EncodeInline(
        DatumNullBitmap nullBitmap,
        byte[][] blobs,
        int rowCount,
        uint nullCount,
        DatumZoneMap zoneMap,
        DatumCompression compression)
    {
        int totalPoolBytes = 0;
        for (int i = 0; i < rowCount; i++) totalPoolBytes += blobs[i].Length;

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
                byte[] blob = blobs[rowIndex];
                runningOffset += (uint)blob.Length;
                BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(offsetWrite), runningOffset);
                offsetWrite += 4;

                if (blob.Length > 0)
                {
                    Buffer.BlockCopy(blob, 0, raw, poolWrite, blob.Length);
                    poolWrite += blob.Length;
                }
            }

            byte[] payload = DatumCompressor.Compress(raw.AsSpan(0, rawLength), compression);

            return new DatumEncodedPage(payload, DatumEncoding.VariableBytes, compression, rawLength, zoneMap);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
        }
    }

    private static DatumEncodedPage EncodeExternalized(
        IReadOnlyList<DataValue> values,
        DatumColumnDescriptor descriptor,
        DatumEncoderContext context,
        DatumNullBitmap nullBitmap,
        byte[][] blobs,
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
            for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                if (nullBitmap.IsNull(rowIndex))
                {
                    pathBytes[rowIndex] = [];
                    continue;
                }

                byte[] blob = blobs[rowIndex];
                string fileName = $"{context.RowGroupIndex}_{blobIndex++}{ImageExtension}";
                string absolutePath = Path.Combine(columnSidecarDir, fileName);
                File.WriteAllBytes(absolutePath, blob);

                // Store a path relative to the datum file's directory so the file set is portable.
                string relativePath = Path.GetRelativePath(datumFileDir, absolutePath);
                pathBytes[rowIndex] = System.Text.Encoding.UTF8.GetBytes(relativePath);
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
                byte[] payload = DatumCompressor.Compress(raw.AsSpan(0, rawLength), DatumCompression.Zstd);

                return new DatumEncodedPage(payload, DatumEncoding.ExternalBytes, DatumCompression.Zstd, rawLength, zoneMap);
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
