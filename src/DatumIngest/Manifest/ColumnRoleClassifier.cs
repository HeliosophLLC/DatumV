namespace DatumIngest.Manifest;

using DatumIngest.Model;

/// <summary>
/// Classifies a column's semantic <see cref="ColumnRole"/> using its
/// <see cref="DataKind"/> and statistical profile from the feature manifest.
/// Classification is purely statistical — no column-name heuristics.
/// </summary>
public static class ColumnRoleClassifier
{
    /// <summary>NDV / RowCount threshold above which an integer/UUID column is considered an identifier.</summary>
    private const double IdentifierDistinctRatio = 0.95;

    /// <summary>NDV / RowCount threshold below which a column exhibits strong repetition (categorical signal).</summary>
    private const double StrongRepetitionRatio = 0.1;

    /// <summary>NDV / RowCount threshold for moderate repetition when combined with concentrated top-K.</summary>
    private const double ModerateRepetitionRatio = 0.3;

    /// <summary>Top-K coverage threshold for moderate-repetition categorical detection.</summary>
    private const double TopKCoverageThreshold = 0.5;

    /// <summary>Maximum absolute NDV for trivially small vocabularies.</summary>
    private const long TrivialVocabularySize = 50;

    /// <summary>NDV / RowCount threshold above which a string column is considered free-form text.</summary>
    private const double TextDistinctRatio = 0.5;

    /// <summary>Maximum string length threshold for text detection (average proxy: uses MaxLength).</summary>
    private const int TextMaxLengthThreshold = 50;

    /// <summary>Maximum null ratio for identifier columns.</summary>
    private const double IdentifierMaxNullRatio = 0.01;

    /// <summary>
    /// Minimum absolute NDV for an integer column to be classified as <see cref="ColumnRole.ForeignKey"/>
    /// regardless of its NDV-to-row-count ratio. Columns with thousands of distinct integer
    /// values are never practical categoricals — they are join keys with high fan-out.
    /// </summary>
    private const long ForeignKeyMinDistinctCount = 1000;

    /// <summary>
    /// Minimum absolute NDV for a contiguous integer range to be classified as a measure
    /// instead of categorical. Below this threshold, contiguous ranges like day-of-week (0–6)
    /// and month (1–12) remain categorical.
    /// </summary>
    private const long MinContiguousMeasureDistinctCount = 25;

    /// <summary>
    /// Maximum absolute NDV for the contiguous measure classification. Above this threshold,
    /// contiguous integer ranges are more likely sequential FK/ID values than bounded measures.
    /// </summary>
    private const long MaxContiguousMeasureDistinctCount = 500;

    /// <summary>
    /// Fraction of the theoretical range (max − min + 1) that must be covered by estimated
    /// distinct count for a column to be considered a contiguous integer range.
    /// Allows for HyperLogLog estimation error.
    /// </summary>
    private const double ContiguousRangeCoverage = 0.9;

    /// <summary>
    /// Minimum observed range (max − min) for the sparse-integer-measurement heuristic
    /// to engage. Below this, narrow-domain integers stay in the categorical / contiguous
    /// paths above (day-of-week, month, channel-count etc.).
    /// </summary>
    private const double SparseMeasureMinRange = 100.0;

    /// <summary>
    /// Minimum range-to-NDV ratio for an integer column to be classified as a sparse
    /// numeric measurement. Values sampled from a continuous physical domain (image
    /// dimensions, file sizes, encoded fixed-point measurements) typically span a range
    /// many times their distinct count; densely-packed FK ID sequences do not.
    /// </summary>
    private const double SparseMeasureRangeToDistinctRatio = 4.0;

    /// <summary>
    /// Minimum count of distinct values for the sparse-measurement heuristic. Below this,
    /// the column has too little signal to distinguish a measurement from a low-cardinality
    /// vocabulary that happens to span a wide range.
    /// </summary>
    private const long SparseMeasureMinDistinctCount = 5;

    /// <summary>
    /// Lower bound on <c>Min</c> required to classify a high-NDV integer column as a sparse
    /// measurement. Autoincrement-style ID columns typically start at 0 or 1; a
    /// <c>Min</c> meaningfully above that floor is the only purely-statistical signal we
    /// have to distinguish a wide-range measurement (file_byte_length: min ≈ 50KB) from a
    /// sparse ID column (instacart product_id: min = 1, max = 206209). Only applied when
    /// NDV is above the FK threshold; low-NDV cases are unambiguous (a 50-distinct column
    /// over a 1000-wide range is never a vocabulary).
    /// </summary>
    private const double SparseMeasureMinValueFloor = 100.0;

    /// <summary>
    /// Classifies a column's semantic role from its manifest statistics.
    /// </summary>
    /// <param name="manifest">The feature manifest for the column.</param>
    /// <param name="rowCount">Total number of rows in the table.</param>
    /// <returns>The inferred <see cref="ColumnRole"/>.</returns>
    public static ColumnRole Classify(FeatureManifest manifest, long rowCount)
    {
        // Structural kinds previously covered Vector. Float32 + IsArray columns
        // now flow through the numeric-array path; restoring a dedicated
        // structural classifier for them is deferred to the typed-array
        // feature-stats work.

        // Binary kinds: byte arrays (UInt8 + IsArray), Image, Audio, Video, Json (canonical CBOR bytes).
        if (manifest.Kind is DataKind.Image or DataKind.Audio or DataKind.Video or DataKind.Json
            || (manifest.Kind == DataKind.UInt8 && manifest.IsArray))
        {
            return ColumnRole.Binary;
        }

        // Temporal kinds: Date, Timestamp, TimestampTz, Time, Duration.
        if (manifest.Kind is DataKind.Date or DataKind.Timestamp or DataKind.TimestampTz or DataKind.Time or DataKind.Duration)
        {
            return ColumnRole.Temporal;
        }

        // Boolean is always categorical (two values).
        if (manifest.Kind is DataKind.Boolean)
        {
            return ColumnRole.Categorical;
        }

        double distinctRatio = rowCount > 0
            ? (double)manifest.EstimatedDistinctCount / rowCount
            : 0.0;

        double nullRatio = manifest.NullRatio ?? 0.0;

        // UUID columns: classify by cardinality.
        if (manifest.Kind is DataKind.Uuid)
        {
            return ClassifyIdentityColumn(distinctRatio, nullRatio);
        }

        // Integer kinds: identity vs. categorical vs. measure.
        if (IsIntegerKind(manifest.Kind))
        {
            return ClassifyIntegerColumn(manifest, rowCount, distinctRatio, nullRatio);
        }

        // Floating-point kinds: measure vs. integer-valued FK/ID.
        if (manifest.Kind is DataKind.Float32 or DataKind.Float64)
        {
            return ClassifyFloatingPointColumn(manifest, distinctRatio);
        }

        // String kinds.
        if (manifest.Kind == DataKind.String)
        {
            return ClassifyStringColumn(manifest, distinctRatio);
        }

        // Fallback — treat unknown kinds as categorical.
        return ColumnRole.Categorical;
    }

    /// <summary>
    /// Classifies an integer-typed column as Identifier, ForeignKey, Categorical, or Measure.
    /// </summary>
    private static ColumnRole ClassifyIntegerColumn(
        FeatureManifest manifest, long rowCount, double distinctRatio, double nullRatio)
    {
        // High cardinality + low nulls → Identifier.
        if (distinctRatio >= IdentifierDistinctRatio && nullRatio <= IdentifierMaxNullRatio)
        {
            return ColumnRole.Identifier;
        }

        // Contiguous integer range with moderate NDV → Measure.
        // Columns covering every integer in [min, max] are bounded numeric measures
        // (e.g. days_since_prior_order 0–30, age 0–99), not categorical dimensions.
        if (IsContiguousIntegerMeasure(manifest))
        {
            return ColumnRole.Measure;
        }

        // Sparse numeric measurement: range substantially wider than NDV → Measure.
        // Catches image-dimension columns (file_width: ~50 distinct in [200, 1024])
        // and file-size columns (file_byte_length: ~50K distinct in [50KB, 10MB]).
        // High-NDV cases require Min above an autoincrement-floor to avoid stealing
        // sparse 1-based ID sequences from the FK bucket.
        if (IsSparseIntegerMeasure(manifest))
        {
            return ColumnRole.Measure;
        }

        // Trivially small vocabulary → Categorical.
        if (manifest.EstimatedDistinctCount <= TrivialVocabularySize)
        {
            return ColumnRole.Categorical;
        }

        // High absolute NDV → ForeignKey regardless of ratio.
        // Columns with thousands of distinct integer values are join keys, not categoricals.
        if (manifest.EstimatedDistinctCount > ForeignKeyMinDistinctCount)
        {
            return ColumnRole.ForeignKey;
        }

        // Strong repetition → Categorical.
        if (distinctRatio < StrongRepetitionRatio)
        {
            return ColumnRole.Categorical;
        }

        // Moderate repetition + concentrated top-K → Categorical.
        if (distinctRatio < ModerateRepetitionRatio)
        {
            double topKCoverage = ComputeTopKCoverage(manifest, rowCount);
            if (topKCoverage > TopKCoverageThreshold)
            {
                return ColumnRole.Categorical;
            }
        }

        // Remaining integers with moderate-to-high cardinality → ForeignKey.
        return ColumnRole.ForeignKey;
    }

    /// <summary>
    /// Classifies a floating-point column. Integer-valued floats with identity-like
    /// statistics are reclassified; otherwise they are measures.
    /// </summary>
    private static ColumnRole ClassifyFloatingPointColumn(FeatureManifest manifest, double distinctRatio)
    {
        // If integer-valued (no fractional parts), use integer classification logic.
        if (manifest is NumericFeatureManifest { IntegerValued: true } numeric)
        {
            double nullRatio = manifest.NullRatio ?? 0.0;

            if (distinctRatio >= IdentifierDistinctRatio && nullRatio <= IdentifierMaxNullRatio)
            {
                return ColumnRole.Identifier;
            }

            if (manifest.EstimatedDistinctCount <= TrivialVocabularySize)
            {
                return ColumnRole.Categorical;
            }

            if (distinctRatio < StrongRepetitionRatio)
            {
                return ColumnRole.Categorical;
            }

            // Integer-valued floats with moderate cardinality → ForeignKey (legacy data pattern).
            return ColumnRole.ForeignKey;
        }

        // True floating-point with fractional values → Measure.
        return ColumnRole.Measure;
    }

    /// <summary>Minimum fixed string length to qualify as a synthetic identifier.
    /// Below this threshold, fixed-length alphanumeric strings are more likely to be
    /// abbreviation codes (state codes, currency codes) than synthetic identifiers.</summary>
    private const int MinSyntheticIdentifierLength = 8;

    /// <summary>
    /// Classifies a string or JSON column. Fixed-length strings with restricted character
    /// repertoire (hexadecimal, base-64, alphanumeric) are classified as synthetic identity
    /// columns (Identifier or ForeignKey depending on cardinality); long high-cardinality
    /// strings as text; all others as categorical.
    /// </summary>
    private static ColumnRole ClassifyStringColumn(FeatureManifest manifest, double distinctRatio)
    {
        // Fixed-length + restricted character set + sufficient length → synthetic identity column.
        if (manifest is StringFeatureManifest
            {
                MinLength: >= MinSyntheticIdentifierLength, CharacterClass: not CharacterClass.Mixed
            } stringManifest
            && stringManifest.MinLength == stringManifest.MaxLength)
        {
            double nullRatio = manifest.NullRatio ?? 0.0;
            return ClassifyIdentityColumn(distinctRatio, nullRatio);
        }

        // High distinct ratio + long values → Text.
        if (distinctRatio > TextDistinctRatio && manifest is StringFeatureManifest { MaxLength: > TextMaxLengthThreshold })
        {
            return ColumnRole.Text;
        }

        // All other string columns → Categorical.
        return ColumnRole.Categorical;
    }

    /// <summary>
    /// Classifies a UUID or other identity-typed column by cardinality.
    /// </summary>
    private static ColumnRole ClassifyIdentityColumn(double distinctRatio, double nullRatio)
    {
        if (distinctRatio >= IdentifierDistinctRatio && nullRatio <= IdentifierMaxNullRatio)
        {
            return ColumnRole.Identifier;
        }

        return ColumnRole.ForeignKey;
    }

    /// <summary>
    /// Computes the fraction of total rows covered by the top-K most frequent values.
    /// </summary>
    private static double ComputeTopKCoverage(FeatureManifest manifest, long rowCount)
    {
        if (rowCount <= 0 || manifest.TopKValues.Count == 0)
        {
            return 0.0;
        }

        long topKTotal = 0;
        foreach (FrequencyEntry entry in manifest.TopKValues)
        {
            topKTotal += entry.Frequency;
        }

        return (double)topKTotal / rowCount;
    }

    /// <summary>
    /// Detects bounded numeric measures that happen to have small NDV because their domain
    /// is a contiguous integer range. Distinguished from trivial categoricals (day_of_week,
    /// month) by requiring NDV above <see cref="MinContiguousMeasureDistinctCount"/>, and from
    /// sequential FK/ID columns by capping at <see cref="MaxContiguousMeasureDistinctCount"/>.
    /// </summary>
    private static bool IsContiguousIntegerMeasure(FeatureManifest manifest)
    {
        if (manifest is not NumericFeatureManifest { IntegerValued: true } numeric)
        {
            return false;
        }

        long ndv = manifest.EstimatedDistinctCount;

        if (ndv <= MinContiguousMeasureDistinctCount || ndv > MaxContiguousMeasureDistinctCount)
        {
            return false;
        }

        double rangeSpan = numeric.Max - numeric.Min + 1;

        if (rangeSpan <= 0)
        {
            return false;
        }

        double coverage = ndv / rangeSpan;

        return coverage >= ContiguousRangeCoverage;
    }

    /// <summary>
    /// Detects integer columns whose observed range is substantially wider than their
    /// distinct count, indicating values sampled from a continuous physical domain rather
    /// than drawn from a small categorical vocabulary or a packed ID sequence.
    /// </summary>
    /// <remarks>
    /// Two regimes:
    /// <list type="bullet">
    /// <item><description>
    /// Low-NDV (NDV ≤ <see cref="ForeignKeyMinDistinctCount"/>): a column with only a few
    /// dozen distinct values that span a wide range is unambiguously a measurement —
    /// image widths/heights are the canonical case (e.g. ~50 distinct values in [200, 1024]).
    /// </description></item>
    /// <item><description>
    /// High-NDV: the heuristic additionally requires <c>Min</c> ≥
    /// <see cref="SparseMeasureMinValueFloor"/> to distinguish wide-range measurements
    /// (file_byte_length: ~50K distinct values in [50KB, 10MB]) from sparse autoincrement
    /// ID sequences (e.g. 5K distinct product IDs in [1, 206209]).
    /// </description></item>
    /// </list>
    /// </remarks>
    private static bool IsSparseIntegerMeasure(FeatureManifest manifest)
    {
        if (manifest is not NumericFeatureManifest { IntegerValued: true } numeric)
        {
            return false;
        }

        long ndv = manifest.EstimatedDistinctCount;
        if (ndv < SparseMeasureMinDistinctCount)
        {
            return false;
        }

        double range = numeric.Max - numeric.Min;
        if (range < SparseMeasureMinRange)
        {
            return false;
        }

        if (range / ndv < SparseMeasureRangeToDistinctRatio)
        {
            return false;
        }

        if (ndv > ForeignKeyMinDistinctCount && numeric.Min < SparseMeasureMinValueFloor)
        {
            return false;
        }

        return true;
    }

    private static bool IsIntegerKind(DataKind kind)
    {
        return kind is DataKind.UInt8 or DataKind.Int8 or DataKind.Int16 or DataKind.UInt16
            or DataKind.Int32 or DataKind.UInt32 or DataKind.Int64 or DataKind.UInt64;
    }
}
