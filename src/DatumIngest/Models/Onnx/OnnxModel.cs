using DatumIngest.DatumFile.Sidecar;
using DatumIngest.Model;

using Microsoft.ML.OnnxRuntime;

namespace DatumIngest.Models.Onnx;

/// <summary>
/// Abstract <see cref="IModel"/> base for ONNX Runtime backed models. Holds the
/// loaded <see cref="InferenceSession"/> and orchestrates the per-batch dispatch;
/// concrete subclasses (e.g. <see cref="MobileNetV2Model"/>) supply the
/// model-specific preprocessing (DataValue → input tensors) and postprocessing
/// (output tensors → DataValue).
/// </summary>
/// <remarks>
/// <para>
/// The session is loaded once and held for the lifetime of the catalog entry —
/// matching the "load once, hold forever" residency policy for Demo 0.5. Native
/// resources are released through <see cref="IDisposable"/>; the catalog drops
/// loaded models on <see cref="ModelCatalog.Unregister"/> but does not dispose
/// them (the caller may still hold a reference).
/// </para>
/// <para>
/// <strong>Batching</strong> — <see cref="InferBatchAsync"/> packs the whole
/// input slice into a single tensor (with the leading batch dimension equal to
/// <c>inputs.Count</c>) and runs one ONNX dispatch. Sub-batching for VRAM caps
/// is the subclass's responsibility if it ever cares; the operator-level batch
/// size already bounds throughput at <c>context.BatchSize</c>.
/// </para>
/// </remarks>
public abstract class OnnxModel : IModel, IDisposable
{
    /// <summary>The loaded ONNX Runtime session. Disposed by <see cref="Dispose"/>.</summary>
    protected InferenceSession Session { get; }

    /// <summary>The catalog name this model is registered under.</summary>
    public string Name { get; }

    /// <inheritdoc />
    public bool IsDeterministic { get; }

    /// <inheritdoc />
    public IReadOnlyList<DataKind> InputKinds { get; }

    /// <inheritdoc />
    public DataKind OutputKind { get; }

    /// <summary>
    /// Loads the ONNX file at <paramref name="modelFilePath"/> via
    /// <see cref="InferenceSession"/>. Subclasses pass through their declared
    /// signature so the planner can validate calls without instantiating the
    /// full session.
    /// </summary>
    protected OnnxModel(
        string name,
        string modelFilePath,
        IReadOnlyList<DataKind> inputKinds,
        DataKind outputKind,
        bool isDeterministic)
    {
        if (!File.Exists(modelFilePath))
        {
            throw new FileNotFoundException(
                $"ONNX model file not found at '{modelFilePath}'. Confirm the catalog's ModelDirectory and the entry's RelativePath resolve to a real file.",
                modelFilePath);
        }

        Name = name;
        InputKinds = inputKinds;
        OutputKind = outputKind;
        IsDeterministic = isDeterministic;
        Session = new InferenceSession(modelFilePath);
    }

    /// <summary>
    /// Builds the input tensors for a whole row batch. Concrete models pack
    /// per-row <see cref="DataValue"/>s into one tensor per ONNX input, with
    /// the leading dimension equal to <paramref name="rows"/>.<c>Count</c>.
    /// </summary>
    /// <param name="rows">Per-row input columns (row-major: outer = row, inner = column/arity).</param>
    /// <param name="inputStore">
    /// Where arena-backed input payloads (image bytes, strings) resolve. The
    /// operator passes the source batch's arena.
    /// </param>
    /// <param name="sidecarRegistry">
    /// Registry for sidecar-backed inputs (e.g. images stored in
    /// <c>.datum-blob</c> files). <see langword="null"/> when no sidecar sources
    /// are in play.
    /// </param>
    /// <returns>
    /// The list of named tensors fed to <see cref="InferenceSession.Run(IReadOnlyCollection{NamedOnnxValue})"/>.
    /// Names must match the ONNX graph's input names.
    /// </returns>
    protected abstract IReadOnlyCollection<NamedOnnxValue> BuildBatchInputs(
        IReadOnlyList<IReadOnlyList<DataValue>> rows,
        IValueStore inputStore,
        SidecarRegistry? sidecarRegistry);

    /// <summary>
    /// Reads the output tensors and produces one <see cref="DataValue"/> per
    /// row. Implementations should materialise non-inline payloads (strings,
    /// vectors, byte arrays) into <paramref name="targetStore"/> so the
    /// returned values resolve against the operator's output arena.
    /// </summary>
    /// <param name="outputs">The named output tensors returned by ONNX Runtime.</param>
    /// <param name="batchSize">The expected row count (leading dim of every output tensor).</param>
    /// <param name="targetStore">Where to materialise non-inline result payloads.</param>
    /// <returns>One <see cref="DataValue"/> per row, in input order.</returns>
    protected abstract IReadOnlyList<DataValue> ParseBatchOutputs(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs,
        int batchSize,
        IValueStore targetStore);

    /// <inheritdoc />
    public virtual Task<IReadOnlyList<DataValue>> InferBatchAsync(
        IReadOnlyList<IReadOnlyList<DataValue>> inputs,
        IValueStore inputStore,
        SidecarRegistry? sidecarRegistry,
        IValueStore targetStore,
        IReadOnlyList<IReadOnlyList<DataValue>> overrides,
        CancellationToken cancellationToken)
    {
        // The base ONNX session has no per-call hyperparameters in this design.
        // Subclasses that want to expose them (e.g. a future quantised model
        // with a "precision" knob) can override; for now ONNX entries that
        // declare any optional kinds will see this argument ignored.
        _ = overrides;
        cancellationToken.ThrowIfCancellationRequested();

        if (inputs.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<DataValue>>([]);
        }

        // session.Run is synchronous and CPU/GPU bound. Wrap in Task.Run so the
        // operator's async loop doesn't block the calling thread for the whole
        // dispatch — which matters when several queries share a worker.
        return Task.Run<IReadOnlyList<DataValue>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyCollection<NamedOnnxValue> batchInputs = BuildBatchInputs(inputs, inputStore, sidecarRegistry);
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = Session.Run(batchInputs);

            cancellationToken.ThrowIfCancellationRequested();

            return ParseBatchOutputs(outputs, inputs.Count, targetStore);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Session.Dispose();
        GC.SuppressFinalize(this);
    }
}
