namespace DatumIngest.Catalog;

/// <summary>
/// Describes a sub-table discovered within a multi-table source file.
/// </summary>
/// <param name="Name">
/// Sub-table qualifier (e.g. the JSON property name). Used as the suffix
/// in the catalog table name: <c>{baseName}.{Name}</c>.
/// </param>
/// <param name="Options">
/// Merged provider options for this sub-table. Typically includes the original
/// options plus any path override (e.g. <c>json_path</c>) needed to reach
/// the sub-table within the source file.
/// </param>
public sealed record DiscoveredTable(string Name, IReadOnlyDictionary<string, string> Options);
