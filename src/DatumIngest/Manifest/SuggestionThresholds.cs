namespace DatumIngest.Manifest;

/// <summary>
/// Configurable thresholds for the <see cref="SuggestionEngine"/>. Each threshold
/// controls when a particular heuristic suggestion tag is emitted. The defaults
/// are intentionally conservative — obvious cases trigger, borderline ones do not.
/// </summary>
public sealed class SuggestionThresholds
{
    /// <summary>
    /// Zero ratio above which a numeric column is tagged <c>zero-inflated</c>.
    /// Default: 0.5 (more than half the values are zero).
    /// </summary>
    public double ZeroInflatedMinRatio { get; init; } = 0.5;

    /// <summary>
    /// Maximum distinct count for an integer-valued numeric column to be tagged
    /// <c>possible-ordinal</c>. Default: 30.
    /// </summary>
    public long PossibleOrdinalMaxDistinct { get; init; } = 30;

    /// <summary>
    /// Minimum ratio of estimated distinct count to row count for a column to be
    /// tagged <c>possible-identifier</c>. Default: 0.9.
    /// </summary>
    public double PossibleIdentifierMinDistinctRatio { get; init; } = 0.9;

    /// <summary>
    /// Maximum top-K coverage ratio for a column to be tagged <c>possible-identifier</c>.
    /// High coverage means a few values dominate, which is not identifier-like.
    /// Default: 0.05.
    /// </summary>
    public double PossibleIdentifierMaxTopKCoverage { get; init; } = 0.05;

    /// <summary>
    /// Minimum ratio of estimated distinct count to row count for a column to be
    /// tagged <c>high-cardinality</c>. Default: 0.5.
    /// </summary>
    public double HighCardinalityMinDistinctRatio { get; init; } = 0.5;

    /// <summary>
    /// Maximum distinct count for a column to be tagged <c>low-cardinality</c>.
    /// Default: 20.
    /// </summary>
    public long LowCardinalityMaxDistinct { get; init; } = 20;

    /// <summary>
    /// Minimum skewness magnitude for a numeric column to be tagged
    /// <c>right-skewed</c>. Default: 2.0.
    /// </summary>
    public double RightSkewedMinSkewness { get; init; } = 2.0;

    /// <summary>
    /// Maximum skewness (negative) for a numeric column to be tagged
    /// <c>left-skewed</c>. Default: −2.0.
    /// </summary>
    public double LeftSkewedMaxSkewness { get; init; } = -2.0;

    /// <summary>
    /// Minimum excess kurtosis for a numeric column to be tagged <c>heavy-tailed</c>.
    /// Default: 7.0.
    /// </summary>
    public double HeavyTailedMinKurtosis { get; init; } = 7.0;

    /// <summary>
    /// Minimum null ratio for a column to be tagged <c>high-missingness</c>.
    /// Default: 0.3 (more than 30% missing).
    /// </summary>
    public double HighMissingnessMinRatio { get; init; } = 0.3;

    /// <summary>Default threshold values.</summary>
    public static SuggestionThresholds Default { get; } = new();
}
