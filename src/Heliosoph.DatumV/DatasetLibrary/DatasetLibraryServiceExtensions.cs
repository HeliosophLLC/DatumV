// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.DatasetLibrary;

using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI registration for the dataset library: catalog manifest reader, path
/// resolver, and the download + install pipeline. Reuses the model-side
/// <see cref="Heliosoph.DatumV.ModelLibrary.IModelSourceClient"/> family (those
/// clients are caller-agnostic — they download files, period) and the
/// model-side <see cref="Heliosoph.DatumV.ModelLibrary.ILicenseAcceptanceService"/>
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

        // SQL-shape ingest executor — uses the live TableCatalog (which
        // must be registered by the host) to plan + execute the catalog
        // author's SELECT and stream the result rows into a .datum file.
        // Hosts that don't register a TableCatalog (CLI tools that don't
        // open any datasets) will only see catalog-load errors if a SQL-
        // shape job actually runs, so this is safe to register
        // unconditionally.
        services.AddSingleton<SqlIngestExecutor>();

        services.AddSingleton<IDatasetDownloadService, DatasetDownloadService>();

        // Catalog substrate: one DatasetSchemaCatalog instance owns every
        // schema the manifest declares; the binder swaps the per-table
        // snapshot atomically on boot and after every install/uninstall.
        // The hosting layer (e.g. Heliosoph.DatumV.Web's
        // DatasetCatalogInitializationService) mounts the catalog into
        // TableCatalog.Backends once both have constructed.
        services.AddSingleton(sp =>
        {
            IManifestStore store = sp.GetRequiredService<IManifestStore>();
            HashSet<string> schemas = new(StringComparer.OrdinalIgnoreCase);
            foreach (DatasetEntry e in store.Manifest.Datasets)
            {
                schemas.Add(e.Schema);
            }
            // Reuse the hosting TableCatalog's SidecarRegistry so dataset reads
            // share the storeId space with every other catalog backend.
            Heliosoph.DatumV.Catalog.TableCatalog catalog =
                sp.GetRequiredService<Heliosoph.DatumV.Catalog.TableCatalog>();
            return new DatasetSchemaCatalog(schemas, catalog.SidecarRegistry);
        });
        services.AddSingleton<DatasetSchemaBinder>();
        return services;
    }
}
