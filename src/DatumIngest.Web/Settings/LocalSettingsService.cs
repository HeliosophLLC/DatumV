using System.Text.Json;
using System.Text.Json.Serialization;
using DatumIngest.Web.Dtos.Settings;
using DatumIngest.Web.Hosting;

namespace DatumIngest.Web.Settings;

// JSON-file impl of ISettingsService. Reads on demand (low frequency
// today; can add caching when patterns require). Atomic writes via
// temp-file + rename so a torn write can't corrupt the document.
internal sealed class LocalSettingsService(ICurrentContext context) : ISettingsService
{
    // Locale is a BCP 47 tag (e.g. "en", "en-US") or the sentinel "system",
    // which tells the client to resolve from navigator.language. Server
    // doesn't enumerate supported tags — that's driven by which locale
    // bundles the client ships.
    //
    // ModelsDirectory empty string = "use the resolution cascade"
    // (env var → default location). The Settings UI surfaces the resolved
    // effective path via the Health endpoint.
    private static readonly SettingsDto Defaults = new(
        Theme: ThemePreference.System,
        ChromeStyle: ChromeStyle.Auto,
        Locale: "system",
        ModelsDirectory: "",
        Animations: true);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private string SettingsPath => Path.Combine(context.CatalogPath, "settings.json");

    public async Task<SettingsDto> GetAsync(CancellationToken ct = default)
    {
        if (!File.Exists(SettingsPath))
        {
            return Defaults;
        }

        await using var stream = File.OpenRead(SettingsPath);
        var dto = await JsonSerializer.DeserializeAsync<SettingsDto>(stream, JsonOptions, ct);
        return dto ?? Defaults;
    }

    public async Task<SettingsDto> PatchAsync(SettingsPatchDto patch, CancellationToken ct = default)
    {
        var current = await GetAsync(ct);
        var merged = new SettingsDto(
            Theme: patch.Theme ?? current.Theme,
            ChromeStyle: patch.ChromeStyle ?? current.ChromeStyle,
            Locale: patch.Locale ?? current.Locale,
            ModelsDirectory: patch.ModelsDirectory ?? current.ModelsDirectory,
            Animations: patch.Animations ?? current.Animations);

        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);

        var tempPath = SettingsPath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, merged, JsonOptions, ct);
        }
        File.Move(tempPath, SettingsPath, overwrite: true);

        return merged;
    }
}
