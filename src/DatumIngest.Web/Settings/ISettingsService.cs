using DatumIngest.Web.Dtos.Settings;

namespace DatumIngest.Web.Settings;

// Per-user (today: per-local-user) settings. Backed by a JSON file under
// the principal's catalog path. Scoped per request so the file path is
// derived from ICurrentContext.CatalogPath.
public interface ISettingsService
{
    // Returns the user's settings, with defaults filled in for unset fields.
    // Always succeeds; if the file doesn't exist, returns a fresh defaults document.
    Task<SettingsDto> GetAsync(CancellationToken ct = default);

    // Merges the patch into the current document (null fields ignored) and
    // writes the result atomically. Returns the merged document.
    Task<SettingsDto> PatchAsync(SettingsPatchDto patch, CancellationToken ct = default);
}
