namespace Heliosoph.DatumV.Manifest.Insights;

/// <summary>
/// Policy tier controlling how an insight's actions are applied.
/// Derived from action nature and confidence — never authored directly by rules.
/// </summary>
public enum ApplyMode
{
    /// <summary>
    /// Safe to apply automatically. High confidence, lossless, or provably harmless
    /// (e.g., dropping a constant feature).
    /// </summary>
    AutoSafe,

    /// <summary>
    /// Recommended but involves lossy transformation. High confidence, non-destructive
    /// (e.g., log-transform a right-skewed column).
    /// </summary>
    Suggest,

    /// <summary>
    /// Requires explicit user review. Destructive action (drop/lossy replace) or
    /// insufficient confidence to recommend automatically.
    /// </summary>
    ManualOnly,

    /// <summary>
    /// Reserved for future use. Actions that should never be applied without
    /// domain-specific validation (e.g., suspected target leakage).
    /// </summary>
    Blocked
}
