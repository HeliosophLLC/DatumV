// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

using DatumIngest.ModelLibrary;
using DatumIngest.Models.Python;
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
        services.AddSingleton<IManifestStore, ManifestStore>();
        services.AddSingleton<ILicenseAcceptanceService, LicenseAcceptanceService>();

        // Each source client gets its own typed HttpClient so handler
        // pools, BaseAddress, and default headers stay isolated.
        services.AddHttpClient<HuggingFaceSourceClient>();
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
