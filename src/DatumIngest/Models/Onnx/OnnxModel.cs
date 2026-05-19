using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;

using Microsoft.ML.OnnxRuntime;

namespace Heliosoph.DatumV.Models.Onnx;

/// <summary>
/// Abstract <see cref="IModel"/> base for ONNX Runtime backed models. Holds the
/// loaded <see cref="InferenceSession"/> and orchestrates the per-batch dispatch;
/// concrete subclasses (e.g. <c>Florence2Model</c>) supply the
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

    /// <inheritdoc />
    public virtual IReadOnlyList<ColumnInfo>? OutputFields => null;

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
        // Centralised session creation routes through the factory so
        // every ONNX model tries CUDA first and falls back to CPU
        // consistently. Without this, ONNX models silently run on CPU
        // even when the machine has a capable GPU.
        Session = OnnxSessionFactory.Create(modelFilePath);
    }

    /// <summary>
    /// Builds the input tensors for a whole row batch. Concrete models pack
    /// per-row <see cref="ValueRef"/>s into one tensor per ONNX input, with
    /// the leading dimension equal to <paramref name="rows"/>.<c>Count</c>.
    /// Inputs arrive pre-resolved (arena/sidecar payloads already materialised
    /// as managed strings / byte arrays) — implementations call
    /// <c>value.AsString()</c> / <c>value.AsBytes()</c> directly.
    /// </summary>
    /// <param name="rows">Per-row input columns (row-major: outer = row, inner = column/arity).</param>
    /// <returns>
    /// The list of named tensors fed to <see cref="InferenceSession.Run(IReadOnlyCollection{NamedOnnxValue})"/>.
    /// Names must match the ONNX graph's input names.
    /// </returns>
    protected abstract IReadOnlyCollection<NamedOnnxValue> BuildBatchInputs(
        IReadOnlyList<IReadOnlyList<ValueRef>> rows);

    /// <summary>
    /// Reads the output tensors and produces one <see cref="ValueRef"/> per
    /// row. Implementations construct results in managed memory via
    /// <see cref="ValueRef.FromString"/>, <see cref="ValueRef.FromStruct(ValueRef[])"/>,
    /// <c>ValueRef.FromArray</c>, etc. — the model never touches an
    /// arena. The operator's scatter step calls <see cref="ValueRef.ToDataValue"/>
    /// to materialise into the output batch's arena in one recursive pass.
    /// </summary>
    /// <param name="outputs">The named output tensors returned by ONNX Runtime.</param>
    /// <param name="batchSize">The expected row count (leading dim of every output tensor).</param>
    /// <returns>One <see cref="ValueRef"/> per row, in input order.</returns>
    protected abstract IReadOnlyList<ValueRef> ParseBatchOutputs(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs,
        int batchSize);

    /// <inheritdoc />
    public virtual Task<IReadOnlyList<ValueRef>> InferBatchAsync(
        IReadOnlyList<IReadOnlyList<ValueRef>> inputs,
        IReadOnlyList<IReadOnlyList<ValueRef>> overrides,
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
            return Task.FromResult<IReadOnlyList<ValueRef>>([]);
        }

        // session.Run is synchronous and CPU/GPU bound. Wrap in Task.Run so the
        // operator's async loop doesn't block the calling thread for the whole
        // dispatch — which matters when several queries share a worker.
        return Task.Run<IReadOnlyList<ValueRef>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyCollection<NamedOnnxValue> batchInputs = BuildBatchInputs(inputs);
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = Session.Run(batchInputs);

            cancellationToken.ThrowIfCancellationRequested();

            return ParseBatchOutputs(outputs, inputs.Count);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Session.Dispose();
        GC.SuppressFinalize(this);
    }
}
