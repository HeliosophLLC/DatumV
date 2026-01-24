namespace DatumIngest.Web.Dtos;

public sealed record HealthDto(
    string Status,
    string Version,
    string UserId,
    string DisplayName,
    string CatalogPath,
    string NodeId);
