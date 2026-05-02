namespace DatumIngest.ModelLibrary;

/// <summary>
/// Answers "for catalog entry &lt;id&gt;, which version is currently the
/// bare-form active install?" by consulting the live catalog-row state
/// — the in-memory side of <c>.datum-catalog.json</c>'s model entries.
/// Replaces the previous <c>&lt;id&gt;/active</c> text-pointer file: a
/// catalog row's <c>CatalogVersion</c> is the active version exactly
/// when its <c>PinnedAs</c> is null (pinned-form rows coexist with
/// bare ones and intentionally don't drive "active").
/// </summary>
/// <remarks>
/// <para>
/// The lookup is intentionally narrow — one method, no caching, no
/// invalidation. The state behind it is already cached in
/// <see cref="DatumIngest.Catalog.Registries.ModelRegistry"/> (a
/// <c>ConcurrentDictionary</c>) and updates atomically with install /
/// activate / delete; there's no separate cache to keep in sync.
/// </para>
/// <para>
/// Implementations are expected to return <see langword="null"/> for
/// unknown catalog ids and for catalog entries that have only
/// pinned-form rows installed (no bare-form active version exists).
/// </para>
/// </remarks>
public interface ICatalogActiveVersionLookup
{
    /// <summary>
    /// Returns the version string of the currently-active bare-form
    /// install for <paramref name="catalogId"/>, or <see langword="null"/>
    /// when no bare-form row exists. A return value of null means
    /// "nothing to resolve to" — the resolver's callers fall back to the
    /// version-less folder shape (or surface the absence as an install
    /// prompt, depending on context).
    /// </summary>
    string? GetActiveVersion(string catalogId);
}

/// <summary>
/// Lookup that always returns <see langword="null"/>. Wired into hosts
/// that have no catalog (tests, standalone tools) so the resolver's
/// active-version surface stays callable without a real catalog
/// behind it.
/// </summary>
internal sealed class NullCatalogActiveVersionLookup : ICatalogActiveVersionLookup
{
    public static readonly NullCatalogActiveVersionLookup Instance = new();

    private NullCatalogActiveVersionLookup() { }

    public string? GetActiveVersion(string catalogId) => null;
}
