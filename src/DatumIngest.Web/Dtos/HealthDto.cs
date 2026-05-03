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
    string? ModelsDirectory = null,
    // Resolved raw-datasets cache directory — the path where downloaded
    // archives and their extracted trees live before ingest. Sourced
    // from the WebHostOptions override > $DATUM_DATASETS env var >
    // %LOCALAPPDATA%/DatumIngest/datasets-cache cascade. Null when no
    // dataset library is registered on the host.
    string? DatasetsCacheDirectory = null);
