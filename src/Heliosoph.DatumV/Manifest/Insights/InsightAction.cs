namespace Heliosoph.DatumV.Manifest.Insights;

/// <summary>
/// A structured transformation action recommended by a <see cref="DatasetInsight"/>.
/// Actions describe concrete changes to the query projection or filter.
/// </summary>
/// <param name="Kind">The type of transformation (Drop, Replace, Append, Filter).</param>
/// <param name="Column">Target column name. Null for Filter or Append actions that create new columns.</param>
/// <param name="Expression">SQL expression for the transformation. Null for Drop actions.</param>
/// <param name="Alias">Output column name for Append actions. Null for other action types.</param>
/// <param name="Lossy">Whether this action discards information that cannot be recovered.</param>
/// <param name="Reversible">Whether the original data can be reconstructed from the output.</param>
/// <param name="BundleIdentifier">
/// Optional identifier grouping related actions that must be applied atomically
/// (all or none). For example, a zero-inflated column's indicator and conditional
/// log-transform share a bundle so they are never applied independently.
/// </param>
public sealed record InsightAction(
    ActionKind Kind,
    string? Column,
    string? Expression,
    string? Alias,
    bool Lossy,
    bool Reversible,
    string? BundleIdentifier);
