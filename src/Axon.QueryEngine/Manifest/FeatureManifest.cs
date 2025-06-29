namespace Axon.QueryEngine.Manifest;

using System.Text.Json.Serialization;
using Axon.QueryEngine.Model;
using Axon.QueryEngine.Statistics.Accumulators;

/// <summary>
/// A frequency entry pairing a value string with its occurrence count.
/// </summary>
/// <param name="Value">The string representation of the value.</param>
/// <param name="Frequency">Number of times this value was observed.</param>
public sealed record FrequencyEntry(string Value, long Frequency);

/// <summary>
/// Base class for per-column feature manifests. Each <see cref="DataKind"/> maps to
/// a specialized subclass that carries kind-appropriate statistics.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(NumericFeatureManifest), "numeric")]
[JsonDerivedType(typeof(StringFeatureManifest), "string")]
[JsonDerivedType(typeof(VectorFeatureManifest), "vector")]
[JsonDerivedType(typeof(TensorFeatureManifest), "tensor")]
[JsonDerivedType(typeof(ImageFeatureManifest), "image")]
[JsonDerivedType(typeof(BinaryFeatureManifest), "binary")]
[JsonDerivedType(typeof(TemporalFeatureManifest), "temporal")]
public abstract class FeatureManifest
{
    /// <summary>Gets the column name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the data kind of this column.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<DataKind>))]
    public required DataKind Kind { get; init; }

    /// <summary>Gets the total number of non-null values.</summary>
    public required long Count { get; init; }

    /// <summary>Gets the number of null or empty values.</summary>
    public required long NullCount { get; init; }

    /// <summary>Gets the HyperLogLog-estimated distinct value count.</summary>
    public required long EstimatedDistinctCount { get; init; }

    /// <summary>Gets the top-K most frequent values.</summary>
    public required IReadOnlyList<FrequencyEntry> TopKValues { get; init; }

    /// <summary>Gets the Shannon entropy of the value distribution in bits, or null if not applicable.</summary>
    public double? Entropy { get; init; }

    /// <summary>Gets whether the entropy value is an approximate lower bound due to frequency map capping.</summary>
    public bool? EntropyApproximate { get; init; }
}

/// <summary>
/// Feature manifest for numeric columns (<see cref="DataKind.Scalar"/>, <see cref="DataKind.UInt8"/>).
/// Includes descriptive statistics and histogram.
/// </summary>
public sealed class NumericFeatureManifest : FeatureManifest
{
    /// <summary>Gets the minimum value.</summary>
    public required double Min { get; init; }

    /// <summary>Gets the maximum value.</summary>
    public required double Max { get; init; }

    /// <summary>Gets the arithmetic mean.</summary>
    public required double Mean { get; init; }

    /// <summary>Gets the population variance.</summary>
    public required double Variance { get; init; }

    /// <summary>Gets the population standard deviation.</summary>
    public required double StandardDeviation { get; init; }

    /// <summary>Gets the histogram data.</summary>
    public required HistogramData Histogram { get; init; }

    /// <summary>Gets the percentile/quantile data, or null if not computed.</summary>
    public QuantileData? Quantiles { get; init; }

    /// <summary>Gets the number of values exactly equal to zero.</summary>
    public required long ZeroCount { get; init; }

    /// <summary>Gets the ratio of zero values to total count.</summary>
    public required double ZeroRatio { get; init; }
}

/// <summary>
/// Histogram bin edges and counts.
/// </summary>
/// <param name="BinEdges">Edge values defining the bins (length = bin count + 1).</param>
/// <param name="Counts">Number of values in each bin (length = bin count).</param>
public sealed record HistogramData(IReadOnlyList<double> BinEdges, IReadOnlyList<long> Counts);

/// <summary>
/// Feature manifest for string columns (<see cref="DataKind.String"/>, <see cref="DataKind.JsonValue"/>).
/// </summary>
public sealed class StringFeatureManifest : FeatureManifest
{
    /// <summary>Gets the minimum string length.</summary>
    public required int MinLength { get; init; }

    /// <summary>Gets the maximum string length.</summary>
    public required int MaxLength { get; init; }
}

/// <summary>
/// Percentile data for numeric columns.
/// </summary>
/// <param name="P01">1st percentile.</param>
/// <param name="P05">5th percentile.</param>
/// <param name="P25">25th percentile (first quartile).</param>
/// <param name="P50">50th percentile (median).</param>
/// <param name="P75">75th percentile (third quartile).</param>
/// <param name="P95">95th percentile.</param>
/// <param name="P99">99th percentile.</param>
public sealed record QuantileData(
    double P01, double P05, double P25, double P50, double P75, double P95, double P99);

/// <summary>
/// Aggregate numeric summary used within vector, tensor, image, and binary manifests.
/// </summary>
/// <param name="Count">Number of elements.</param>
/// <param name="Min">Minimum value.</param>
/// <param name="Max">Maximum value.</param>
/// <param name="Mean">Arithmetic mean.</param>
/// <param name="Variance">Population variance.</param>
/// <param name="StandardDeviation">Population standard deviation.</param>
public sealed record NumericSummaryData(
    long Count, double Min, double Max, double Mean, double Variance, double StandardDeviation);

/// <summary>
/// Feature manifest for rank-1 vector columns (<see cref="DataKind.Vector"/>).
/// </summary>
public sealed class VectorFeatureManifest : FeatureManifest
{
    /// <summary>Gets the minimum vector length.</summary>
    public required int MinLength { get; init; }

    /// <summary>Gets the maximum vector length.</summary>
    public required int MaxLength { get; init; }

    /// <summary>Gets aggregate element-wise numeric statistics across all vectors.</summary>
    public required NumericSummaryData ElementStats { get; init; }
}

/// <summary>
/// Feature manifest for multi-dimensional tensor columns (<see cref="DataKind.Matrix"/>, <see cref="DataKind.Tensor"/>).
/// </summary>
public sealed class TensorFeatureManifest : FeatureManifest
{
    /// <summary>Gets the minimum rank (number of dimensions) observed.</summary>
    public required int MinRank { get; init; }

    /// <summary>Gets the maximum rank observed.</summary>
    public required int MaxRank { get; init; }

    /// <summary>Gets the minimum total element count.</summary>
    public required int MinElementCount { get; init; }

    /// <summary>Gets the maximum total element count.</summary>
    public required int MaxElementCount { get; init; }

    /// <summary>Gets aggregate element-wise numeric statistics across all tensors.</summary>
    public required NumericSummaryData ElementStats { get; init; }
}

/// <summary>
/// Feature manifest for image columns (<see cref="DataKind.Image"/>).
/// Dimensions and channels are extracted by header-only parsing (no full decode).
/// </summary>
public sealed class ImageFeatureManifest : FeatureManifest
{
    /// <summary>Gets the minimum image width in pixels.</summary>
    public required int MinWidth { get; init; }

    /// <summary>Gets the maximum image width in pixels.</summary>
    public required int MaxWidth { get; init; }

    /// <summary>Gets the minimum image height in pixels.</summary>
    public required int MinHeight { get; init; }

    /// <summary>Gets the maximum image height in pixels.</summary>
    public required int MaxHeight { get; init; }

    /// <summary>Gets the distribution of channel counts (key = channels, value = image count).</summary>
    public required IReadOnlyDictionary<int, long> ChannelCounts { get; init; }

    /// <summary>Gets the number of images whose format could not be identified.</summary>
    public required long UndecodableCount { get; init; }

    /// <summary>Gets file size statistics in bytes.</summary>
    public required NumericSummaryData FileSizeStats { get; init; }
}

/// <summary>
/// Feature manifest for raw binary columns (<see cref="DataKind.UInt8Array"/>).
/// </summary>
public sealed class BinaryFeatureManifest : FeatureManifest
{
    /// <summary>Gets byte-length statistics.</summary>
    public required NumericSummaryData SizeStats { get; init; }
}

/// <summary>
/// Feature manifest for temporal columns (<see cref="DataKind.Date"/>, <see cref="DataKind.DateTime"/>).
/// </summary>
public sealed class TemporalFeatureManifest : FeatureManifest
{
    /// <summary>Gets the earliest value as an ISO 8601 string.</summary>
    public required string? Earliest { get; init; }

    /// <summary>Gets the latest value as an ISO 8601 string.</summary>
    public required string? Latest { get; init; }
}
