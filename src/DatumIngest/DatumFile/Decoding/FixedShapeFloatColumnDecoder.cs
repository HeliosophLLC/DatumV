using DatumIngest.DatumFile.Compression;
using DatumIngest.Model;

namespace DatumIngest.DatumFile.Decoding;

/// <summary>
/// Decodes <see cref="DataKind.Vector"/>, <see cref="DataKind.Matrix"/>, and
/// <see cref="DataKind.Tensor"/> column pages produced by <c>FixedShapeFloatColumnEncoder</c>.
/// </summary>
/// <remarks>
/// Uncompressed layout: <c>nullBitmap[ceil(N/8)] | shuffledFloat32[N * elementsPerRow * 4]</c>.
/// After unshuffling the float data, each row's slice is reconstructed into the appropriate
/// <see cref="DataValue"/> using the fixed shape stored in <see cref="DatumColumnDescriptor.FixedShape"/>.
/// </remarks>
internal sealed class FixedShapeFloatColumnDecoder : DatumColumnDecoder
{
    /// <inheritdoc/>
    public override DataValue[] Decode(
        byte[] payload,
        DatumEncoding encoding,
        DatumCompression compression,
        int uncompressedByteLength,
        int rowCount,
        DatumColumnDescriptor descriptor,
        DatumDecoderContext context)
    {
        byte[] raw = DecompressPayload(payload, uncompressedByteLength, compression);
        int bitmapByteCount = DatumNullBitmap.ByteCount(rowCount);
        DatumNullBitmap nullBitmap = ReadNullBitmap(raw, rowCount);

        int elementsPerRow = ResolveElementsPerRow(descriptor, raw, bitmapByteCount, rowCount);

        float[] allFloats = new float[rowCount * elementsPerRow];
        FloatByteShuffle.Unshuffle(raw.AsSpan(bitmapByteCount), allFloats);

        DataValue[] result = new DataValue[rowCount];
        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            if (nullBitmap.IsNull(rowIndex))
            {
                result[rowIndex] = DataValue.Null(descriptor.Kind);
                continue;
            }

            float[] rowFloats = allFloats[(rowIndex * elementsPerRow)..((rowIndex + 1) * elementsPerRow)];
            result[rowIndex] = BuildDataValue(descriptor, rowFloats);
        }

        return result;
    }

    /// <inheritdoc/>
    public override void DecodeIntoColumn(
        byte[] payload,
        DatumEncoding encoding,
        DatumCompression compression,
        int uncompressedByteLength,
        int rowCount,
        DatumColumnDescriptor descriptor,
        DatumDecoderContext context,
        DataValue[] target,
        StringArena stringArena,
        DataArena dataArena)
    {
        byte[] raw = DecompressPayload(payload, uncompressedByteLength, compression);
        int bitmapByteCount = DatumNullBitmap.ByteCount(rowCount);
        DatumNullBitmap nullBitmap = ReadNullBitmap(raw, rowCount);

        int elementsPerRow = ResolveElementsPerRow(descriptor, raw, bitmapByteCount, rowCount);

        float[] allFloats = new float[rowCount * elementsPerRow];
        FloatByteShuffle.Unshuffle(raw.AsSpan(bitmapByteCount), allFloats);

        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            if (nullBitmap.IsNull(rowIndex))
            {
                target[rowIndex] = DataValue.Null(descriptor.Kind);
                continue;
            }

            float[] rowFloats = allFloats[(rowIndex * elementsPerRow)..((rowIndex + 1) * elementsPerRow)];
            target[rowIndex] = BuildDataValue(descriptor, rowFloats);
        }
    }

    private static int ResolveElementsPerRow(DatumColumnDescriptor descriptor, byte[] raw, int bitmapByteCount, int rowCount)
    {
        if (descriptor.HasFixedShape)
        {
            return descriptor.ElementsPerRow();
        }

        // All-null page: no float bytes present.
        int floatBytes = raw.Length - bitmapByteCount;
        if (floatBytes == 0 || rowCount == 0)
        {
            return 0;
        }

        return floatBytes / (sizeof(float) * rowCount);
    }

    private static DataValue BuildDataValue(DatumColumnDescriptor descriptor, float[] rowFloats)
    {
        return descriptor.Kind switch
        {
            DataKind.Vector => DataValue.FromVector(rowFloats),
            DataKind.Matrix when descriptor.FixedShape is [int rows, int columns] =>
                DataValue.FromMatrix(rowFloats, rows, columns),
            DataKind.Tensor when descriptor.FixedShape is int[] shape =>
                DataValue.FromTensor(rowFloats, shape),
            DataKind.Float32 => DataValue.FromFloat32(rowFloats[0]),
            _ => throw new NotSupportedException(
                $"FixedShapeFloatColumnDecoder does not support {descriptor.Kind}.")
        };
    }
}
