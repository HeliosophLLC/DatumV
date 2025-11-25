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
    /// <strong>ValueRef inputs.</strong> Inputs and overrides arrive as
    /// <see cref="ValueRef"/>s — already-resolved managed payloads (strings,
    /// byte arrays, inline numerics). The evaluator handled all arena and
    /// sidecar resolution at the operator boundary, so implementations call
    /// <c>value.AsString()</c> / <c>value.AsBytes()</c> / <c>value.ToFloat()</c>
    /// directly without threading a store or sidecar registry. The function
    /// chain feeding the model never wrote its output to the arena.
    /// </para>
    /// <para>
    /// <strong>DataValue outputs.</strong> The result list is still
    /// <see cref="DataValue"/> — that's the shape the pipeline emits. The
    /// implementation materialises non-inline result payloads (strings,
    /// vectors, byte arrays, structs, arrays) into <paramref name="targetStore"/>
    /// so they survive past the call. Migrating outputs to <see cref="ValueRef"/>
    /// is a follow-up; held for after the <c>DataKind.Array</c> elimination
    /// settles.
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
    /// <param name="targetStore">
    /// Where the implementation should materialise non-inline result payloads
    /// (strings, vectors, byte arrays, struct/array DataValues). The pipeline
    /// runtime supplies the output batch's arena so results land where the
    /// caller expects to read them from.
    /// </param>
    /// <param name="cancellationToken">Honoured between sub-batches at minimum.</param>
    /// <returns>One <see cref="DataValue"/> per input row, in the same order.</returns>
    Task<IReadOnlyList<DataValue>> InferBatchAsync(
        IReadOnlyList<IReadOnlyList<ValueRef>> inputs,
        IReadOnlyList<IReadOnlyList<ValueRef>> overrides,
        IValueStore targetStore,
        CancellationToken cancellationToken);
}
