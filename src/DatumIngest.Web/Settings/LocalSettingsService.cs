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
    // Default dock layout: every panel icon lives on the left dock at app
    // first-launch (right dock hidden until the user drags an icon over).
    // Settings is pinned to the bottom of the left dock by the renderer and
    // is not part of this list. Order = display order.
    private static readonly IReadOnlyList<string> DefaultDockLeftItems =
        ["chat", "catalog", "procedures", "projects"];

    private static readonly IReadOnlyList<string> DefaultDockRightItems =
        Array.Empty<string>();

    private static readonly SettingsDto Defaults = new(
        Theme: ThemePreference.System,
        ChromeStyle: ChromeStyle.Auto,
        Locale: "system",
        ModelsDirectory: "",
        Animations: true,
        DockLeftItems: DefaultDockLeftItems,
        DockRightItems: DefaultDockRightItems,
        OpenLeftPanel: null,
        OpenRightPanel: null);

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
        if (dto is null) return Defaults;
        // Heal old documents that pre-date the dock fields. A positional
        // record deserialized from JSON missing those keys would hand us
        // `null!` for the collection slots, which then trips downstream
        // code. Treat missing-or-null as "use the default layout."
        return FillCollectionDefaults(dto);
    }

    private static SettingsDto FillCollectionDefaults(SettingsDto dto)
    {
        if (dto.DockLeftItems is not null && dto.DockRightItems is not null)
        {
            return dto;
        }
        return dto with
        {
            DockLeftItems = dto.DockLeftItems ?? DefaultDockLeftItems,
            DockRightItems = dto.DockRightItems ?? DefaultDockRightItems,
        };
    }

    public async Task<SettingsDto> PatchAsync(SettingsPatchDto patch, CancellationToken ct = default)
    {
        var current = await GetAsync(ct);
        var merged = new SettingsDto(
            Theme: patch.Theme ?? current.Theme,
            ChromeStyle: patch.ChromeStyle ?? current.ChromeStyle,
            Locale: patch.Locale ?? current.Locale,
            ModelsDirectory: patch.ModelsDirectory ?? current.ModelsDirectory,
            Animations: patch.Animations ?? current.Animations,
            DockLeftItems: patch.DockLeftItems ?? current.DockLeftItems,
            DockRightItems: patch.DockRightItems ?? current.DockRightItems,
            // Clear flags win over the value field — passing both is a
            // client bug but the clearer of the two semantically wins.
            OpenLeftPanel: patch.ClearOpenLeftPanel
                ? null
                : patch.OpenLeftPanel ?? current.OpenLeftPanel,
            OpenRightPanel: patch.ClearOpenRightPanel
                ? null
                : patch.OpenRightPanel ?? current.OpenRightPanel);

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
