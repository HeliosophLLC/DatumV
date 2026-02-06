using System.Text.Json;

namespace DatumIngest.Web.Settings;

// Reads selected fields out of settings.json synchronously at startup,
// before any DI service is constructed. Used to feed values like
// ModelsDirectory into the TableCatalog factory, which runs before
// ISettingsService is reachable (settings is a scoped per-request
// service — wrong shape for a startup boot path).
//
// Tolerant: a missing file, missing field, or malformed JSON all return
// null. The caller falls back to whatever default applies. Intentionally
// minimal — anything more elaborate should go through ISettingsService.
internal static class StartupSettingsLoader
{
    // Reads {catalogRootPath}/settings.json and returns the user-configured
    // ModelsDirectory if present and non-empty. Null means "no user
    // override; use the host's default cascade."
    public static string? LoadModelsDirectory(string catalogRootPath)
    {
        string path = Path.Combine(catalogRootPath, "settings.json");
        if (!File.Exists(path)) return null;

        try
        {
            using FileStream stream = File.OpenRead(path);
            using JsonDocument doc = JsonDocument.Parse(stream);

            if (doc.RootElement.TryGetProperty("modelsDirectory", out JsonElement element)
                && element.ValueKind == JsonValueKind.String)
            {
                string? value = element.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        catch (Exception)
        {
            // Swallow — best-effort. A malformed settings file shouldn't
            // prevent startup; just fall through to the default cascade.
        }
        return null;
    }
}
