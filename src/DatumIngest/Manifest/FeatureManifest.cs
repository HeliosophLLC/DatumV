namespace DatumIngest.Manifest;

using System.Text.Json.Serialization;
using DatumIngest.Model;
using DatumIngest.Statistics.Accumulators;

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
[JsonDerivedType(typeof(ArrayFeatureManifest), "array")]
[JsonDerivedType(typeof(ImageFeatureManifest), "image")]
[JsonDerivedType(typeof(BinaryFeatureManifest), "binary")]
[JsonDerivedType(typeof(TemporalFeatureManifest), "temporal")]
[JsonDerivedType(typeof(BooleanFeatureManifest), "boolean")]
[JsonDerivedType(typeof(DecimalFeatureManifest), "decimal")]
[JsonDerivedType(typeof(UuidFeatureManifest), "uuid")]
[JsonDerivedType(typeof(JsonFeatureManifest), "json")]
public abstract class FeatureManifest
{
    /// <summary>Gets the column name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the data kind of this column.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<DataKind>))]
    public required DataKind Kind { get; init; }

    /// <summary>
    /// True when this column holds typed arrays of <see cref="Kind"/> elements
    /// (byte arrays, vectors, etc.). Defaults to <c>false</c>; absent in JSON
    /// when the column is scalar. Mirrors <see cref="ColumnInfo.IsArray"/>.
    /// </summary>
    public bool IsArray { get; init; }

    /// <summary>Gets the total number of non-null values.</summary>
    public required long Count { get; init; }

    /// <summary>Gets the number of null or empty values.</summary>
    public required long NullCount { get; init; }

    /// <summary>Gets the number of valid (non-null, non-empty) values.</summary>
    public required long ValidCount { get; init; }

    /// <summary>Gets the HyperLogLog-estimated distinct value count.</summary>
    public required long EstimatedDistinctCount { get; init; }

    /// <summary>Gets whether the column contains at most one distinct value.</summary>
    public bool IsConstant => EstimatedDistinctCount <= 1;

    /// <summary>
    /// Gets whether the column is near-constant, meaning a single value dominates
    /// more than 98% of rows. Uses a hard-coded threshold of 0.98 on
    /// <see cref="DominantValueRatio"/>. Near-constant columns are typically
    /// useless as features in machine learning models.
    /// </summary>
    public bool IsNearConstant => DominantValueRatio.HasValue && DominantValueRatio.Value > 0.98;

    /// <summary>Gets the top-K most frequent values.</summary>
    public required IReadOnlyList<FrequencyEntry> TopKValues { get; init; }

    /// <summary>Gets the ratio of null/empty values to total rows, or null if row count is zero.</summary>
    public double? NullRatio { get; init; }

    /// <summary>Gets the ratio of the most frequent value's count to total rows, or null if row count is zero or top-K is empty.</summary>
    public double? DominantValueRatio { get; init; }

    /// <summary>Gets the number of contiguous runs of null/empty values in the column.</summary>
    public long? MissingRuns { get; init; }

    /// <summary>Gets the Shannon entropy of the value distribution in bits, or null if not applicable.</summary>
    public double? Entropy { get; init; }

    /// <summary>Gets whether the entropy value is an approximate lower bound due to frequency map capping.</summary>
    public bool? EntropyApproximate { get; init; }

    /// <summary>
    /// Gets or sets the inferred semantic role of this column, or <c>null</c> for manifests
    /// created before column role classification was introduced.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<ColumnRole>))]
    public ColumnRole? Role { get; set; }

    /// <summary>
    /// Record of how this column's <see cref="Kind"/> was decided during schema inference.
    /// Populated when full-file scan inference runs; null when the ingester used
    /// sample-based inference or accepted an externally-declared schema.
    /// </summary>
    /// <remarks>
    /// Columns whose source data had leading-zero numerics (e.g. <c>"02931"</c>) are
    /// kept as <see cref="DataKind.String"/> by design — querying them numerically
    /// remains possible via <c>CAST(column AS Int32)</c>, but the stored form preserves
    /// original text exactly. See <see cref="SchemaInferenceReason.KeptAsStringLeadingZeros"/>.
    /// </remarks>
    public SchemaInferenceDecision? SchemaInference { get; init; }

    /// <summary>
    /// Gets whether the cached half of this manifest (top-K beyond the bitmap distinct
    /// set, t-digest quantiles, histogram buckets, entropy, kind-specific summaries
    /// like Mean / StdDev / FileSizeStats) is current as of the table's last mutation.
    /// </summary>
    /// <remarks>
    /// The hybrid manifest design (PR14h) splits each <see cref="FeatureManifest"/>
    /// into a "live" half — <see cref="Count"/>, <see cref="NullCount"/>,
    /// <see cref="NullRatio"/>, <see cref="EstimatedDistinctCount"/> — derived
    /// fresh from the per-column index on every snapshot rebuild, and a "cached"
    /// half persisted in <c>.datum-manifest</c> and refreshed on demand by
    /// <c>ANALYZE</c>. Mutations (INSERT / UPDATE / DELETE) flip this flag to
    /// <see langword="false"/>; <c>ANALYZE</c> sets it back to <see langword="true"/>.
    /// Consumers reading the cached fields (planner, language server hover, ML
    /// pipelines) should weight them lower or display a "may be outdated" hint
    /// when this flag is <see langword="false"/>.
    /// </remarks>
    public bool CachedStatsValid { get; init; } = true;
}

/// <summary>
/// Feature manifest for numeric scalar columns. Covers all integer kinds
/// (<see cref="DataKind.Int8"/>..<see cref="DataKind.Int128"/>,
/// <see cref="DataKind.UInt8"/>..<see cref="DataKind.UInt128"/>) and all float kinds
/// (<see cref="DataKind.Float16"/>, <see cref="DataKind.Float32"/>, <see cref="DataKind.Float64"/>),
/// plus <see cref="DataKind.Duration"/> with values expressed in seconds.
/// Int128/UInt128 statistics are computed via a <see cref="double"/> intermediate and
/// lose precision past 2^53; <see cref="DataKind.Decimal"/> is not yet routed here.
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

    /// <summary>Gets the skewness (third standardized moment). Positive indicates right-skewed distribution.</summary>
    public required double Skewness { get; init; }

    /// <summary>Gets the kurtosis (fourth standardized moment). Normal distribution yields approximately 3.</summary>
    public required double Kurtosis { get; init; }

    /// <summary>Gets the histogram data.</summary>
    public required HistogramData Histogram { get; init; }

    /// <summary>Gets the percentile/quantile data, or null if not computed.</summary>
    public QuantileData? Quantiles { get; init; }

    /// <summary>Gets the number of values exactly equal to zero.</summary>
    public required long ZeroCount { get; init; }

    /// <summary>Gets the ratio of zero values to total count.</summary>
    public required double ZeroRatio { get; init; }

    /// <summary>Gets the number of values with |x - mean| / stddev &gt; 3 (Z-score outliers).</summary>
    public required long OutlierCount { get; init; }

    /// <summary>Gets the ratio of outlier values to total count.</summary>
    public required double OutlierRatio { get; init; }

    /// <summary>Gets whether all observed values are integers (no fractional part).</summary>
    public required bool IntegerValued { get; init; }

    /// <summary>Gets the number of nonzero values, or null when not applicable (low zero ratio).</summary>
    public long? NonzeroCount { get; init; }

    /// <summary>Gets the mean of nonzero values, or null when not applicable.</summary>
    public double? NonzeroMean { get; init; }

    /// <summary>Gets the population variance of nonzero values, or null when not applicable.</summary>
    public double? NonzeroVariance { get; init; }

    /// <summary>Gets the population standard deviation of nonzero values, or null when not applicable.</summary>
    public double? NonzeroStandardDeviation { get; init; }
}

/// <summary>
/// Histogram bin edges and counts.
/// </summary>
/// <param name="BinEdges">Edge values defining the bins (length = bin count + 1).</param>
/// <param name="Counts">Number of values in each bin (length = bin count).</param>
public sealed record HistogramData(IReadOnlyList<double> BinEdges, IReadOnlyList<long> Counts);

/// <summary>
/// Feature manifest for string columns (<see cref="DataKind.String"/>).
/// </summary>
public sealed class StringFeatureManifest : FeatureManifest
{
    /// <summary>Gets the minimum string length.</summary>
    public required int MinLength { get; init; }

    /// <summary>Gets the maximum string length.</summary>
    public required int MaxLength { get; init; }

    /// <summary>
    /// Gets the dominant character repertoire of the column values, inferred by
    /// sampling the top-K entries. Used to detect synthetic identifier strings.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<CharacterClass>))]
    public CharacterClass CharacterClass { get; init; }
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
/// <param name="Iqr">Interquartile range (P75 − P25).</param>
/// <param name="LowerFence">Lower Tukey fence (P25 − 1.5 × IQR). Values below this are outliers.</param>
/// <param name="UpperFence">Upper Tukey fence (P75 + 1.5 × IQR). Values above this are outliers.</param>
/// <param name="OutlierCount">Number of sampled values outside the fences.</param>
/// <param name="OutlierRatio">Ratio of outlier values to total sampled values.</param>
public sealed record QuantileData(
    double P01, double P05, double P25, double P50, double P75, double P95, double P99,
    double Iqr, double LowerFence, double UpperFence, long OutlierCount, double OutlierRatio);

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
/// Feature manifest for typed-array columns (any <see cref="DataKind"/> +
/// <see cref="FeatureManifest.IsArray"/>). Currently emitted only for
/// <see cref="DataKind.Float32"/> + <see cref="FeatureManifest.IsArray"/> arrays
/// (the former Vector kind); other typed-array element kinds gain dedicated
/// element-stats paths as they're demanded.
/// </summary>
public sealed class ArrayFeatureManifest : FeatureManifest
{
    /// <summary>Gets the minimum array length (in elements).</summary>
    public required int MinLength { get; init; }

    /// <summary>Gets the maximum array length (in elements).</summary>
    public required int MaxLength { get; init; }

    /// <summary>Gets aggregate element-wise numeric statistics across all arrays.</summary>
    public required NumericSummaryData ElementStats { get; init; }

    /// <summary>Gets the total number of elements exactly equal to zero.</summary>
    public required long ZeroElementCount { get; init; }

    /// <summary>Gets the ratio of zero elements to total element count.</summary>
    public required double ZeroElementRatio { get; init; }

    /// <summary>Gets the number of arrays where every element is zero.</summary>
    public required long ZeroArrayCount { get; init; }

    /// <summary>Gets the minimum L2 norm across all arrays.</summary>
    public required double NormMin { get; init; }

    /// <summary>Gets the maximum L2 norm across all arrays.</summary>
    public required double NormMax { get; init; }

    /// <summary>Gets the mean L2 norm across all arrays.</summary>
    public required double NormMean { get; init; }
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

    /// <summary>Gets the distribution of image orientations (landscape, portrait, square).</summary>
    public required IReadOnlyDictionary<string, long> OrientationCounts { get; init; }

    /// <summary>Gets the number of images whose format could not be identified.</summary>
    public required long UndecodableCount { get; init; }

    /// <summary>Gets the number of images with width or height below 32 pixels.</summary>
    public required long TinyImageCount { get; init; }

    /// <summary>Gets the number of images with width or height above 4096 pixels.</summary>
    public required long HugeImageCount { get; init; }

    /// <summary>Gets file size statistics in bytes.</summary>
    public required NumericSummaryData FileSizeStats { get; init; }

    /// <summary>Gets megapixel (width × height / 1,000,000) summary statistics.</summary>
    public NumericSummaryData? MegapixelStats { get; init; }

    /// <summary>Gets pixel count (width × height) summary statistics for GPU memory planning.</summary>
    public NumericSummaryData? PixelCountStats { get; init; }

    /// <summary>Gets aspect ratio (width/height) summary statistics.</summary>
    public NumericSummaryData? AspectRatioStats { get; init; }

    /// <summary>Gets the aspect ratio (width/height) histogram, or null if no images were decoded.</summary>
    public HistogramData? AspectRatioHistogram { get; init; }
}

/// <summary>
/// Feature manifest for raw byte-array columns
/// (<see cref="DataKind.UInt8"/> + <see cref="FeatureManifest.IsArray"/>).
/// </summary>
public sealed class BinaryFeatureManifest : FeatureManifest
{
    /// <summary>Gets byte-length statistics.</summary>
    public required NumericSummaryData SizeStats { get; init; }
}

/// <summary>
/// Feature manifest for temporal columns
/// (<see cref="DataKind.Date"/>, <see cref="DataKind.Timestamp"/>,
/// <see cref="DataKind.TimestampTz"/>, <see cref="DataKind.Time"/>).
/// <see cref="Earliest"/>/<see cref="Latest"/> are ISO 8601 strings — date for Date,
/// <c>HH:mm:ss.FFFFFFF</c> for Time, and full ISO timestamp for the timestamp kinds.
/// </summary>
public sealed class TemporalFeatureManifest : FeatureManifest
{
    /// <summary>Gets the earliest value as an ISO 8601 string.</summary>
    public required string? Earliest { get; init; }

    /// <summary>Gets the latest value as an ISO 8601 string.</summary>
    public required string? Latest { get; init; }
}

/// <summary>
/// Feature manifest for boolean columns (<see cref="DataKind.Boolean"/>).
/// Applies to native booleans and to integer columns in untyped formats (CSV)
/// where all values are 0 or 1. The base class carries count, null, cardinality,
/// and top-K statistics; this subclass adds only the meaningful derived metric.
/// </summary>
public sealed class BooleanFeatureManifest : FeatureManifest
{
    /// <summary>
    /// Gets the ratio of <c>true</c> (or <c>1</c>) values to total valid (non-null) values.
    /// A value of 0.0 means all false; 1.0 means all true.
    /// </summary>
    public required double TrueRatio { get; init; }
}

/// <summary>
/// Feature manifest for <see cref="DataKind.Decimal"/> columns. Carries all
/// numeric statistics in <see cref="decimal"/> precision rather than
/// <see cref="double"/>, preserving Decimal's full 28-digit range — the whole
/// point of the kind.
/// </summary>
/// <remarks>
/// Higher-order moments (skewness, kurtosis), histogram, and quantile data
/// are intentionally absent from v1: those statistics are diagnostic and
/// would require either a hybrid double / decimal implementation or
/// additional decimal arithmetic for cubic / quartic terms. Add them when a
/// real workload demands them. The planner's primary inputs (Min, Max,
/// Mean, Variance, StandardDeviation, ZeroCount, IntegerValued) are all
/// present.
/// </remarks>
public sealed class DecimalFeatureManifest : FeatureManifest
{
    /// <summary>Gets the minimum value in full decimal precision.</summary>
    public required decimal Min { get; init; }

    /// <summary>Gets the maximum value in full decimal precision.</summary>
    public required decimal Max { get; init; }

    /// <summary>Gets the arithmetic mean in full decimal precision.</summary>
    public required decimal Mean { get; init; }

    /// <summary>Gets the population variance.</summary>
    public required decimal Variance { get; init; }

    /// <summary>Gets the population standard deviation.</summary>
    public required decimal StandardDeviation { get; init; }

    /// <summary>Gets the number of values exactly equal to zero.</summary>
    public required long ZeroCount { get; init; }

    /// <summary>Gets the ratio of zero values to total count.</summary>
    public required decimal ZeroRatio { get; init; }

    /// <summary>
    /// True when every observed value was a whole number (no fractional part).
    /// Distinguishes Decimal columns storing pure integers (e.g. quantities)
    /// from those storing fractional measurements (e.g. monetary amounts).
    /// </summary>
    public required bool IntegerValued { get; init; }
}

/// <summary>
/// Feature manifest for <see cref="DataKind.Uuid"/> columns. Surfaces RFC
/// 9562 version distribution and (for v7 UUIDs only) embedded-timestamp range
/// — the only kind-specific signals beyond cardinality / null counts that an
/// analyst typically wants from a UUID column.
/// </summary>
public sealed class UuidFeatureManifest : FeatureManifest
{
    /// <summary>
    /// Counts of UUIDs per RFC 9562 version field. Keys are the integer
    /// version (1-8 for the named versions; 0 for the nil UUID and any
    /// unrecognised value).
    /// </summary>
    public required IReadOnlyDictionary<int, long> VersionCounts { get; init; }

    /// <summary>
    /// Earliest embedded timestamp across all v7 UUIDs (which carry 48 bits
    /// of unix-milliseconds in their leading bytes), as an ISO 8601 string.
    /// <see langword="null"/> when no v7 UUIDs were observed.
    /// v1 / v6 timestamps are not extracted yet.
    /// </summary>
    public string? EmbeddedTimestampEarliest { get; init; }

    /// <summary>
    /// Latest embedded timestamp across all v7 UUIDs.
    /// <see langword="null"/> when no v7 UUIDs were observed.
    /// </summary>
    public string? EmbeddedTimestampLatest { get; init; }
}

/// <summary>
/// Feature manifest for <see cref="DataKind.Json"/> columns. JSON payloads
/// in DatumIngest are stored as canonical CBOR (RFC 7049 §3.9); this manifest
/// exposes a shallow shape summary derived from a single CBOR pass per value.
/// </summary>
/// <remarks>
/// V1 scope (Q5 Option A in the PR14 plan): root-type histogram + top-level
/// field set + maximum nesting depth. Recursive schema inference (per-key-path
/// type frequencies across the entire tree) is deferred — that's a separate
/// "schema discovery" feature that the current manifest's scalar shape doesn't
/// need to bake in.
/// </remarks>
public sealed class JsonFeatureManifest : FeatureManifest
{
    /// <summary>
    /// Counts of values per CBOR root type. Keys are
    /// <c>"object"</c> / <c>"array"</c> / <c>"string"</c> / <c>"number"</c>
    /// / <c>"boolean"</c> / <c>"null"</c> / <c>"other"</c>; absent keys
    /// indicate zero values of that root type.
    /// </summary>
    public required IReadOnlyDictionary<string, long> RootTypeCounts { get; init; }

    /// <summary>
    /// For object-rooted values, the count of values where each top-level
    /// key was present. A key with frequency <c>= ObjectRootCount</c> is
    /// present in every object; lower frequencies indicate optional fields.
    /// Empty when no object-rooted values were observed.
    /// </summary>
    public required IReadOnlyDictionary<string, long> TopLevelFieldCounts { get; init; }

    /// <summary>
    /// Maximum nesting depth observed. <c>1</c> for scalar-rooted values
    /// (string / number / boolean / null), <c>2</c> for an object containing
    /// only scalars, and so on.
    /// </summary>
    public required int MaxDepth { get; init; }
}
