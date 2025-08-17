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
    /// Classifies a column's semantic role from its manifest statistics.
    /// </summary>
    /// <param name="manifest">The feature manifest for the column.</param>
    /// <param name="rowCount">Total number of rows in the table.</param>
    /// <returns>The inferred <see cref="ColumnRole"/>.</returns>
    public static ColumnRole Classify(FeatureManifest manifest, long rowCount)
    {
        // Structural kinds: Vector, Matrix, Tensor.
        if (manifest.Kind is DataKind.Vector or DataKind.Matrix or DataKind.Tensor)
        {
            return ColumnRole.Structural;
        }

        // Binary kinds: UInt8Array, Image.
        if (manifest.Kind is DataKind.UInt8Array or DataKind.Image)
        {
            return ColumnRole.Binary;
        }

        // Temporal kinds: Date, DateTime, Time, Duration.
        if (manifest.Kind is DataKind.Date or DataKind.DateTime or DataKind.Time or DataKind.Duration)
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

        // String / JsonValue kinds.
        if (manifest.Kind is DataKind.String or DataKind.JsonValue)
        {
            return ClassifyStringColumn(manifest, distinctRatio);
        }

        // Array kind — treat as structural.
        if (manifest.Kind is DataKind.Array)
        {
            return ColumnRole.Structural;
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

        // Trivially small vocabulary → Categorical.
        if (manifest.EstimatedDistinctCount <= TrivialVocabularySize)
        {
            return ColumnRole.Categorical;
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

    /// <summary>
    /// Classifies a string or JSON column. Strings never classify as Identifier or
    /// ForeignKey — prefer omitting a join over generating false positives from
    /// string matching.
    /// </summary>
    private static ColumnRole ClassifyStringColumn(FeatureManifest manifest, double distinctRatio)
    {
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

    private static bool IsIntegerKind(DataKind kind)
    {
        return kind is DataKind.UInt8 or DataKind.Int8 or DataKind.Int16 or DataKind.UInt16
            or DataKind.Int32 or DataKind.UInt32 or DataKind.Int64 or DataKind.UInt64;
    }
}
