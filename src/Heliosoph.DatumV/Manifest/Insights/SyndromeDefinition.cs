namespace Heliosoph.DatumV.Manifest.Insights;

/// <summary>
/// Defines a syndrome — a compound insight pattern where multiple atomic findings
/// co-occur on overlapping features and warrant bundled treatment instead of
/// individual recommendations.
/// </summary>
/// <param name="Kind">The compound <see cref="InsightKind"/> emitted when this syndrome matches.</param>
/// <param name="ComponentKinds">Atomic insight kinds that must co-occur (at least 2 required) on overlapping features.</param>
/// <param name="Category">Category assigned to the compound insight.</param>
/// <param name="Severity">Severity assigned to the compound insight.</param>
internal sealed record SyndromeDefinition(
    InsightKind Kind,
    IReadOnlySet<InsightKind> ComponentKinds,
    InsightCategory Category,
    InsightSeverity Severity);
