namespace DatumIngest.Web.Dtos;

public sealed record HealthDto(
    string Status,
    string Version,
    string UserId,
    string DisplayName,
    string CatalogPath,
    string NodeId,
    // Resolved models directory — the path actually being used by the
    // ModelCatalog after the settings.json override > $DATUM_MODELS env
    // var > %LOCALAPPDATA%/DatumIngest/models cascade. Null when the host
    // is in SaaS mode (no local ModelCatalog attached).
    string? ModelsDirectory = null);
