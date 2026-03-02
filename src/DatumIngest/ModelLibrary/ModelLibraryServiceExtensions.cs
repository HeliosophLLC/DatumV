// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

using DatumIngest.ModelLibrary;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI registration for the model library: catalog manifest reader, HF Hub
/// HTTP client, license-acceptance store, and the download orchestrator.
/// </summary>
public static class ModelLibraryServiceExtensions
{
    /// <summary>
    /// Registers the model library services. The caller must separately
    /// register an <see cref="IDownloadProgressReporter"/> â€” the Web host
    /// registers a SignalR-backed implementation; tests and CLI consumers
    /// typically register <see cref="NullDownloadProgressReporter"/>.
    /// </summary>
    public static IServiceCollection AddModelLibrary(
        this IServiceCollection services,
        ModelLibraryOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<IManifestStore, ManifestStore>();
        services.AddSingleton<ILicenseAcceptanceService, LicenseAcceptanceService>();
        services.AddHttpClient<HfHubClient>();
        services.AddSingleton<IModelDownloadService, ModelDownloadService>();
        return services;
    }
}
