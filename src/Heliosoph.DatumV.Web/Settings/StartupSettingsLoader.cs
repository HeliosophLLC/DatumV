using System.Text.Json;

using Heliosoph.DatumV.DatasetLibrary;
using Heliosoph.DatumV.Web.Hosting;

namespace Heliosoph.DatumV.Web.Settings;

// Reads selected fields out of settings.json synchronously at startup,
// before any DI service is constructed. Used to feed values like
// ModelsDirectory into the TableCatalog factory, which runs before
// ISettingsService is reachable (settings is a scoped per-request
// service — wrong shape for a startup boot path).
//
// settings.json lives under WebHostOptions.GlobalDataPath — settings are
// per-machine, not per-catalog, so they survive when the user swaps
// which workspace catalog they have open.
//
// Tolerant: a missing file, missing field, or malformed JSON all return
// null. The caller falls back to whatever default applies. Intentionally
// minimal — anything more elaborate should go through ISettingsService.
internal static class StartupSettingsLoader
{
    // Single source of truth for the global-data root. Callers that
    // hold a WebHostOptions go through this rather than repeating the
    // null-coalesce-to-LocalApplicationData fallback at every site.
    public static string ResolveGlobalDataPath(WebHostOptions options)
        => options.GlobalDataPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Heliosoph.DatumV");

    public static string ResolveSettingsFile(WebHostOptions options)
        => Path.Combine(ResolveGlobalDataPath(options), "settings.json");

    public static string? LoadModelsDirectory(WebHostOptions options)
        => ReadStringField(ResolveSettingsFile(options), "modelsDirectory");

    // Reads the catalog name the user picked for the chat LLM, or null
    // when the setting is unset / file missing. Called lazily by
    // LlmDriverHolder on first chat load — the setting is read each time
    // a fresh driver is needed, so flipping the setting then restarting
    // applies cleanly.
    public static string? LoadDefaultLlm(WebHostOptions options)
        => ReadStringField(ResolveSettingsFile(options), "defaultLlmModel");

    public static string? LoadDatasetsDirectory(WebHostOptions options)
        => ReadStringField(ResolveSettingsFile(options), "datasetsDirectory");

    // Reads the user's current KeepRawDownloads preference. Called by the
    // SettingsBackedKeepRawDownloadsPolicy on every install — each install
    // re-reads the file so a flip to `never` applies on the next install
    // without a restart. Returns the default (`Ask`) when the file is
    // missing or the field is unset / unparseable.
    public static KeepRawDownloadsMode LoadKeepRawDownloads(WebHostOptions options)
    {
        string path = ResolveSettingsFile(options);
        if (!File.Exists(path)) return KeepRawDownloadsMode.Ask;

        try
        {
            using FileStream stream = File.OpenRead(path);
            using JsonDocument doc = JsonDocument.Parse(stream);

            if (doc.RootElement.TryGetProperty("keepRawDownloads", out JsonElement element)
                && element.ValueKind == JsonValueKind.String)
            {
                string? value = element.GetString();
                if (string.Equals(value, "always", StringComparison.OrdinalIgnoreCase))
                {
                    return KeepRawDownloadsMode.Always;
                }
                if (string.Equals(value, "never", StringComparison.OrdinalIgnoreCase))
                {
                    return KeepRawDownloadsMode.Never;
                }
                // "ask" or any unknown value → default.
            }
        }
        catch (Exception)
        {
            // Swallow; the install path still runs with the default.
        }
        return KeepRawDownloadsMode.Ask;
    }

    private static string? ReadStringField(string settingsFile, string fieldName)
    {
        if (!File.Exists(settingsFile)) return null;

        try
        {
            using FileStream stream = File.OpenRead(settingsFile);
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
