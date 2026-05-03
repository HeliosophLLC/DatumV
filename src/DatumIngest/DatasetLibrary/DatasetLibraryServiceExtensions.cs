// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

using DatumIngest.DatasetLibrary;

using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI registration for the dataset library: catalog manifest reader, path
/// resolver, and the download + install pipeline. Reuses the model-side
/// <see cref="DatumIngest.ModelLibrary.IModelSourceClient"/> family (those
/// clients are caller-agnostic — they download files, period) and the
/// model-side <see cref="DatumIngest.ModelLibrary.ILicenseAcceptanceService"/>
/// so license acceptance transfers across surfaces.
/// </summary>
public static class DatasetLibraryServiceExtensions
{
    public static IServiceCollection AddDatasetLibrary(
        this IServiceCollection services,
        DatasetLibraryOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<IManifestStore, ManifestStore>();
        services.AddSingleton<IDatasetPathResolver>(sp =>
            new VersionedDatasetPathResolver(sp.GetRequiredService<DatasetLibraryOptions>()));

        // Default to the no-op reporter so test hosts that don't need
        // SignalR-backed progress get a working DI graph. The Web host
        // replaces this with the SignalR-backed implementation via a
        // plain AddSingleton (last-wins).
        services.TryAddSingleton<IDatasetDownloadProgressReporter>(
            NullDatasetDownloadProgressReporter.Instance);

        // Default keep-raw policy returns `Ask`, which preserves the raw
        // cache. The Web host replaces this with a settings.json-backed
        // implementation so user preferences flow through.
        services.TryAddSingleton<IKeepRawDownloadsPolicy>(
            DefaultKeepRawDownloadsPolicy.Instance);

        services.AddSingleton<IDatasetDownloadService, DatasetDownloadService>();
        return services;
    }
}
