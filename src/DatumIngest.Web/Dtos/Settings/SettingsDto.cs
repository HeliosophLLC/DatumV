namespace DatumIngest.Web.Dtos.Settings;

// Full settings document — what GET /api/settings returns. All fields
// non-nullable; defaults are applied server-side when the file is missing or
// a field is unset, so clients never see nulls here.
public sealed record SettingsDto(
    ThemePreference Theme,
    ChromeStyle ChromeStyle,
    string Locale,
    // User-configured models directory. Empty string = use the resolution
    // cascade ($DATUM_MODELS env var → %LOCALAPPDATA%/DatumIngest/models).
    // When non-empty, takes precedence over both. Read once at startup by
    // StartupSettingsLoader; runtime changes require a restart to apply.
    string ModelsDirectory,
    bool Animations,
    // Dock layout. PanelIds: "chat" | "catalog" | "procedures" | "projects".
    // Settings is always pinned to the bottom of the left dock and is not
    // listed here. The right dock is hidden entirely when DockRightItems is
    // empty. OpenLeftPanel / OpenRightPanel are null when that side has no
    // panel open. Order in each list reflects icon order in the dock.
    IReadOnlyList<string> DockLeftItems,
    IReadOnlyList<string> DockRightItems,
    string? OpenLeftPanel,
    string? OpenRightPanel);

// Partial document for PATCH /api/settings. All fields nullable; null means
// "don't change this field." Server merges with the current document and
// writes the result atomically.
public sealed record SettingsPatchDto(
    ThemePreference? Theme = null,
    ChromeStyle? ChromeStyle = null,
    string? Locale = null,
    string? ModelsDirectory = null,
    bool? Animations = null,
    IReadOnlyList<string>? DockLeftItems = null,
    IReadOnlyList<string>? DockRightItems = null,
    // Opt-in null sentinels — use the boolean clears below when patching the
    // "no panel open" state, since `null` here means "don't change."
    string? OpenLeftPanel = null,
    string? OpenRightPanel = null,
    bool ClearOpenLeftPanel = false,
    bool ClearOpenRightPanel = false);
