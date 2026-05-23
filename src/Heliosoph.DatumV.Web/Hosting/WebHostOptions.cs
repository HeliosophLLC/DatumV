namespace Heliosoph.DatumV.Web.Hosting;

public sealed class WebHostOptions
{
    // Root directory of the workspace catalog the user has opened. One
    // folder = one catalog (VSCode-workspace model); the Electron shell
    // hands the path to us per launch. No fallback — if this is null
    // when ManageLocalCatalog is true, host startup fails fast.
    public string? CatalogRootPath { get; init; }

    // Per-machine global storage. Holds settings.json, the recent-
    // catalogs list, downloaded models, and anything else that lives
    // independently of which workspace catalog is open. Defaults to
    // %LOCALAPPDATA%/Heliosoph.DatumV when null. The Electron shell
    // computes the same path and passes it via DATUMV_GLOBAL_PATH so
    // both sides agree.
    public string? GlobalDataPath { get; init; }

    // When true (desktop default), AddDatumVWeb opens a singleton
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
    // the $DATUMV_MODELS env var or %LOCALAPPDATA%/Heliosoph.DatumV/models.
    public string? ModelsDirectory { get; init; }

    // Override for the raw datasets cache directory — where downloaded
    // archives and their extracted trees land before ingest. When null,
    // the dataset library uses $DATUMV_DATASETS or
    // %LOCALAPPDATA%/Heliosoph.DatumV/datasets-cache. Distinct from
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
