#pragma warning disable CS1591 // missing XML comment for publicly visible type or member

namespace DatumIngest.ModelLibrary;

/// <summary>
/// Single source of truth for "where does this model's files live on disk?"
/// Every callsite that previously constructed a path of the form
/// <c>&lt;DATUM_MODELS&gt;/&lt;catalog-id&gt;/...</c> by hand routes through this
/// resolver so the catalog substrate's per-version layout
/// (<c>&lt;id&gt;/&lt;version&gt;/...</c>) is owned in one place.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="VersionedModelPathResolver"/> is the production
/// implementation: <see cref="GetModelRoot"/> returns
/// <c>&lt;root&gt;/&lt;id&gt;/&lt;active-version&gt;</c> where the active
/// version comes from the wired <see cref="ICatalogActiveVersionLookup"/>
/// — for the production host that's a query against the live model-registry
/// rows (a catalog row's <c>CatalogVersion</c> with <c>PinnedAs == null</c>
/// is the active version). The <c>ModelInstallContext.CurrentVersionPin</c>
/// AsyncLocal can override the lookup for the duration of an install /
/// rehydrate, when the catalog row doesn't exist yet or names a different
/// version than the one being staged.
/// </para>
/// <para>
/// When no active version is known (no install yet, no lookup wired), the
/// resolver falls back to the version-less folder <c>&lt;root&gt;/&lt;id&gt;</c>
/// — the same shape the pre-catalog flat layout had — so probe / size
/// estimation against a not-yet-installed entry still resolves to a
/// stable path.
/// </para>
/// <para>
/// <see cref="FlatModelPathResolver"/> is the legacy flat-layout
/// implementation, retained for the handful of test fixtures and
/// fall-through paths that need a version-free resolver.
/// </para>
/// </remarks>
public interface IModelPathResolver
{
    /// <summary>
    /// Absolute path to the top-level models directory
    /// (<c>$DATUM_MODELS</c> or the per-user default). Stable for the
    /// lifetime of the resolver — version flips happen <i>below</i>
    /// this root.
    /// </summary>
    string ModelsRoot { get; }

    /// <summary>
    /// Absolute path to the folder containing <paramref name="modelId"/>'s
    /// files. Per-version layout:
    /// <c>&lt;root&gt;/&lt;id&gt;/&lt;active-version&gt;</c> (or
    /// <c>&lt;root&gt;/&lt;id&gt;/&lt;versionPin&gt;</c> when
    /// <paramref name="versionPin"/> is supplied). Falls back to the
    /// version-less folder when no active version is known.
    /// </summary>
    string GetModelRoot(string modelId, string? versionPin = null);

    /// <summary>
    /// Currently active version for <paramref name="modelId"/>, or
    /// <see langword="null"/> when no bare-form install exists yet. The
    /// resolver consults <see cref="ModelInstallContext.CurrentVersionPin"/>
    /// first so installs / rehydrates pin to the version they're staging,
    /// then the wired <see cref="ICatalogActiveVersionLookup"/>.
    /// <see cref="FlatModelPathResolver"/> always returns null.
    /// </summary>
    string? GetActiveVersion(string modelId);

    /// <summary>
    /// Resolve a path that is conceptually "inside" a model's folder.
    /// Equivalent to <c>Path.Combine(GetModelRoot(modelId, versionPin), relativeToModelRoot)</c>.
    /// </summary>
    string ResolveRelative(string modelId, string relativeToModelRoot, string? versionPin = null);

    /// <summary>
    /// Convenience overload for legacy call sites whose
    /// <c>relativePath</c> string already starts with the catalog id
    /// segment (e.g. <c>"llama-3.1-8b-instruct-gguf/Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf"</c>).
    /// Splits the first path segment, treats it as the model id, and
    /// resolves the rest as a model-rooted relative path. Paths with no
    /// separator fall through to <c>Path.Combine(ModelsRoot, path)</c>
    /// — preserves the "drop the file straight under the models
    /// directory" pattern used by a handful of pre-catalog GGUF
    /// registrations.
    /// </summary>
    string ResolveIdPrefixedPath(string idPrefixedRelativePath, string? versionPin = null);

    /// <summary>
    /// True when <c>&lt;root&gt;/&lt;modelId&gt;/&lt;version&gt;/</c> exists
    /// on disk. Used by pre-flight (to decide whether a pinned reference
    /// like <c>models.foo@20260529</c> is downloaded). The flat resolver
    /// returns <see langword="false"/> for any version argument because
    /// it has no version concept.
    /// </summary>
    bool IsVersionOnDisk(string modelId, string version);
}

/// <summary>
/// Production resolver: paths look like
/// <c>&lt;root&gt;/&lt;id&gt;/&lt;version&gt;/&lt;rest&gt;</c>. The active
/// version is resolved through <see cref="ICatalogActiveVersionLookup"/>
/// (typically a query against the live catalog rows in
/// <see cref="DatumIngest.Catalog.TableCatalog"/>) with an
/// <see cref="ModelInstallContext.CurrentVersionPin"/> override for
/// in-flight installs / rehydrates.
/// </summary>
internal sealed class VersionedModelPathResolver : IModelPathResolver
{
    public string ModelsRoot { get; }

    private readonly ICatalogActiveVersionLookup _lookup;

    public VersionedModelPathResolver(string modelsRoot)
        : this(modelsRoot, NullCatalogActiveVersionLookup.Instance)
    {
    }

    public VersionedModelPathResolver(string modelsRoot, ICatalogActiveVersionLookup lookup)
    {
        // Empty allowed (some test fixtures construct the residency
        // manager without caring about a real models directory); null
        // rejected so a forgotten DI wiring fails loudly.
        ArgumentNullException.ThrowIfNull(modelsRoot);
        ArgumentNullException.ThrowIfNull(lookup);
        ModelsRoot = modelsRoot;
        _lookup = lookup;
    }

    public VersionedModelPathResolver(ModelLibraryOptions options)
        : this(options.ModelsDirectory, NullCatalogActiveVersionLookup.Instance)
    {
    }

    public VersionedModelPathResolver(ModelLibraryOptions options, ICatalogActiveVersionLookup lookup)
        : this(options.ModelsDirectory, lookup)
    {
    }

    public string GetModelRoot(string modelId, string? versionPin = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        string idFolder = Path.Combine(ModelsRoot, modelId);
        string? version = versionPin ?? GetActiveVersion(modelId);
        return version is null ? idFolder : Path.Combine(idFolder, version);
    }

    public string? GetActiveVersion(string modelId)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        // CurrentVersionPin wins — set by ModelDownloadService /
        // CatalogBackedModelInstaller during install and by
        // TableCatalog.RehydrateModelsAsync during rehydrate so paths
        // resolve against the version being staged before the catalog
        // row exists. Outside those flows it's null and the lookup
        // answers.
        return ModelInstallContext.CurrentVersionPin ?? _lookup.GetActiveVersion(modelId);
    }

    public string ResolveRelative(string modelId, string relativeToModelRoot, string? versionPin = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        ArgumentNullException.ThrowIfNull(relativeToModelRoot);
        return Path.Combine(GetModelRoot(modelId, versionPin), relativeToModelRoot);
    }

    public string ResolveIdPrefixedPath(string idPrefixedRelativePath, string? versionPin = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(idPrefixedRelativePath);
        int slash = idPrefixedRelativePath.AsSpan().IndexOfAny('/', '\\');
        if (slash < 0)
        {
            // No leading id segment — drop the file directly under the
            // models root. Matches behaviour for a handful of pre-catalog
            // GGUF registrations whose modelFilename is bare.
            return Path.Combine(ModelsRoot, idPrefixedRelativePath);
        }
        string id = idPrefixedRelativePath[..slash];
        string rest = idPrefixedRelativePath[(slash + 1)..];
        return Path.Combine(GetModelRoot(id, versionPin), rest);
    }

    public bool IsVersionOnDisk(string modelId, string version)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        return Directory.Exists(Path.Combine(ModelsRoot, modelId, version));
    }
}

/// <summary>
/// Legacy flat-layout resolver: paths look like
/// <c>&lt;root&gt;/&lt;id&gt;/&lt;rest&gt;</c>, no version concept.
/// Retained as a fallback for test fixtures that constructed a load
/// context without a resolver — most production code paths flow
/// through <see cref="VersionedModelPathResolver"/>.
/// </summary>
internal sealed class FlatModelPathResolver : IModelPathResolver
{
    public string ModelsRoot { get; }

    public FlatModelPathResolver(string modelsRoot)
    {
        ArgumentNullException.ThrowIfNull(modelsRoot);
        ModelsRoot = modelsRoot;
    }

    public FlatModelPathResolver(ModelLibraryOptions options)
        : this(options.ModelsDirectory)
    {
    }

    public string GetModelRoot(string modelId, string? versionPin = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        return Path.Combine(ModelsRoot, modelId);
    }

    public string? GetActiveVersion(string modelId) => null;

    public string ResolveRelative(string modelId, string relativeToModelRoot, string? versionPin = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        ArgumentNullException.ThrowIfNull(relativeToModelRoot);
        return Path.Combine(ModelsRoot, modelId, relativeToModelRoot);
    }

    public string ResolveIdPrefixedPath(string idPrefixedRelativePath, string? versionPin = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(idPrefixedRelativePath);
        int slash = idPrefixedRelativePath.AsSpan().IndexOfAny('/', '\\');
        if (slash < 0)
        {
            return Path.Combine(ModelsRoot, idPrefixedRelativePath);
        }
        string id = idPrefixedRelativePath[..slash];
        string rest = idPrefixedRelativePath[(slash + 1)..];
        return Path.Combine(ModelsRoot, id, rest);
    }

    public bool IsVersionOnDisk(string modelId, string version) => false;
}
