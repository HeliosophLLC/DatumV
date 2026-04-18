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
        => ReadStringField(catalogRootPath, "modelsDirectory");

    // Reads the catalog name the user picked for the chat LLM, or null
    // when the setting is unset / file missing. Called lazily by
    // LlmDriverHolder on first chat load — the setting is read each time
    // a fresh driver is needed, so flipping the setting then restarting
    // applies cleanly.
    public static string? LoadDefaultLlm(string catalogRootPath)
        => ReadStringField(catalogRootPath, "defaultLlmModel");

    private static string? ReadStringField(string catalogRootPath, string fieldName)
    {
        string path = Path.Combine(catalogRootPath, "settings.json");
        if (!File.Exists(path)) return null;

        try
        {
            using FileStream stream = File.OpenRead(path);
            using JsonDocument doc = JsonDocument.Parse(stream);

            if (doc.RootElement.TryGetProperty(fieldName, out JsonElement element)
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
