using DatumIngest.DatumFile.Sidecar;
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
    /// <param name="inputs">Per-row input columns. Outer length = row count; inner length = arity.</param>
    /// <param name="inputStore">
    /// The <see cref="IValueStore"/> against which arena-backed input DataValue
    /// payloads (image bytes, strings, vectors) resolve. Sidecar-backed values
    /// resolve through <paramref name="sidecarRegistry"/> instead.
    /// </param>
    /// <param name="sidecarRegistry">
    /// Registry used to resolve sidecar-backed input values (e.g. <c>.datum-blob</c>
    /// images). <see langword="null"/> when the query has no sidecar sources active.
    /// </param>
    /// <param name="targetStore">
    /// Where the implementation should materialise non-inline result payloads
    /// (strings, vectors, byte arrays). The pipeline runtime supplies the output
    /// batch's arena so results land where the caller expects to read them from.
    /// </param>
    /// <param name="cancellationToken">Honoured between sub-batches at minimum.</param>
    /// <returns>One <see cref="DataValue"/> per input row, in the same order.</returns>
    Task<IReadOnlyList<DataValue>> InferBatchAsync(
        IReadOnlyList<IReadOnlyList<DataValue>> inputs,
        IValueStore inputStore,
        SidecarRegistry? sidecarRegistry,
        IValueStore targetStore,
        CancellationToken cancellationToken);
}
