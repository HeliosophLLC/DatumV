namespace DatumIngest.Manifest.Insights;

/// <summary>
/// Configurable thresholds for <see cref="IInsightRule"/> implementations.
/// Controls when each insight pattern fires. Defaults are intentionally conservative.
/// </summary>
public sealed class InsightThresholds
{
    // ── Data Quality ──

    /// <summary>
    /// Null ratio above which a column is flagged for high missingness.
    /// Default: 0.3 (more than 30% missing).
    /// </summary>
    public double HighMissingnessMinRatio { get; init; } = 0.3;

    /// <summary>
    /// Null ratio above which a column is flagged as critically missing (likely unusable).
    /// Default: 0.8 (more than 80% missing).
    /// </summary>
    public double CriticalMissingnessMinRatio { get; init; } = 0.8;

    /// <summary>
    /// Missingness correlation above which two columns are considered part of a correlated group.
    /// Default: 0.95.
    /// </summary>
    public double MissingnessCorrelationMinThreshold { get; init; } = 0.95;

    // ── Distribution ──

    /// <summary>
    /// Zero ratio above which a numeric column is flagged as zero-inflated.
    /// Default: 0.5 (more than half the values are zero).
    /// </summary>
    public double ZeroInflatedMinRatio { get; init; } = 0.5;

    /// <summary>
    /// Minimum skewness magnitude for a numeric column to be flagged as right-skewed.
    /// Default: 2.0.
    /// </summary>
    public double RightSkewedMinSkewness { get; init; } = 2.0;

    /// <summary>
    /// Maximum skewness (negative) for a numeric column to be flagged as left-skewed.
    /// Default: −2.0.
    /// </summary>
    public double LeftSkewedMaxSkewness { get; init; } = -2.0;

    /// <summary>
    /// Minimum excess kurtosis for a numeric column to be flagged as heavy-tailed.
    /// Default: 7.0.
    /// </summary>
    public double HeavyTailedMinKurtosis { get; init; } = 7.0;

    /// <summary>
    /// Outlier ratio above which a numeric column is flagged for extreme outliers.
    /// Default: 0.05 (more than 5% of values are Z-score outliers).
    /// </summary>
    public double ExtremeOutlierMinRatio { get; init; } = 0.05;

    // ── Encoding ──

    /// <summary>
    /// Maximum distinct count for an integer-valued numeric column to be considered ordinal.
    /// Default: 30.
    /// </summary>
    public long PossibleOrdinalMaxDistinct { get; init; } = 30;

    /// <summary>
    /// Maximum distinct count for a string column to be recommended for one-hot encoding.
    /// Default: 20.
    /// </summary>
    public long OneHotMaxDistinct { get; init; } = 20;

    // ── Redundancy ──

    /// <summary>
    /// Minimum absolute Pearson or Spearman correlation for two numeric columns to be
    /// flagged as near-duplicate. Default: 0.95.
    /// </summary>
    public double NearDuplicateMinCorrelation { get; init; } = 0.95;

    /// <summary>
    /// Minimum Cramér's V for two categorical columns to be flagged as near-duplicate.
    /// Default: 0.95.
    /// </summary>
    public double NearDuplicateMinCramerV { get; init; } = 0.95;

    // ── Dimensionality ──

    /// <summary>
    /// Minimum distinct-to-row ratio for a column to be flagged as a possible identifier.
    /// Default: 0.9.
    /// </summary>
    public double PossibleIdentifierMinDistinctRatio { get; init; } = 0.9;

    /// <summary>
    /// Maximum top-K coverage ratio for a possible-identifier column.
    /// Default: 0.05.
    /// </summary>
    public double PossibleIdentifierMaxTopKCoverage { get; init; } = 0.05;

    // ── Scale ──

    /// <summary>
    /// Minimum range (max − min) relative to mean for a numeric column to be flagged
    /// for normalization. Default: 10.0 (range is 10× the mean).
    /// </summary>
    public double NormalizationMinRangeMeanRatio { get; init; } = 10.0;

    // ── Image Quality ──

    /// <summary>
    /// Minimum ratio of tiny images (width or height below 32) for an image column
    /// to be flagged. Default: 0.01 (more than 1% tiny).
    /// </summary>
    public double TinyImageMinRatio { get; init; } = 0.01;

    /// <summary>
    /// Minimum ratio of huge images (width or height above 4096) for an image column
    /// to be flagged. Default: 0.01 (more than 1% huge).
    /// </summary>
    public double HugeImageMinRatio { get; init; } = 0.01;

    /// <summary>
    /// Minimum ratio of undecodable images for an image column to be flagged.
    /// Default: 0.01 (more than 1% undecodable).
    /// </summary>
    public double UndecodableImageMinRatio { get; init; } = 0.01;

    /// <summary>Default threshold values.</summary>
    public static InsightThresholds Default { get; } = new();
}
