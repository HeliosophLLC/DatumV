namespace Heliosoph.DatumV.Web.Dtos.Settings;

// Serialized as the camelCase string (System → "system" etc.) via the
// global JsonStringEnumConverter configured in WebHostExtensions. NSwag
// emits this as a TypeScript string union (per nswag.json's enumStyle).
public enum ThemePreference
{
    System,
    Light,
    Dark,
}
