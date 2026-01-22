namespace DatumIngest.Web.Hosting;

public sealed class WebHostOptions
{
    public string? CatalogPath { get; init; }

    public bool EnableSwagger { get; init; }

    public IReadOnlyList<string> CorsOrigins { get; init; } = Array.Empty<string>();
}
