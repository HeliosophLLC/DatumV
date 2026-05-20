namespace Heliosoph.DatumV.Web.Dtos.Settings;

// User's window-chrome preference. `Auto` follows the detected OS (see
// HostController); explicit values force a specific look for cross-platform
// testing or personal preference.
public enum ChromeStyle
{
    Auto,
    Windows,
    Macos,
    Linux,
}
