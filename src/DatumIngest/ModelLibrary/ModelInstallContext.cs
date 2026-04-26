namespace DatumIngest.ModelLibrary;

/// <summary>
/// Async-local ambient context the installer sets so a model registration
/// in flight resolves <c>USING '&lt;entryId&gt;/&lt;rest&gt;'</c> paths
/// against a specific catalog version's folder instead of the
/// <c>&lt;id&gt;/active</c> pointer's current value.
/// </summary>
/// <remarks>
/// <para>
/// Why ambient: the chain
/// <c>ModelDownloadService → IModelInstaller → TableCatalog.ExecuteStatementAsync
/// → Routines.ApplyCreateModelAsync → ModelCatalog.ResolveFilePath</c>
/// has no parameter slot to thread an explicit version pin through. An
/// <see cref="System.Threading.AsyncLocal{T}"/> flows through the await
/// chain on the same <c>ExecutionContext</c> and stays out of the way of
/// every call site that doesn't care about install-time resolution.
/// </para>
/// <para>
/// Why active-pointer indirection isn't enough on its own: pre-fix #4,
/// the install path flipped <c>&lt;id&gt;/active</c> to the new version
/// before running installSql so the resolver picked up the new files.
/// In-flight queries that observed the new active pointer cached the new
/// version's paths, then a cross-check revert restored the old pointer —
/// leaving those queries with stale paths. With the ambient pin, active
/// only flips after the install fully succeeds, so in-flight queries
/// observe a consistent pointer throughout.
/// </para>
/// <para>
/// <see cref="ModelDownloadService"/> sets the pin around the
/// <see cref="IModelInstaller.InstallAsync"/> call and clears it (back
/// to the prior value, usually <see langword="null"/>) in a
/// <c>finally</c>. Rehydration on startup never sets the pin — by then
/// the active pointer correctly names the installed version and
/// straight active-pointer indirection resolves USING paths.
/// </para>
/// </remarks>
public static class ModelInstallContext
{
    private static readonly AsyncLocal<string?> _versionPin = new();

    /// <summary>
    /// The catalog version a currently-running install is pinning USING
    /// paths to, or <see langword="null"/> when no install is in flight
    /// on this async flow. Read by <c>ModelCatalog.ResolveFilePath</c> to
    /// override the resolver's default active-pointer lookup.
    /// </summary>
    public static string? CurrentVersionPin
    {
        get => _versionPin.Value;
        set => _versionPin.Value = value;
    }
}
