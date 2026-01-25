namespace DatumIngest.Web.Dtos.Settings;

// Full settings document — what GET /api/settings returns. All fields
// non-nullable; defaults are applied server-side when the file is missing or
// a field is unset, so clients never see nulls here.
public sealed record SettingsDto(ThemePreference Theme);

// Partial document for PATCH /api/settings. All fields nullable; null means
// "don't change this field." Server merges with the current document and
// writes the result atomically.
public sealed record SettingsPatchDto(ThemePreference? Theme = null);
