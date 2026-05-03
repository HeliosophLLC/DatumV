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

    // Attaches the model subsystem via ModelHost.AttachTo — loads the
    // models/catalog.json manifest, wires the ModelCatalog onto the
    // TableCatalog, registers system.* model providers, and runs the
    // catalog-driven Python registrar. Cheap registration — actual model
    // loads are lazy via the residency manager. Set false for SaaS or
    // test hosts that don't want catalog-driven SQL models. The property
    // name predates the SQL-LLM migration; it's preserved as public API.
    public bool RegisterBuiltinModels { get; init; } = true;

    // Override for the model files directory. When null, ModelCatalog uses
    // the $DATUM_MODELS env var or %LOCALAPPDATA%/DatumIngest/models.
    public string? ModelsDirectory { get; init; }

    // Override for the raw datasets cache directory — where downloaded
    // archives and their extracted trees land before ingest. When null,
    // the dataset library uses $DATUM_DATASETS or
    // %LOCALAPPDATA%/DatumIngest/datasets-cache. Distinct from
    // CatalogRootPath because ingested .datum files live under the
    // catalog root (datasets/ subfolder) while raw archives are
    // expendable per the user's keepRawDownloads setting.
    public string? DatasetsCacheDirectory { get; init; }

    // System prompt prepended to every conversation. When null, the chat
    // agent uses its built-in default. Read once at agent construction.
    public string? SystemPrompt { get; init; }

    public bool EnableSwagger { get; init; }

    public IReadOnlyList<string> CorsOrigins { get; init; } = Array.Empty<string>();
}
