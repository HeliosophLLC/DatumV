using System.Collections.Concurrent;
using DatumIngest.Inference;

namespace DatumIngest.Catalog.Registries;

/// <summary>
/// Per-model lazy session loader. Wraps the (alias → resolved-path) map
/// declared in a <c>CREATE MODEL ... USING ... AS alias</c> clause and
/// defers the actual ONNX session load until the first <c>infer('alias',
/// ...)</c> call invokes the alias.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why lazy.</strong> SQL-defined models used to call
/// <see cref="IInferenceDispatcher.LoadBundleAsync"/> at CREATE MODEL
/// registration time, which made catalog rehydration on startup load
/// every installed model's ONNX sessions before the host could serve
/// requests. With a few dozen SQL-defined models the boot cost ran into
/// minutes; this class makes session load O(invocations) instead of
/// O(installed models).
/// </para>
/// <para>
/// <strong>Concurrency.</strong> Each alias maps to a <c>Lazy&lt;Task&gt;</c>
/// — the first call wins the load, every concurrent caller awaits the
/// same task. <see cref="ResolveAsync"/> is safe to call from any thread
/// without external synchronization.
/// </para>
/// <para>
/// <strong>Disposal.</strong> <see cref="DisposeLoaded"/> walks only the
/// aliases that actually finished loading — entries that were never
/// invoked carry no native resources, so skipping them is both correct
/// and faster on shutdown.
/// </para>
/// </remarks>
public sealed class LazyModelSessions
{
    private readonly IInferenceDispatcher _dispatcher;
    private readonly IReadOnlyDictionary<string, string> _resolvedPaths;
    private readonly string _bundleId;
    private readonly ConcurrentDictionary<string, Lazy<Task<IInferenceSession>>> _loaders;

    /// <summary>
    /// Builds a lazy session bundle. <paramref name="resolvedPaths"/> must
    /// already be absolute filesystem paths (the registrar resolves them
    /// at CREATE MODEL time so a broken path fails before the descriptor
    /// gets registered).
    /// </summary>
    public LazyModelSessions(
        IInferenceDispatcher dispatcher,
        IReadOnlyDictionary<string, string> resolvedPaths,
        string bundleId)
    {
        _dispatcher = dispatcher;
        _resolvedPaths = resolvedPaths;
        _bundleId = bundleId;
        _loaders = new ConcurrentDictionary<string, Lazy<Task<IInferenceSession>>>(StringComparer.Ordinal);
    }

    /// <summary>All declared session aliases. Available without triggering loads.</summary>
    public IReadOnlyCollection<string> Keys => (IReadOnlyCollection<string>)_resolvedPaths.Keys;

    /// <summary>Resolved absolute filesystem path for <paramref name="alias"/>, or null if the alias isn't declared.</summary>
    public string? ResolvedPathFor(string alias)
        => _resolvedPaths.TryGetValue(alias, out string? path) ? path : null;

    /// <summary>True when <paramref name="alias"/> was declared in CREATE MODEL ... AS.</summary>
    public bool ContainsKey(string alias) => _resolvedPaths.ContainsKey(alias);

    /// <summary>True when <paramref name="alias"/> has been loaded and is in cache.</summary>
    public bool IsLoaded(string alias)
        => _loaders.TryGetValue(alias, out Lazy<Task<IInferenceSession>>? lazy)
            && lazy.IsValueCreated
            && lazy.Value.IsCompletedSuccessfully;

    /// <summary>
    /// Returns the session for <paramref name="alias"/>, loading it on
    /// first access. Concurrent calls for the same alias share a single
    /// in-flight load (any caller may be the loader, the rest await).
    /// Throws when the alias wasn't declared in CREATE MODEL.
    /// </summary>
    public ValueTask<IInferenceSession> ResolveAsync(string alias, CancellationToken cancellationToken)
    {
        if (!_resolvedPaths.ContainsKey(alias))
        {
            throw new InvalidOperationException(
                $"Session alias '{alias}' is not declared in the model's USING clause. " +
                $"Available aliases: [{string.Join(", ", _resolvedPaths.Keys)}].");
        }
        Lazy<Task<IInferenceSession>> lazy = _loaders.GetOrAdd(
            alias,
            a => new Lazy<Task<IInferenceSession>>(
                () => LoadOneAsync(a, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));
        return new ValueTask<IInferenceSession>(lazy.Value);
    }

    private async Task<IInferenceSession> LoadOneAsync(string alias, CancellationToken cancellationToken)
    {
        BundleManifest single = new(
            BundleId: $"{_bundleId}#{alias}",
            Sessions: new Dictionary<string, string>(StringComparer.Ordinal) { [alias] = _resolvedPaths[alias] },
            PreferredBackends: Array.Empty<InferenceBackendId>());
        IReadOnlyDictionary<string, IInferenceSession> loaded = await _dispatcher
            .LoadBundleAsync(single, new InferencePreferences(), cancellationToken)
            .ConfigureAwait(false);
        return loaded[alias];
    }

    /// <summary>
    /// Disposes only the sessions that finished loading. Aliases that were
    /// never invoked carry no native handles, so skipping them is correct
    /// and avoids paying load cost on the disposal path.
    /// </summary>
    public void DisposeLoaded()
    {
        foreach (KeyValuePair<string, Lazy<Task<IInferenceSession>>> kv in _loaders)
        {
            if (!kv.Value.IsValueCreated) continue;
            Task<IInferenceSession> task = kv.Value.Value;
            if (!task.IsCompletedSuccessfully) continue;
            try { task.Result.Dispose(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"Failed to dispose session '{kv.Key}' in bundle '{_bundleId}': {ex.Message}");
            }
        }
    }
}
