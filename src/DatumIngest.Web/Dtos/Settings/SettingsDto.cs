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
    string ModelsDirectory);

// Partial document for PATCH /api/settings. All fields nullable; null means
// "don't change this field." Server merges with the current document and
// writes the result atomically.
public sealed record SettingsPatchDto(
    ThemePreference? Theme = null,
    ChromeStyle? ChromeStyle = null,
    string? Locale = null,
    string? ModelsDirectory = null);
