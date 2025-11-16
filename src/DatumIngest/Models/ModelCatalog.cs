using System.Collections.Concurrent;

namespace DatumIngest.Models;

/// <summary>
/// Process-scoped registry of <see cref="ModelCatalogEntry"/> records and the
/// <see cref="IModel"/> instances they produce. Lives outside <c>ExecutionContext</c>
/// because models are server-wide resources: a loaded ONNX model is amortised
/// across queries, sessions, and tenants. Per-query state (memory budget, query
/// meter, spill arenas) belongs to the context; model residency does not.
/// </summary>
/// <remarks>
/// <para>
/// Demo 0.5 uses a "load on first use, hold forever" policy: <see cref="GetModel"/>
/// memoises the result of the entry's loader. Real eviction (LRU, VRAM-bounded
/// admission control) lives in a future <c>ModelResidencyManager</c> behind the
/// same interface — callers don't change.
/// </para>
/// <para>
/// Lookup is namespaced by the SQL surface: <c>models.classify</c> resolves to
/// the entry whose <see cref="ModelCatalogEntry.Name"/> equals <c>"classify"</c>.
/// The leading <c>"models."</c> qualifier is stripped by the planner before lookup.
/// </para>
/// </remarks>
public sealed class ModelCatalog
{
    /// <summary>Default model directory when none is configured.</summary>
    /// <remarks>
    /// User-specific default per <c>project_inference_integration_approach.md</c>.
    /// Production deployments configure this per-database; tests default to a
    /// temp path so they don't touch the user's models directory.
    /// </remarks>
    public const string DefaultModelDirectory = @"E:\models";

    private readonly ConcurrentDictionary<string, ModelCatalogEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, IModel> _loaded =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Absolute path to the directory holding model files. Resolved at construction;
    /// each entry's <see cref="ModelCatalogEntry.RelativePath"/> is combined with this
    /// at load time.
    /// </summary>
    public string ModelDirectory { get; }

    /// <summary>Creates a catalog rooted at <paramref name="modelDirectory"/>.</summary>
    /// <param name="modelDirectory">
    /// Absolute path to the models directory. <see langword="null"/> uses
    /// <see cref="DefaultModelDirectory"/>.
    /// </param>
    public ModelCatalog(string? modelDirectory = null)
    {
        ModelDirectory = modelDirectory ?? DefaultModelDirectory;
    }

    /// <summary>
    /// Registers <paramref name="entry"/>. Throws when an entry with the same name
    /// is already registered — replacement requires explicit
    /// <see cref="Unregister"/> first to avoid silent shadowing.
    /// </summary>
    public void Register(ModelCatalogEntry entry)
    {
        if (!_entries.TryAdd(entry.Name, entry))
        {
            throw new InvalidOperationException(
                $"Model '{entry.Name}' is already registered. Call Unregister first if replacement is intended.");
        }
    }

    /// <summary>
    /// Removes the entry. Any already-loaded <see cref="IModel"/> instance is
    /// dropped from the cache too — subsequent <see cref="GetModel"/> calls would
    /// re-load. Implementations of <see cref="IModel"/> that hold native
    /// resources should implement <see cref="IDisposable"/>; this method does not
    /// dispose them (the caller may still hold a reference).
    /// </summary>
    public bool Unregister(string name)
    {
        _loaded.TryRemove(name, out _);
        return _entries.TryRemove(name, out _);
    }

    /// <summary>
    /// Returns the entry for <paramref name="name"/> if registered.
    /// </summary>
    public ModelCatalogEntry? TryGetEntry(string name)
        => _entries.TryGetValue(name, out ModelCatalogEntry? entry) ? entry : null;

    /// <summary>
    /// All registered entries, keyed by <see cref="ModelCatalogEntry.Name"/>.
    /// Used by the future <c>sys.models</c> virtual table to project catalog
    /// state into SQL.
    /// </summary>
    public IReadOnlyDictionary<string, ModelCatalogEntry> Entries => _entries;

    /// <summary>
    /// Resolves the loaded <see cref="IModel"/> for <paramref name="name"/>,
    /// loading it on first use. Subsequent calls return the same instance —
    /// "load once, hold forever" for Demo 0.5. Real residency control comes
    /// from a future <c>ModelResidencyManager</c> behind the same call.
    /// </summary>
    /// <exception cref="InvalidOperationException">No entry registered for <paramref name="name"/>.</exception>
    public IModel GetModel(string name)
    {
        return _loaded.GetOrAdd(name, key =>
        {
            ModelCatalogEntry entry = TryGetEntry(key)
                ?? throw new InvalidOperationException(
                    $"No model registered as '{key}'. Register it via ModelCatalog.Register before referencing it from SQL.");

            return entry.Loader(new ModelLoadContext(entry, ModelDirectory));
        });
    }
}
