// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

namespace DatumIngest.ModelLibrary;

/// <summary>
/// Optional sink that turns a successful download into a registered
/// SQL-defined model. Hosts that run a SQL catalog (the Web host) register
/// a real implementation; tests and CLI consumers get
/// <see cref="NullModelInstaller"/> by default and skip the install step.
/// </summary>
/// <remarks>
/// The <see cref="CatalogModel.InstallSql"/> field is the contract — when
/// it's null the installer is never invoked. When set, it's a path relative
/// to the manifest directory (where catalog.json lives), resolved by the
/// installer implementation through <see cref="IManifestStore.ManifestDirectory"/>.
/// </remarks>
public interface IModelInstaller
{
    /// <summary>
    /// True when the model is already registered in the running catalog —
    /// i.e. its CREATE MODEL statement has been executed and the
    /// SQL-defined model is present in ModelRegistry. Probe-time call;
    /// expected to be a cheap in-memory lookup, not an I/O round trip.
    /// </summary>
    ValueTask<bool> IsInstalledAsync(CatalogModel model, CancellationToken ct);

    /// <summary>
    /// Reads the SQL file referenced by <paramref name="model"/>'s
    /// <see cref="CatalogModel.InstallSql"/> and executes it against the
    /// host's catalog. Implementations are responsible for resolving the
    /// path (relative to manifest dir), parsing, and running each statement
    /// in the file — a single file may contain multiple CREATE MODEL
    /// statements. Throws if execution fails; callers translate to
    /// <see cref="ModelDownloadFailed"/> events.
    /// </summary>
    ValueTask InstallAsync(CatalogModel model, CancellationToken ct);
}

/// <summary>
/// Default <see cref="IModelInstaller"/> for hosts that don't run a SQL
/// catalog. <see cref="IsInstalledAsync"/> always returns true so probes
/// stop at <see cref="ModelInstallState.Downloaded"/> and surface as
/// "installed" to the UI; <see cref="InstallAsync"/> is a no-op.
/// </summary>
public sealed class NullModelInstaller : IModelInstaller
{
    public static NullModelInstaller Instance { get; } = new();
    private NullModelInstaller() { }

    public ValueTask<bool> IsInstalledAsync(CatalogModel model, CancellationToken ct)
        => ValueTask.FromResult(true);

    public ValueTask InstallAsync(CatalogModel model, CancellationToken ct)
        => ValueTask.CompletedTask;
}
