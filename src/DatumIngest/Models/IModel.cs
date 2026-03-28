using System.Runtime.CompilerServices;

using DatumIngest.Functions;
using DatumIngest.Model;

namespace DatumIngest.Models;

/// <summary>
/// Backend-agnostic model abstraction. The engine never names a runtime
/// (ONNX Runtime, LlamaSharp, OnnxStack, etc.) — every backend implements
/// this interface and registers itself behind a <see cref="ModelCatalogEntry"/>
/// so the SQL surface stays runtime-neutral.
/// </summary>
/// <remarks>
/// <para>
/// Async lives at the model boundary because GPU dispatch and I/O are inherently
/// async. The scalar function infrastructure stays sync — model invocations are
/// hoisted into <c>ModelInvocationOperator</c> at plan time, and the operator's
/// <c>IAsyncEnumerable&lt;RowBatch&gt;</c> contract absorbs the await.
/// </para>
/// <para>
/// <strong>Batching is the contract.</strong> Implementations receive a slice of
/// inputs in one call and are expected to dispatch them as a single GPU batch
/// (or sub-batched if the slice exceeds a per-model batch limit). Per-row
/// invocation through this interface would defeat its purpose.
/// </para>
/// </remarks>
public interface IModel
{
    /// <summary>
    /// Stable identifier for this model in the catalog (e.g. <c>"mobilenet_v2"</c>).
    /// SQL surface keys functions to this name via <c>models.&lt;name&gt;</c>.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Whether this model is deterministic for a given input. <see langword="true"/>
    /// for classifiers, embedders, OCR, perceptual hashing — anything where the same
    /// input always produces the same output. <see langword="false"/> for samplers
    /// (LLMs with temperature, diffusion without a fixed seed). The planner uses this
    /// to decide whether different call sites with equal arguments may share a result
    /// (CSE) — same call site always shares regardless.
    /// </summary>
    bool IsDeterministic { get; }

    /// <summary>
    /// The <see cref="DataKind"/> each input column must have. Length is the model's
    /// input arity. The planner uses this to validate <c>models.mobilenetv2(x, y, z)</c>
    /// argument types at plan time.
    /// </summary>
    IReadOnlyList<DataKind> InputKinds { get; }

    /// <summary>
    /// The <see cref="DataKind"/> this model emits per row. Single-output for
    /// classifiers, embedders, captioners. Multi-output models (bounding boxes +
    /// labels + scores) will eventually need a richer return type — for the
    /// initial integration we stick with one output per invocation.
    /// </summary>
    DataKind OutputKind { get; }

    /// <summary>
    /// For struct-output models, the ordered field descriptors of the output struct.
    /// Returns <see langword="null"/> for non-struct outputs or when the schema is
    /// unspecified. The default implementation returns <see langword="null"/>.
    /// </summary>
    IReadOnlyList<ColumnInfo>? OutputFields => null;

    /// <summary>
    /// Run inference over a slice of inputs. <paramref name="inputs"/> is a row-
    /// major view: <c>inputs[r][c]</c> is column <c>c</c> of row <c>r</c>.
    /// Implementations should dispatch in one or a small number of GPU calls —
    /// never one per row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>ValueRef in, ValueRef out.</strong> Inputs and overrides arrive
    /// as already-resolved managed payloads (strings, byte arrays, inline
    /// numerics) — the evaluator handled arena and sidecar resolution at the
    /// operator boundary. Implementations call <c>value.AsString()</c> /
    /// <c>value.AsBytes()</c> / <c>value.ToFloat()</c> directly without
    /// threading a store or sidecar registry.
    /// </para>
    /// <para>
    /// Outputs are constructed in managed memory via <see cref="ValueRef.FromString"/>,
    /// <see cref="ValueRef.FromStruct(ValueRef[])"/>, <c>ValueRef.FromArray</c>,
    /// etc. — the model never touches an arena. The operator's scatter step
    /// calls <see cref="ValueRef.ToDataValue"/> to materialise into the
    /// output batch's arena in one recursive pass. Nested structures (e.g.
    /// <c>Array&lt;Struct&gt;</c> for object-detector outputs) defer all the
    /// way through; the arena writes happen exactly once at the boundary.
    /// </para>
    /// </remarks>
    /// <param name="inputs">Per-row input columns. Outer length = row count; inner length = arity.</param>
    /// <param name="overrides">
    /// Per-row hyperparameter overrides. Outer length matches
    /// <paramref name="inputs"/>.Count (one entry per row). Each inner list is
    /// in the order declared by the catalog entry's
    /// <see cref="ModelCatalogEntry.OptionalArgKinds"/>; an inner length shorter
    /// than the declared list means trailing parameters fall back to the model's
    /// construction-time defaults for that row. An empty inner list (or empty
    /// outer list) means "use defaults for everything." Implementations that
    /// don't accept any optional args may ignore this parameter.
    /// </param>
    /// <param name="cancellationToken">Honoured between sub-batches at minimum.</param>
    /// <returns>One <see cref="ValueRef"/> per input row, in the same order.</returns>
    Task<IReadOnlyList<ValueRef>> InferBatchAsync(
        IReadOnlyList<IReadOnlyList<ValueRef>> inputs,
        IReadOnlyList<IReadOnlyList<ValueRef>> overrides,
        CancellationToken cancellationToken);

    /// <summary>
    /// Type-registry-aware batch dispatch. Same contract as the 3-arg
    /// <see cref="InferBatchAsync(IReadOnlyList{IReadOnlyList{ValueRef}}, IReadOnlyList{IReadOnlyList{ValueRef}}, CancellationToken)"/>
    /// with an extra <paramref name="types"/> parameter carrying the
    /// caller's per-query <see cref="TypeRegistry"/>. Models that build
    /// struct or typed-array outputs whose shape comes from runtime data
    /// (rather than a static <see cref="OutputFields"/> schema) intern
    /// their result shapes into this registry so the caller's
    /// <c>ToDataValue</c> / struct-field-access paths can resolve the
    /// stamped TypeIds without cross-registry translation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Default: forwards to the 3-arg overload, ignoring
    /// <paramref name="types"/>. Existing model implementations that
    /// don't construct dynamic-shape outputs (the vast majority — every
    /// model whose <see cref="OutputFields"/> is non-null, plus every
    /// model that emits only primitive scalars or primitive arrays) need
    /// no changes. SQL-defined models routed through
    /// <c>ProceduralModelAdapter</c> override this method so struct
    /// shapes built inside the body land in the caller's registry.
    /// </para>
    /// <para>
    /// Callers from inside the engine (MIO, ModelScalarFunction) should
    /// invoke this overload with <c>context.Types</c> / <c>frame.Types</c>
    /// so the registry threading works for all model implementations.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<ValueRef>> InferBatchAsync(
        IReadOnlyList<IReadOnlyList<ValueRef>> inputs,
        IReadOnlyList<IReadOnlyList<ValueRef>> overrides,
        TypeRegistry? types,
        CancellationToken cancellationToken)
        => InferBatchAsync(inputs, overrides, cancellationToken);

    /// <summary>
    /// Run inference for a single row, yielding the result as a stream of
    /// chunks. Each yielded <see cref="ValueRef"/> is a fragment of the same
    /// logical output value — for a string-emitting model, consumers
    /// concatenate chunks to recover the full response.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Streaming is the wire protocol, not a type change.</strong>
    /// SQL semantics never see partial values — every <c>SELECT</c>,
    /// <c>WHERE</c>, <c>GROUP BY</c>, etc. uses <c>InferBatchAsync</c>
    /// (which collects internally for streaming-capable models). Only an
    /// explicit streaming sink — currently <c>CALL &lt;model-call&gt;</c> to
    /// the terminal — consumes chunks incrementally.
    /// </para>
    /// <para>
    /// <strong>Single-row by definition.</strong> Streaming output is
    /// per-row state (an LLM's KV cache); cross-row batching at the
    /// streaming layer doesn't compose. Operators that want batched
    /// throughput stay on <c>InferBatchAsync</c>.
    /// </para>
    /// <para>
    /// <strong>Default implementation.</strong> Models that have no useful
    /// intermediate state (classifiers, vision, image generation) inherit
    /// the default, which simply runs <c>InferBatchAsync</c> for the
    /// single row and yields the one result. Models that do have
    /// intermediate state (LLMs) override and yield as the underlying
    /// runtime produces tokens; their <c>InferBatchAsync</c> in
    /// turn collects over this method so the streaming path is exercised
    /// even on collected calls.
    /// </para>
    /// </remarks>
    /// <param name="rowInputs">Input columns for this row. Length = arity.</param>
    /// <param name="rowOverrides">
    /// Hyperparameter overrides for this row, in the order declared by
    /// <see cref="ModelCatalogEntry.OptionalArgKinds"/>. An empty list means
    /// "use defaults for everything."
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation token. Implementations should check between yielded
    /// chunks at minimum so a Ctrl-C in the shell stops generation
    /// promptly.
    /// </param>
    async IAsyncEnumerable<ValueRef> InferStreamingAsync(
        IReadOnlyList<ValueRef> rowInputs,
        IReadOnlyList<ValueRef> rowOverrides,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Default: collapse to a single-chunk stream by running the batch
        // path for one row. Models without intermediate state (every
        // non-LLM today) get streaming "for free" without any per-backend
        // boilerplate. Streaming-capable models override this method and
        // collect over it from InferBatchAsync.
        IReadOnlyList<IReadOnlyList<ValueRef>> inputs = [rowInputs];
        IReadOnlyList<IReadOnlyList<ValueRef>> overrides = rowOverrides.Count > 0
            ? [rowOverrides]
            : [];

        IReadOnlyList<ValueRef> result = await InferBatchAsync(
            inputs, overrides, cancellationToken).ConfigureAwait(false);

        if (result.Count > 0)
        {
            yield return result[0];
        }
    }

    /// <summary>
    /// Optional mini-batch size used by <c>ModelInvocationOperator</c> to
    /// stream results to the user. When non-<see langword="null"/>, the
    /// operator splits incoming <c>RowBatch</c>es into sub-batches of this
    /// size, runs <c>InferBatchAsync</c> per sub-batch, and emits
    /// one output batch per inference call — so the user sees results
    /// arrive incrementally rather than waiting for the full upstream
    /// batch (typically 1024 rows) to complete.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Cost-tier-aware defaults.</strong>
    /// <list type="bullet">
    ///   <item><description>
    ///     Cheap models (classifiers, detectors, embedders — sub-100ms per
    ///     row): leave <see langword="null"/>. Per-row latency is small
    ///     enough that batch-level emission feels instant; rebatching adds
    ///     overhead without UX gain.
    ///   </description></item>
    ///   <item><description>
    ///     Medium-cost models (captioners, OCR — ~500ms-2s per row):
    ///     return <c>8</c>. First results visible in seconds; total
    ///     throughput nearly identical to non-rebatched.
    ///   </description></item>
    ///   <item><description>
    ///     Expensive models (LLMs — ~2-5s per generation): return <c>4</c>.
    ///     Streaming dominates; user perceives the system as live rather
    ///     than batch-frozen.
    ///   </description></item>
    ///   <item><description>
    ///     Very expensive models (image generation — ~1-2s per image,
    ///     each producing MB-scale output): return <c>1</c>. Each image
    ///     emits as soon as it's done.
    ///   </description></item>
    /// </list>
    /// </para>
    /// <para>
    /// Scan-side batching (typically 1024 rows) is unaffected — that's
    /// throughput-oriented and shouldn't shrink. The rebatching only
    /// happens at model-invocation boundaries where per-row latency is
    /// large enough that streaming matters more than per-call overhead.
    /// </para>
    /// </remarks>
    int? PreferredBatchSize => null;

    /// <summary>
    /// Whether this model's body is amenable to columnar cross-row dispatch
    /// — i.e. running N rows through the body once with each intermediate
    /// value held as a column, instead of looping <c>InferBatchAsync</c>
    /// per row. For SQL-defined models the runtime answer comes from a
    /// straight-line check on the body's statements (DECLARE / SET / RETURN
    /// only, no IF / WHILE / BLOCK). Built-in <see cref="IModel"/>s that
    /// already batch internally inside their own <c>InferBatchAsync</c>
    /// don't need to opt in here — this flag is specifically for the
    /// SQL-defined-body batching path. Default: <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Surfaces on <c>system.models.batchable</c> as a diagnostic for
    /// users asking "if I call this model on N rows, will the engine
    /// dispatch them in one GPU call?" The answer also depends on the
    /// bound ONNX session's input shape (it must have a dynamic leading
    /// dim), but that check stays at the call site since the session is
    /// loaded lazily — this flag covers only the body-shape half.
    /// </remarks>
    bool IsBatchable => false;
}
