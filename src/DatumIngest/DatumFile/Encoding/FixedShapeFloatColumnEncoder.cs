using DatumIngest.DatumFile.Compression;
using DatumIngest.Model;

namespace DatumIngest.DatumFile.Encoding;

/// <summary>
/// Encodes <see cref="DataKind.Vector"/>, <see cref="DataKind.Matrix"/>, and
/// <see cref="DataKind.Tensor"/> column pages using <see cref="DatumEncoding.FixedFloat32"/>
/// with a byte-shuffle pre-filter and Zstd compression.
/// </summary>
/// <remarks>
/// All rows in the column must have the same shape (the number of elements per row is uniform);
/// this invariant is enforced by the schema descriptor. The shape is derived from the first
/// non-null value in the first row group and frozen in <see cref="DatumColumnDescriptor.FixedShape"/>.
/// <para>
/// Layout of the uncompressed payload:
/// <c>nullBitmap[ceil(N/8)] | float32[N * elementsPerRow]</c>.
/// Null rows occupy <c>elementsPerRow</c> consecutive <c>float.NaN</c> values in the array,
/// maintaining implicit positional indexing. The entire float block is passed through
/// <see cref="FloatByteShuffle.Shuffle"/> before Zstd compression.
/// </para>
/// </remarks>
internal sealed class FixedShapeFloatColumnEncoder : DatumColumnEncoder
{
    /// <inheritdoc/>
    public override DatumEncodedPage Encode(
        IReadOnlyList<DataValue> values,
        DatumColumnDescriptor descriptor,
        DatumEncoderContext context)
    {
        int rowCount = values.Count;
        DatumNullBitmap nullBitmap = new(rowCount);
        uint nullCount = 0;

        // Determine elements-per-row from the descriptor or from the first non-null value.
        int elementsPerRow = ResolveElementsPerRow(values, descriptor);

        float[] floatData = new float[rowCount * elementsPerRow];
        // Pre-fill with NaN so null rows are already correct.
        floatData.AsSpan().Fill(float.NaN);

        float minimum = float.MaxValue;
        float maximum = float.MinValue;

        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            DataValue value = values[rowIndex];

            if (value.IsNull)
            {
                nullBitmap.SetNull(rowIndex);
                nullCount++;
                continue;
            }

            ReadOnlySpan<float> elements = ExtractElements(value);
            Span<float> destination = floatData.AsSpan(rowIndex * elementsPerRow, elementsPerRow);
            elements.CopyTo(destination);

            foreach (float element in elements)
            {
                if (!float.IsNaN(element))
                {
                    if (element < minimum) minimum = element;
                    if (element > maximum) maximum = element;
                }
            }
        }

        DatumZoneMap zoneMap = BuildZoneMap(nullCount, rowCount, minimum, maximum);

        byte[] bitmapBytes = nullBitmap.ToBytes();
        byte[] shuffledFloats = new byte[floatData.Length * sizeof(float)];
        FloatByteShuffle.Shuffle(floatData, shuffledFloats);

        byte[] raw = new byte[bitmapBytes.Length + shuffledFloats.Length];
        bitmapBytes.CopyTo(raw, 0);
        shuffledFloats.CopyTo(raw, bitmapBytes.Length);

        byte[] compressed = DatumCompressor.Compress(raw, DatumCompression.Zstd);

        return new DatumEncodedPage(compressed, DatumEncoding.FixedFloat32, DatumCompression.Zstd, raw.Length, zoneMap);
    }

    private static int ResolveElementsPerRow(IReadOnlyList<DataValue> values, DatumColumnDescriptor descriptor)
    {
        if (descriptor.HasFixedShape)
        {
            return descriptor.ElementsPerRow();
        }

        // Fall back to reading the shape from the first non-null value when the descriptor
        // has not yet been frozen (e.g. first row group of a streaming write).
        foreach (DataValue value in values)
        {
            if (!value.IsNull)
            {
                ReadOnlySpan<float> elements = ExtractElements(value);
                return elements.Length;
            }
        }

        // All rows null — page is zero-width (no float data, bitmap only).
        return 0;
    }

    private static ReadOnlySpan<float> ExtractElements(DataValue value)
    {
        return value.Kind switch
        {
            DataKind.Scalar => new float[] { value.AsScalar() },
            DataKind.Vector => value.AsVector(),
            DataKind.Matrix => value.AsMatrix(out _, out _),
            DataKind.Tensor => value.AsTensor(out _),
            _ => throw new NotSupportedException($"FixedShapeFloatColumnEncoder does not support {value.Kind}.")
        };
    }

    private static DatumZoneMap BuildZoneMap(uint nullCount, int rowCount, float minimum, float maximum)
    {
        if (nullCount == (uint)rowCount || minimum > maximum)
        {
            return new DatumZoneMap(nullCount, null, null);
        }

        return new DatumZoneMap(nullCount, DataValue.FromScalar(minimum), DataValue.FromScalar(maximum));
    }
}
