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
    /// input arity. The planner uses this to validate <c>models.classify(x, y, z)</c>
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
}
