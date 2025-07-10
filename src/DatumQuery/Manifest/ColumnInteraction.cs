namespace Axon.QueryEngine.Manifest;

/// <summary>
/// Describes the statistical relationship between two columns in the result set.
/// Only applicable measures are populated; others remain null and are omitted from JSON.
/// </summary>
public sealed class ColumnInteraction
{
    /// <summary>Gets the name of the first column.</summary>
    public required string ColumnA { get; init; }

    /// <summary>Gets the name of the second column.</summary>
    public required string ColumnB { get; init; }

    /// <summary>Gets the Pearson product-moment correlation coefficient (numeric × numeric).</summary>
    public double? Pearson { get; init; }

    /// <summary>Gets the Spearman rank correlation coefficient (numeric × numeric).</summary>
    public double? Spearman { get; init; }

    /// <summary>Gets Cramér's V association measure (categorical × categorical).</summary>
    public double? CramerV { get; init; }

    /// <summary>Gets the ANOVA F-statistic (categorical × numeric).</summary>
    public double? AnovaFStatistic { get; init; }

    /// <summary>Gets mutual information in bits (all pair types).</summary>
    public double? MutualInformation { get; init; }

    /// <summary>Gets Theil's U(A|B): how much column B reduces uncertainty about column A. Range [0, 1].</summary>
    public double? TheilUAB { get; init; }

    /// <summary>Gets Theil's U(B|A): how much column A reduces uncertainty about column B. Range [0, 1].</summary>
    public double? TheilUBA { get; init; }

    /// <summary>Gets the Pearson correlation between null masks of the two columns. Range [-1, 1].</summary>
    public double? MissingnessCorrelation { get; init; }
}
