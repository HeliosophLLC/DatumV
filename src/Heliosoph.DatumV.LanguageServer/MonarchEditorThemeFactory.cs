namespace Heliosoph.DatumV.LanguageServer;

/// <summary>
/// Builds the Monaco editor themes that pair with
/// <see cref="MonarchGrammarFactory"/>. Token-color rules reference the
/// same token type names the grammar emits (<c>predefined.function</c>,
/// <c>keyword</c>, …), so the palette is the single source of truth for
/// every editor instance in the client.
/// </summary>
public static class MonarchEditorThemeFactory
{
    /// <summary>
    /// Returns the light and dark theme bodies in the shape Monaco's
    /// <c>monaco.editor.defineTheme</c> expects (<c>IStandaloneThemeData</c>).
    /// Both inherit from the corresponding built-in theme so unset token
    /// types keep their stock colors.
    /// </summary>
    public static object BuildThemes() => new
    {
        light = new
        {
            @base = "vs",
            inherit = true,
            rules = new[]
            {
                // Registered SQL functions (replace, sum, apply_colormap, …).
                // SSMS-style magenta — the classic "pink" that long-time SQL
                // Server users associate with system functions.
                new { token = "predefined.function", foreground = "FF00FF" },
            },
            colors = new Dictionary<string, string>(),
        },
        dark = new
        {
            @base = "vs-dark",
            inherit = true,
            rules = new[]
            {
                // Lightened magenta for legibility on the dark surface.
                new { token = "predefined.function", foreground = "FF5DFF" },
            },
            colors = new Dictionary<string, string>(),
        },
    };
}
