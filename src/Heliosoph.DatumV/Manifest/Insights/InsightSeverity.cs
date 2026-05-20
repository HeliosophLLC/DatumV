namespace Heliosoph.DatumV.Manifest.Insights;

/// <summary>
/// Indicates the urgency of a <see cref="DatasetInsight"/>.
/// </summary>
public enum InsightSeverity
{
    /// <summary>Informational observation with no immediate action required.</summary>
    Info,

    /// <summary>Issue that may degrade model quality if left unaddressed.</summary>
    Warning,

    /// <summary>Serious issue that is likely to cause training failures or significant model degradation.</summary>
    Critical
}
