namespace DatumIngest.Inference;

/// <summary>
/// One loaded model bound to one device, ready to accept inference calls.
/// Created by <see cref="IInferenceBackend.LoadAsync"/>, managed by
/// <see cref="IInferenceDispatcher"/>, disposed by the residency manager
/// under memory pressure (and transparently reloaded on the next call).
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
public interface IInferenceSession : IDisposable
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

    /// <summary>The backend that produced this session.</summary>
    InferenceBackendId Backend { get; }

    /// <summary>The concrete device this session is running on.</summary>
    InferenceDevice Device { get; }

    /// <summary>
    /// Reasonable estimate of how much memory this session is keeping
    /// resident (weights + activation arenas + EP-internal buffers).
    /// Used by the residency manager to budget VRAM/RAM at load time and
    /// pick eviction candidates under pressure. Implementations should
    /// err on the high side — under-estimating triggers OOMs.
    /// </summary>
    long EstimatedResidentBytes { get; }

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
