using System.Collections.Concurrent;
using DatumIngest.Inference;

namespace DatumIngest.Catalog.Registries;

/// <summary>
/// Per-model lazy session loader. Wraps the (alias → resolved-path) map
/// declared in a <c>CREATE MODEL ... USING ... AS alias</c> clause and
/// defers the actual session load until the first call (<c>infer('alias',
/// ...)</c>, <c>llama_chat('alias', ...)</c>, …) invokes the alias.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why lazy.</strong> SQL-defined models used to call
/// <see cref="IInferenceDispatcher.LoadBundleAsync"/> at CREATE MODEL
/// registration time, which made catalog rehydration on startup load
/// every installed model's sessions before the host could serve
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
/// <para>
/// <strong>Session type.</strong> Sessions are surfaced as the narrow
/// <see cref="IModelSession"/> handle. Consumers that need the tensor
/// surface (ONNX-style scalars like <c>infer</c>,
/// <c>decode_decoder_only</c>) cast to <see cref="IInferenceSession"/>
/// at the use site; backends with non-tensor dispatch shapes return
/// their own derived interfaces that scalars cast to similarly.
/// </para>
/// </remarks>
public sealed class LazyModelSessions
{
    private readonly IInferenceDispatcher _dispatcher;
    private readonly IReadOnlyDictionary<string, string> _resolvedPaths;
    private readonly string _bundleId;
    private readonly ConcurrentDictionary<string, Lazy<Task<IModelSession>>> _loaders;

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
        _loaders = new ConcurrentDictionary<string, Lazy<Task<IModelSession>>>(StringComparer.Ordinal);
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
        => _loaders.TryGetValue(alias, out Lazy<Task<IModelSession>>? lazy)
            && lazy.IsValueCreated
            && lazy.Value.IsCompletedSuccessfully;

    /// <summary>
    /// Returns the session for <paramref name="alias"/>, loading it on
    /// first access. Concurrent calls for the same alias share a single
    /// in-flight load (any caller may be the loader, the rest await).
    /// Throws when the alias wasn't declared in CREATE MODEL.
    /// </summary>
    public ValueTask<IModelSession> ResolveAsync(string alias, CancellationToken cancellationToken)
    {
        if (!_resolvedPaths.ContainsKey(alias))
        {
            // Distinguish "alias not in the declared set" from "this model
            // has no USING clause at all". The latter is a delegating
            // model whose body produces its result by calling into
            // another model or a UDF — referencing a session alias from
            // its body is almost always an author bug (forgot to add
            // USING, or copied the body from a model that did bind a
            // session).
            if (_resolvedPaths.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Session alias '{alias}' is not declared: this model has no USING clause. " +
                    "Add a USING clause (e.g. `USING 'path/to/model.gguf' AS " + alias + "`) " +
                    "if the body should bind its own sessions, or remove the alias-consuming call " +
                    "if the body should delegate to another model.");
            }
            throw new InvalidOperationException(
                $"Session alias '{alias}' is not declared in the model's USING clause. " +
                $"Available aliases: [{string.Join(", ", _resolvedPaths.Keys)}].");
        }
        Lazy<Task<IModelSession>> lazy = _loaders.GetOrAdd(
            alias,
            a => new Lazy<Task<IModelSession>>(
                () => LoadOneAsync(a, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));
        return new ValueTask<IModelSession>(lazy.Value);
    }

    private async Task<IModelSession> LoadOneAsync(string alias, CancellationToken cancellationToken)
    {
        BundleManifest single = new(
            BundleId: $"{_bundleId}#{alias}",
            Sessions: new Dictionary<string, string>(StringComparer.Ordinal) { [alias] = _resolvedPaths[alias] },
            PreferredBackends: Array.Empty<InferenceBackendId>());
        IReadOnlyDictionary<string, IModelSession> loaded = await _dispatcher
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
        foreach (KeyValuePair<string, Lazy<Task<IModelSession>>> kv in _loaders)
        {
            if (!kv.Value.IsValueCreated) continue;
            Task<IModelSession> task = kv.Value.Value;
            if (!task.IsCompletedSuccessfully) continue;
            try { task.Result.Dispose(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"Failed to dispose session '{kv.Key}' in bundle '{_bundleId}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Disposes every loaded session AND clears the loader cache so the
    /// next <see cref="ResolveAsync"/> on each alias reloads from disk.
    /// Differs from <see cref="DisposeLoaded"/> in that this is intended
    /// for residency-driven re-acquisition: the model gets unloaded from
    /// VRAM, the descriptor and path map stay valid, and a future
    /// invocation transparently reloads. Used by the residency manager
    /// when evicting a SQL-defined model to make room for a sibling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Race with in-flight scalar dispatch.</strong> The
    /// non-MIO scalar fallback path
    /// (<c>ProceduralModelFunction.ExecuteAsync</c>) calls
    /// <see cref="ResolveAsync"/> without holding a residency lease, so
    /// nothing in the residency layer can guarantee it isn't using a
    /// session at the moment <see cref="Reset"/> runs. In practice this
    /// is uncommon — the planner hoists every top-level <c>models.*</c>
    /// call into MIO, which DOES hold a lease, so eviction is gated by
    /// refcount=0. Calls from unhoisted contexts (UDF bodies, etc.) can
    /// race; the failure mode is an <see cref="ObjectDisposedException"/>
    /// from the disposed session. A proper fix needs refcounting on the
    /// scalar path too; tracked separately.
    /// </para>
    /// </remarks>
    public void Reset()
    {
        DisposeLoaded();
        _loaders.Clear();
    }
}
