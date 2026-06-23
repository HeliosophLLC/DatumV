// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

using System.Net.Http;

using Heliosoph.DatumV.ModelLibrary;
using Heliosoph.DatumV.Models.Python;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI registration for the model library: catalog manifest reader, the
/// per-channel <see cref="IModelSourceClient"/>s, license-acceptance store,
/// and the download orchestrator.
/// </summary>
public static class ModelLibraryServiceExtensions
{
    /// <summary>
    /// Registers the model library services. The caller must separately
    /// register an <see cref="IDownloadProgressReporter"/> — the Web host
    /// registers a SignalR-backed implementation; tests and CLI consumers
    /// typically register <see cref="NullDownloadProgressReporter"/>.
    /// </summary>
    /// <remarks>
    /// One <see cref="IModelSourceClient"/> is registered per channel
    /// (HuggingFace, GitHub release, plain HTTPS). The download
    /// orchestrator resolves them as an <see cref="IEnumerable{T}"/> and
    /// dispatches each catalog entry's <see cref="CatalogSource"/> to the
    /// matching client by its <see cref="IModelSourceClient.SupportedType"/>.
    /// Adding a new source kind = new record subtype on
    /// <see cref="CatalogSource"/> + one <c>AddHttpClient&lt;T&gt;</c>
    /// here + adding it to the dispatch switch in
    /// <c>ModelDownloadService.ResolveClient</c>.
    /// </remarks>
    public static IServiceCollection AddModelLibrary(
        this IServiceCollection services,
        ModelLibraryOptions options)
    {
        services.AddSingleton(options);
        // Central license registry (one licenses/index.json + text files
        // at the repo root). Both the model catalog and the dataset
        // catalog reference licenses by id from here. TryAddSingleton so
        // tests / standalone hosts that register a stub registry first
        // win.
        services.TryAddSingleton<ILicenseRegistry, LicenseRegistry>();
        services.AddSingleton<IManifestStore, ManifestStore>();
        services.AddSingleton<ILicenseAcceptanceService, LicenseAcceptanceService>();

        // Centralised "where does <id> live on disk?" lookup. Resolves
        // <id>/<active-version>/<rest> by consulting the wired
        // ICatalogActiveVersionLookup (the live catalog rows) plus the
        // optional ModelInstallContext.CurrentVersionPin override that
        // installs / rehydrates set for the duration of their async flow.
        // Hosts that don't register an ICatalogActiveVersionLookup get
        // NullCatalogActiveVersionLookup, which always returns null
        // (paths fall back to the version-less folder shape).
        services.TryAddSingleton<ICatalogActiveVersionLookup>(NullCatalogActiveVersionLookup.Instance);
        services.AddSingleton<IModelPathResolver>(sp => new VersionedModelPathResolver(
            sp.GetRequiredService<ModelLibraryOptions>(),
            sp.GetRequiredService<ICatalogActiveVersionLookup>()));

        // Each source client gets its own typed HttpClient so handler
        // pools, BaseAddress, and default headers stay isolated.
        //
        // The HF client gets a hand-rolled SocketsHttpHandler so we can
        // bound ConnectTimeout (catches unreachable hosts in <15s instead
        // of 100s) and recycle pooled connections every 2 min (stops a
        // dead TCP/TLS session from sticking after the machine wakes from
        // sleep or the user's Wi-Fi flips networks). The default is
        // PooledConnectionLifetime = Infinite, which is the most common
        // cause of "it worked yesterday and now it just hangs."
        services.AddHttpClient<HuggingFaceSourceClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
                ConnectTimeout = TimeSpan.FromSeconds(15),
                AutomaticDecompression = System.Net.DecompressionMethods.All,
            });
        services.AddHttpClient<GithubReleaseSourceClient>();
        services.AddHttpClient<HttpsSourceClient>();
        // Expose each as IModelSourceClient so the download orchestrator
        // resolves them as IEnumerable<IModelSourceClient> and dispatches
        // by SupportedType. Singletons because the typed HttpClient factory
        // itself manages handler lifetimes.
        services.AddSingleton<IModelSourceClient>(sp => sp.GetRequiredService<HuggingFaceSourceClient>());
        services.AddSingleton<IModelSourceClient>(sp => sp.GetRequiredService<GithubReleaseSourceClient>());
        services.AddSingleton<IModelSourceClient>(sp => sp.GetRequiredService<HttpsSourceClient>());

        // Default to the no-op installer so tests and CLI consumers (no SQL
        // catalog) don't need to think about install state. The Web host
        // replaces this with a catalog-backed installer before resolving.
        // TryAddSingleton keeps host-side registrations winning when they
        // run before AddModelLibrary (e.g. via Replace), or when the host
        // registers after AddModelLibrary using AddSingleton (last-wins).
        services.TryAddSingleton<IModelInstaller>(NullModelInstaller.Instance);

        // Engine-managed Python toolchain. Singleton because uv + Python
        // + venvs are process-wide on-disk state; ModelDownloadService
        // calls into this whenever a kind="python" catalog entry
        // installs. TryAddSingleton so a host that's already registered
        // a configured manager (with a SignalR-backed reporter,
        // typically) keeps its own.
        services.TryAddSingleton<IPythonEnvironmentManager, PythonEnvironmentManager>();

        services.AddSingleton<IModelDownloadService, ModelDownloadService>();
        return services;
    }
}
