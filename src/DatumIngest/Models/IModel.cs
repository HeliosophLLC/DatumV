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
    /// <see cref="ValueRef.FromStruct"/>, <see cref="ValueRef.FromArray"/>,
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
    /// Optional mini-batch size used by <c>ModelInvocationOperator</c> to
    /// stream results to the user. When non-<see langword="null"/>, the
    /// operator splits incoming <c>RowBatch</c>es into sub-batches of this
    /// size, runs <see cref="InferBatchAsync"/> per sub-batch, and emits
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
}
