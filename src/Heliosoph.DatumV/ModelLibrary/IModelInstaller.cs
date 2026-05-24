// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

namespace Heliosoph.DatumV.ModelLibrary;

/// <summary>
/// Optional sink that turns a successful download into a registered
/// SQL-defined model. Hosts that run a SQL catalog (the Web host) register
/// a real implementation; tests and CLI consumers get
/// <see cref="NullModelInstaller"/> by default and skip the install step.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="CatalogVersion.InstallSql"/> field is the contract — when
/// it's null the installer is never invoked. When set, it's a path relative
/// to the manifest directory (where catalog.json lives), resolved by the
/// installer implementation through <see cref="IManifestStore.ManifestDirectory"/>.
/// </para>
/// <para>
/// <see cref="InstallAsync"/> returns the identifiers actually observed
/// during install so <see cref="ModelDownloadService"/> can cross-check
/// them against the catalog's declared
/// <see cref="CatalogVersion.Models"/> set. A mismatch (the installSql
/// registered an identifier the JSON didn't declare, or omitted one it
/// did) is fatal at install time — the active pointer reverts and the
/// install fails before SQL clients can take a dependency on a
/// half-registered cut.
/// </para>
/// </remarks>
public interface IModelInstaller
{
    /// <summary>
    /// True when the variant is already registered in the running catalog —
    /// i.e. its CREATE MODEL statement has been executed and the
    /// SQL-defined model is present in ModelRegistry. Probe-time call;
    /// expected to be a cheap in-memory lookup, not an I/O round trip.
    /// </summary>
    ValueTask<bool> IsInstalledAsync(CatalogVariant variant, CancellationToken ct);

    /// <summary>
    /// Reads the SQL file referenced by <paramref name="version"/>'s
    /// <see cref="CatalogVersion.InstallSql"/> and executes it against the
    /// host's catalog, returning the identifiers the installSql actually
    /// registered. Implementations are responsible for resolving the
    /// install-SQL path (relative to manifest dir), parsing, and running
    /// each statement in the file — a single file may contain multiple
    /// CREATE MODEL statements. Throws if execution fails; callers
    /// translate to <see cref="ModelDownloadFailed"/> events.
    /// </summary>
    /// <param name="variant">The catalog variant being installed.</param>
    /// <param name="version">
    /// The version cut to install. The active-install path passes
    /// <c>variant.Versions[0]</c>; the pinned-install path passes the
    /// specific version the caller requested.
    /// </param>
    /// <param name="pinnedMode">
    /// When <see langword="true"/>, the installer rewrites
    /// <c>CREATE [OR REPLACE] MODEL &lt;Identifier&gt;</c> to the
    /// materialised <see cref="CatalogVersionModel.PinnedAs"/> name and
    /// pre-resolves <c>USING</c> paths to absolute <c>file://</c> URIs
    /// rooted at the pinned version's folder.
    /// </param>
    /// <param name="ct">Cancellation token for the install.</param>
    /// <returns>
    /// The identifiers actually observed at registration time (the
    /// rewritten pinned names when <paramref name="pinnedMode"/> is
    /// true, otherwise the authored bare names).
    /// </returns>
    ValueTask<IReadOnlyList<string>> InstallAsync(
        CatalogVariant variant,
        CatalogVersion version,
        bool pinnedMode,
        CancellationToken ct);

    /// <summary>
    /// Drops the supplied registered model identifiers from the host's
    /// catalog. Used by <see cref="ModelDownloadService"/> when the
    /// install-time cross-check finds a declared/observed mismatch.
    /// </summary>
    /// <remarks>
    /// Implementations should treat missing identifiers as no-ops
    /// (idempotent <c>DROP MODEL IF EXISTS</c>). Errors on individual
    /// drops should be swallowed and logged — the caller is already
    /// unwinding a failed install and a follow-on exception would mask
    /// the original mismatch diagnostic.
    /// </remarks>
    ValueTask DropModelsAsync(IReadOnlyList<string> identifiers, CancellationToken ct);
}

/// <summary>
/// Default <see cref="IModelInstaller"/> for hosts that don't run a SQL
/// catalog. <see cref="IsInstalledAsync"/> always returns true so probes
/// stop at <see cref="ModelInstallState.Downloaded"/> and surface as
/// "installed" to the UI; <see cref="InstallAsync"/> is a no-op that
/// returns an empty observed-identifier list.
/// </summary>
public sealed class NullModelInstaller : IModelInstaller
{
    public static NullModelInstaller Instance { get; } = new();
    private NullModelInstaller() { }

    public ValueTask<bool> IsInstalledAsync(CatalogVariant variant, CancellationToken ct)
        => ValueTask.FromResult(true);

    public ValueTask<IReadOnlyList<string>> InstallAsync(
        CatalogVariant variant,
        CatalogVersion version,
        bool pinnedMode,
        CancellationToken ct)
        => ValueTask.FromResult<IReadOnlyList<string>>([]);

    public ValueTask DropModelsAsync(IReadOnlyList<string> identifiers, CancellationToken ct)
        => ValueTask.CompletedTask;
}
