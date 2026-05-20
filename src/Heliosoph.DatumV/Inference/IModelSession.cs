namespace Heliosoph.DatumV.Inference;

/// <summary>
/// One loaded model bound to one device, viewed as a generic resolvable
/// resource — no tensor-I/O surface. Created by
/// <see cref="IInferenceBackend.LoadAsync"/>, managed by
/// <see cref="IInferenceDispatcher"/>, disposed by the residency manager
/// under memory pressure (and transparently reloaded on the next call).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Backend-neutral handle.</strong> This is what the dispatcher
/// and procedural model bodies see — an opaque, disposable, device-bound
/// resource with enough metadata for residency accounting. Backends that
/// run tensor graphs (ONNX Runtime, OpenVINO) implement the richer
/// <see cref="IInferenceSession"/> derived contract; backends with other
/// dispatch shapes (LLM token streamers, future RPC clients) implement
/// only <see cref="IModelSession"/> and expose their dispatch surface
/// through their own scalar entry points.
/// </para>
/// <para>
/// <strong>Why the split.</strong> The original
/// <see cref="IInferenceSession"/> conflated handle identity with tensor
/// dispatch. Separating the two lets the engine carry LlamaSharp /
/// llama.cpp sessions through the same lazy resolver + residency
/// pipeline that ORT sessions use, while keeping ORT-specific scalars
/// (<c>infer</c>, <c>decode_decoder_only</c>, …) statically typed
/// against the tensor surface they actually need.
/// </para>
/// </remarks>
public interface IModelSession : IDisposable
{
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
}
