#pragma warning disable CS1591 // missing XML comment for publicly visible type or member

using System.Collections.Concurrent;

namespace DatumIngest.ModelLibrary;

/// <summary>
/// Single source of truth for "where does this model's files live on disk?"
/// Every callsite that previously constructed a path of the form
/// <c>&lt;DATUM_MODELS&gt;/&lt;catalog-id&gt;/...</c> by hand routes through this
/// resolver so the catalog substrate's per-version layout
/// (<c>&lt;id&gt;/&lt;version&gt;/...</c> + <c>&lt;id&gt;/active</c> pointer)
/// is owned in one place.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="VersionedModelPathResolver"/> is the production
/// implementation: <see cref="GetModelRoot"/> returns
/// <c>&lt;root&gt;/&lt;id&gt;/&lt;active-version&gt;</c> where the active
/// version is read from <c>&lt;root&gt;/&lt;id&gt;/active</c> (a one-line
/// text file). When no <c>active</c> pointer exists yet (download not
/// started, partial install never activated), the resolver falls back to
/// <c>&lt;root&gt;/&lt;id&gt;</c> — the same shape today's flat layout
/// has — so probe / size estimation against a not-yet-installed entry
/// still resolves to a stable path.
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
    /// lifetime of the resolver — version-switch flips happen
    /// <i>below</i> this root.
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
    /// <see langword="null"/> when the <c>&lt;id&gt;/active</c> pointer
    /// has not been written yet (install never completed, model
    /// uninstalled, or — for <see cref="FlatModelPathResolver"/> — no
    /// version concept at all).
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
    /// — preserves today's "drop the file straight under the models
    /// directory" pattern used by a handful of pre-catalog GGUF
    /// registrations.
    /// </summary>
    string ResolveIdPrefixedPath(string idPrefixedRelativePath, string? versionPin = null);

    /// <summary>
    /// Sets the active version pointer for <paramref name="modelId"/>.
    /// Atomic at the filesystem level — the new pointer is written to a
    /// temporary file in the model's folder and then renamed over the
    /// existing one. Invalidates any cached active-version reads for
    /// the same id.
    /// </summary>
    void SetActiveVersion(string modelId, string version);

    /// <summary>
    /// Clears any in-memory cached active-version reads — used after
    /// the catalog substrate's version-switch uninstall to ensure the
    /// next path resolution sees the freshly-written <c>active</c>
    /// file.
    /// </summary>
    void InvalidateActiveVersionCache(string? modelId = null);
}

/// <summary>
/// Production resolver: paths look like
/// <c>&lt;root&gt;/&lt;id&gt;/&lt;version&gt;/&lt;rest&gt;</c>. Reads the
/// active version from the <c>&lt;root&gt;/&lt;id&gt;/active</c> text
/// file once per id, then caches in memory. Cache invalidation runs
/// through <see cref="InvalidateActiveVersionCache"/> — the only callers
/// that need to clear it are the version-switch (<c>SetActiveVersion</c>
/// already invalidates itself) and uninstall paths.
/// </summary>
internal sealed class VersionedModelPathResolver : IModelPathResolver
{
    /// <summary>
    /// One-line text file living at <c>&lt;root&gt;/&lt;id&gt;/active</c>.
    /// Holds the version string of the currently-active install.
    /// Text file rather than symlink for cross-platform portability
    /// (no Windows symlink permission gnarliness) and to leave room for
    /// future extensions like lock-files / transition state.
    /// </summary>
    public const string ActivePointerFilename = "active";

    public string ModelsRoot { get; }

    // Sentinel string distinguishing "we looked, no active pointer
    // exists" (cached null) from "we haven't looked yet" (absent key).
    private static readonly string Missing = "\0";

    private readonly ConcurrentDictionary<string, string> _activeCache =
        new(StringComparer.OrdinalIgnoreCase);

    public VersionedModelPathResolver(string modelsRoot)
    {
        // Empty allowed (some test fixtures construct the residency
        // manager without caring about a real models directory); null
        // rejected so a forgotten DI wiring fails loudly.
        ArgumentNullException.ThrowIfNull(modelsRoot);
        ModelsRoot = modelsRoot;
    }

    public VersionedModelPathResolver(ModelLibraryOptions options)
        : this(options.ModelsDirectory)
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
        string cached = _activeCache.GetOrAdd(modelId, ReadActivePointer);
        return ReferenceEquals(cached, Missing) ? null : cached;
    }

    private string ReadActivePointer(string modelId)
    {
        string pointer = Path.Combine(ModelsRoot, modelId, ActivePointerFilename);
        if (!File.Exists(pointer)) return Missing;
        try
        {
            string text = File.ReadAllText(pointer).Trim();
            return string.IsNullOrEmpty(text) ? Missing : text;
        }
        catch (IOException)
        {
            // Pointer present but unreadable — treat as missing so the
            // resolver falls back to the version-less folder. The
            // download/install path will rewrite the pointer next time.
            return Missing;
        }
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
            // models root. Matches today's behaviour for a handful of
            // pre-catalog GGUF registrations whose modelFilename is bare.
            return Path.Combine(ModelsRoot, idPrefixedRelativePath);
        }
        string id = idPrefixedRelativePath[..slash];
        string rest = idPrefixedRelativePath[(slash + 1)..];
        return Path.Combine(GetModelRoot(id, versionPin), rest);
    }

    public void SetActiveVersion(string modelId, string version)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        string idFolder = Path.Combine(ModelsRoot, modelId);
        Directory.CreateDirectory(idFolder);
        string pointer = Path.Combine(idFolder, ActivePointerFilename);
        string tempPointer = pointer + ".tmp";

        File.WriteAllText(tempPointer, version);
        // File.Move with overwrite is atomic-on-same-volume on every
        // supported platform; the version-switch's narrow write-lock
        // window is short enough that a torn write is impossible.
        File.Move(tempPointer, pointer, overwrite: true);

        _activeCache[modelId] = version;
    }

    public void InvalidateActiveVersionCache(string? modelId = null)
    {
        if (modelId is null)
        {
            _activeCache.Clear();
        }
        else
        {
            _activeCache.TryRemove(modelId, out _);
        }
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
        // Empty allowed (some test fixtures construct the residency
        // manager without caring about a real models directory); null
        // rejected so a forgotten DI wiring fails loudly.
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

    public void SetActiveVersion(string modelId, string version)
    {
        // No-op for the flat resolver — there's no active pointer.
    }

    public void InvalidateActiveVersionCache(string? modelId = null)
    {
        // No-op for the flat resolver — no cache.
    }
}
