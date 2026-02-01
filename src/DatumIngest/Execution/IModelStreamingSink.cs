using DatumIngest.Functions;

namespace DatumIngest.Execution;

/// <summary>
/// Optional hook for consuming <c>models.X(...)</c> output incrementally. When
/// attached to an <see cref="ExecutionContext"/>, <see cref="Operators.ModelInvocationOperator"/>
/// switches the active model from its batched <c>InferBatchAsync</c> path to
/// the per-row <c>InferStreamingAsync</c> path and forwards every yielded
/// chunk to the sink as it arrives.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Streaming is the wire protocol, not a SQL semantic.</strong> The
/// sink is invoked exclusively from sinks that opted into incremental
/// delivery — currently <c>CALL &lt;model-call&gt;</c> in the interactive
/// shell. Plain <c>SELECT</c>/<c>WHERE</c>/<c>GROUP BY</c> never see this
/// hook because they need the full collected value; the operator only
/// branches into the streaming path when <see cref="ExecutionContext.StreamingSink"/>
/// is non-<see langword="null"/>.
/// </para>
/// <para>
/// <strong>Lifetime.</strong> A sink is per-query (set via the streaming
/// overload of <c>IQueryPlan.ExecuteAsync</c>) and observes every model
/// dispatch in that query, in arrival order. <see cref="OnChunkAsync"/> may be
/// invoked many times per dispatch; <see cref="OnCompletedAsync"/> fires exactly
/// once per dispatch on success, <see cref="OnFailedAsync"/> exactly once on
/// throw.
/// </para>
/// <para>
/// <strong>ValueRef payloads are managed.</strong> Models construct chunks
/// in managed memory (<see cref="ValueRef.FromString"/>); the sink may call
/// <see cref="ValueRef.AsString"/> / <see cref="ValueRef.AsBytes"/> directly
/// without arena coordination. Chunks do not outlive the
/// <see cref="OnChunkAsync"/> call — copy out anything the sink needs to keep.
/// </para>
/// <para>
/// <strong>Sinks should not throw.</strong> Exceptions from the sink
/// propagate out of the operator and fail the query. Mid-line console output
/// can leave the terminal in a partial state on throw; sinks that print
/// directly should swallow display errors and keep going.
/// </para>
/// </remarks>
public interface IModelStreamingSink
{
    /// <summary>
    /// Called once per chunk yielded by <see cref="Models.IModel.InferStreamingAsync"/>,
    /// in arrival order. For string-emitting models, concatenating all chunks
    /// from one dispatch reproduces the value the corresponding
    /// <c>InferBatchAsync</c> call would return (modulo trailing whitespace
    /// trimming, which the batched path applies and the streaming path does
    /// not).
    /// </summary>
    /// <param name="modelName">Catalog-visible model name (e.g. <c>"llama31_8b"</c>).</param>
    /// <param name="chunk">A single chunk of the dispatch's output.</param>
    ValueTask OnChunkAsync(string modelName, ValueRef chunk);

    /// <summary>
    /// Called once after the streaming dispatch completes successfully. The
    /// sink can use this to flush a trailing newline, emit a footer, or
    /// release per-dispatch state.
    /// </summary>
    ValueTask OnCompletedAsync(string modelName);

    /// <summary>
    /// Called once if the streaming dispatch throws. The exception
    /// propagates regardless — this hook is purely so the sink can clean
    /// up partial output (e.g. print a newline if the last chunk left the
    /// cursor mid-line). Sinks should not rethrow.
    /// </summary>
    ValueTask OnFailedAsync(string modelName, Exception exception);
}
