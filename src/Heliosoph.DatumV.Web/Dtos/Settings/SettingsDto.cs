using Heliosoph.DatumV.DatasetLibrary;

namespace Heliosoph.DatumV.Web.Dtos.Settings;

// Full settings document — what GET /api/settings returns. All fields
// non-nullable; defaults are applied server-side when the file is missing or
// a field is unset, so clients never see nulls here.
public sealed record SettingsDto(
    ThemePreference Theme,
    ChromeStyle ChromeStyle,
    string Locale,
    // User-configured models directory. Empty string = use the resolution
    // cascade ($DATUMV_MODELS env var → %LOCALAPPDATA%/Heliosoph.DatumV/models).
    // When non-empty, takes precedence over both. Read once at startup by
    // StartupSettingsLoader; runtime changes require a restart to apply.
    string ModelsDirectory,
    // User-configured raw datasets cache directory. Empty string = use
    // the resolution cascade ($DATUMV_DATASETS env var →
    // %LOCALAPPDATA%/Heliosoph.DatumV/datasets-cache). When non-empty, takes
    // precedence over both. Read once at startup by StartupSettingsLoader;
    // runtime changes require a restart to apply.
    string DatasetsDirectory,
    // What to do with the raw archives in the dataset cache after a
    // successful ingest. `Ask` keeps the cache and (in a later release)
    // shows a prompt asking the user to remember `Always` or `Never`.
    // Default is `Ask` so the first install never silently deletes
    // anything.
    KeepRawDownloadsMode KeepRawDownloads,
    bool Animations,
    // Dock layout. PanelIds: "chat" | "catalog" | "procedures" | "projects".
    // Settings is always pinned to the bottom of the left dock and is not
    // listed here. The right dock is hidden entirely when DockRightItems is
    // empty. OpenLeftPanel / OpenRightPanel are null when that side has no
    // panel open. Order in each list reflects icon order in the dock.
    IReadOnlyList<string> DockLeftItems,
    IReadOnlyList<string> DockRightItems,
    string? OpenLeftPanel,
    string? OpenRightPanel,
    // Per-cell-kind default display mode for the results-pane data grid
    // (e.g. {"numeric_array": "histogram"}). Keys are the column-mode-
    // registry's `kindKey`; values are mode ids registered for that kind.
    // Missing keys fall back to the registry's `defaultMode`. The client
    // owns the registry — server is just persistence.
    IReadOnlyDictionary<string, string> ColumnDisplayModeDefaults,
    // Catalog name of the LLM the chat surface should prefer when one
    // exists. Null means "auto" — ModelSelector picks the largest model
    // that fits in the VRAM budget. The chat driver reads this once on
    // first load; runtime changes require a restart to apply.
    string? DefaultLlmModel = null);

// Partial document for PATCH /api/settings. All fields nullable; null means
// "don't change this field." Server merges with the current document and
// writes the result atomically.
public sealed record SettingsPatchDto(
    ThemePreference? Theme = null,
    ChromeStyle? ChromeStyle = null,
    string? Locale = null,
    string? ModelsDirectory = null,
    string? DatasetsDirectory = null,
    KeepRawDownloadsMode? KeepRawDownloads = null,
    bool? Animations = null,
    IReadOnlyList<string>? DockLeftItems = null,
    IReadOnlyList<string>? DockRightItems = null,
    // Opt-in null sentinels — use the boolean clears below when patching the
    // "no panel open" state, since `null` here means "don't change."
    string? OpenLeftPanel = null,
    string? OpenRightPanel = null,
    bool ClearOpenLeftPanel = false,
    bool ClearOpenRightPanel = false,
    // Patched as a full-dict replace (no per-key merging). Sending {} clears
    // every persisted mode; null leaves the existing dict alone. Per-kind
    // upserts are done client-side by reading the current dict, mutating,
    // and re-sending — keeps the server's merge logic uniform.
    IReadOnlyDictionary<string, string>? ColumnDisplayModeDefaults = null,
    // Catalog name of the preferred LLM. Mirrors SettingsDto.DefaultLlmModel.
    // Null on the patch means "don't change" — use ClearDefaultLlmModel to
    // explicitly fall back to auto-pick.
    string? DefaultLlmModel = null,
    bool ClearDefaultLlmModel = false);
