namespace DatumIngest.Web.Hosting;

public sealed class WebHostOptions
{
    // Root directory the per-principal catalog path is derived from.
    // Desktop: %LOCALAPPDATA%/DatumIngest. Container: /var/lib/datum (or
    // wherever the volume is mounted). The principal resolver carves a
    // subdirectory from this for each user/tenant.
    public string? CatalogRootPath { get; init; }

    public bool EnableSwagger { get; init; }

    public IReadOnlyList<string> CorsOrigins { get; init; } = Array.Empty<string>();
}
