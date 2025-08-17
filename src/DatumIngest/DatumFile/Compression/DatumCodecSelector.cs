using DatumIngest.Model;

namespace DatumIngest.DatumFile.Compression;

/// <summary>
/// Maps <see cref="DataKind"/> values to their canonical <see cref="DatumEncoding"/> and
/// <see cref="DatumCompression"/> choices. Centralizes the codec policy that the writer
/// uses when encoding column pages.
/// </summary>
public static class DatumCodecSelector
{
    /// <summary>
    /// Returns the canonical encoding for a column of the given <paramref name="kind"/>.
    /// Dictionary encoding is handled separately at write time via column descriptor flags.
    /// </summary>
    public static DatumEncoding SelectEncoding(DataKind kind)
    {
        return kind switch
        {
            DataKind.Float32 => DatumEncoding.FixedFloat32,
            DataKind.UInt8 => DatumEncoding.Raw,
            DataKind.Int8 => DatumEncoding.Raw,
            DataKind.Int16 => DatumEncoding.Raw,
            DataKind.UInt16 => DatumEncoding.Raw,
            DataKind.Int32 => DatumEncoding.Raw,
            DataKind.UInt32 => DatumEncoding.Raw,
            DataKind.Int64 => DatumEncoding.Raw,
            DataKind.UInt64 => DatumEncoding.Raw,
            DataKind.Float64 => DatumEncoding.Raw,
            DataKind.Boolean => DatumEncoding.BitPacked,
            DataKind.Date => DatumEncoding.DeltaInt32,
            DataKind.DateTime => DatumEncoding.DeltaInt64,
            DataKind.Time => DatumEncoding.DeltaInt64,
            DataKind.Duration => DatumEncoding.DeltaInt64,
            DataKind.Uuid => DatumEncoding.Raw,
            DataKind.String => DatumEncoding.VariableBytes,
            DataKind.JsonValue => DatumEncoding.VariableBytes,
            DataKind.UInt8Array => DatumEncoding.VariableBytes,
            DataKind.Image => DatumEncoding.VariableBytes,
            DataKind.Vector => DatumEncoding.FixedFloat32,
            DataKind.Matrix => DatumEncoding.FixedFloat32,
            DataKind.Tensor => DatumEncoding.FixedFloat32,
            DataKind.Array => DatumEncoding.VariableDataValue,
            _ => throw new NotSupportedException($"No canonical encoding defined for DataKind.{kind}."),
        };
    }

    /// <summary>
    /// Returns the compression codec that should be applied after encoding for a column
    /// of the given <paramref name="kind"/>. Image and UInt8Array blobs that are already
    /// compressed use <see cref="DatumCompression.None"/> to avoid expansion.
    /// </summary>
    public static DatumCompression SelectCompression(DataKind kind)
    {
        return kind switch
        {
            // Already-compressed binary blobs: skip re-compression.
            DataKind.Image => DatumCompression.None,
            // Zstd for everything else.
            _ => DatumCompression.Zstd,
        };
    }

    /// <summary>
    /// Returns <c>true</c> if a byte-shuffle pre-filter should be applied before compression
    /// for the given <paramref name="kind"/>. The shuffle re-orders the four byte lanes of
    /// each <c>float32</c> to improve Zstd compression ratios on correlated float data.
    /// </summary>
    public static bool NeedsFloatShuffle(DataKind kind)
    {
        return kind is DataKind.Float32 or DataKind.Vector or DataKind.Matrix or DataKind.Tensor;
    }

    /// <summary>
    /// Returns <c>true</c> if the given kind stores values as a dense block of <c>float32</c> elements
    /// (i.e., uses <see cref="DatumEncoding.FixedFloat32"/>).
    /// </summary>
    public static bool IsFixedFloat(DataKind kind)
    {
        return kind is DataKind.Float32 or DataKind.Vector or DataKind.Matrix or DataKind.Tensor;
    }
}
