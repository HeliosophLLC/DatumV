namespace DatumIngest.Web.Hosting;

public sealed class WebHostOptions
{
    // Root directory the per-principal catalog path is derived from.
    // Desktop: %LOCALAPPDATA%/DatumIngest. Container: /var/lib/datum (or
    // wherever the volume is mounted). The principal resolver carves a
    // subdirectory from this for each user/tenant.
    public string? CatalogRootPath { get; init; }

    // When true (desktop default), AddDatumIngestWeb opens a singleton
    // TableCatalog at CatalogRootPath and runs embedded SQL migrations on
    // startup. When false (SaaS), provisioning is someone else's job —
    // ICatalogService routes by principal to a remote node and the local
    // process never opens a catalog file. Defaults to true so the desktop
    // path is the no-config option.
    public bool ManageLocalCatalog { get; init; } = true;

    public bool EnableSwagger { get; init; }

    public IReadOnlyList<string> CorsOrigins { get; init; } = Array.Empty<string>();
}
