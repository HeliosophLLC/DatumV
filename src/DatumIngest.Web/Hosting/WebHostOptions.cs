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

    // Attaches BuiltinModels (LLMs, vision, audio, etc.) to the local
    // TableCatalog so SQL surfaces `models.X(...)` can resolve and the chat
    // surface can pick an LLM. Cheap registration — actual loads are lazy
    // via the residency manager. Set false for SaaS or test hosts that
    // don't want the standard zoo.
    public bool RegisterBuiltinModels { get; init; } = true;

    // Override for the model files directory. When null, ModelCatalog uses
    // the $DATUM_MODELS env var or %LOCALAPPDATA%/DatumIngest/models.
    public string? ModelsDirectory { get; init; }

    // System prompt prepended to every conversation. When null, the chat
    // agent uses its built-in default. Read once at agent construction.
    public string? SystemPrompt { get; init; }

    public bool EnableSwagger { get; init; }

    public IReadOnlyList<string> CorsOrigins { get; init; } = Array.Empty<string>();
}
