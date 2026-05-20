namespace Heliosoph.DatumV.Inference;

/// <summary>
/// One loaded model bound to one device, ready to accept tensor-graph
/// inference calls. The ONNX-style dispatch surface — adds the
/// <see cref="Inputs"/> / <see cref="Outputs"/> tensor signature and the
/// <see cref="RunAsync"/> entry point on top of the generic
/// <see cref="IModelSession"/> handle. Backends with non-tensor dispatch
/// shapes (LlamaSharp, future RPC clients) implement only
/// <see cref="IModelSession"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Stateless across calls.</strong> A session is a pure function
/// from input tensors to output tensors plus the implicit graph weights.
/// Stateful generation patterns (LLM KV cache, streaming decoders) layer
/// over multiple session calls, not session-internal mutation — same shape
/// as ONNX Runtime's session-per-graph model.
/// </para>
/// <para>
/// <strong>Thread safety.</strong> A single session can be called
/// concurrently from multiple threads on backends that support it (ORT
/// does; OpenVINO does on most devices). Whether to parallelise is a
/// caller decision — the engine's model-invocation operator currently
/// serialises per-session to keep VRAM-pressure semantics predictable.
/// </para>
/// </remarks>
public interface IInferenceSession : IModelSession
{
    /// <summary>
    /// Input signature of the underlying model graph. Read from the loaded
    /// graph at <see cref="IInferenceBackend.LoadAsync"/> time; the engine
    /// validates <see cref="TensorBag"/> contents against this list before
    /// dispatching.
    /// </summary>
    IReadOnlyList<TensorSpec> Inputs { get; }

    /// <summary>Output signature of the underlying model graph.</summary>
    IReadOnlyList<TensorSpec> Outputs { get; }

    /// <summary>
    /// Constructs an input <see cref="TensorBag"/> sized for this session's
    /// input signature. The bag uses the backend's allocator so adding
    /// tensors produces zero-copy native buffers where possible.
    /// </summary>
    TensorBag CreateInputBag();

    /// <summary>
    /// Runs inference. <paramref name="inputs"/> must satisfy every entry
    /// in <see cref="Inputs"/>; missing or shape-mismatched tensors throw
    /// before dispatch. The returned bag's tensors are valid until the bag
    /// is disposed.
    /// </summary>
    ValueTask<TensorBag> RunAsync(TensorBag inputs, CancellationToken cancellationToken);
}
