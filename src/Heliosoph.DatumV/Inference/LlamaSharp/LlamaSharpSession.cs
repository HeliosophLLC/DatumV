using LLama;
using LLama.Common;

namespace Heliosoph.DatumV.Inference.LlamaSharp;

/// <summary>
/// <see cref="IModelSession"/> implementation wrapping a LlamaSharp
/// <see cref="LLamaWeights"/> + <see cref="StatelessExecutor"/>. Owns the
/// native GGUF model handle for the session's lifetime; disposed by the
/// dispatcher / residency manager when evicted.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Non-tensor surface.</strong> Unlike the ONNX-backed
/// <c>OnnxRuntimeSession</c>, this session does NOT implement
/// <see cref="IInferenceSession"/>. LlamaSharp's dispatch shape (templated
/// prompt → token stream) doesn't fit the tensor-bag model. The
/// LLM-shaped scalars (<c>llama_generate</c>, <c>llama_chat</c>; landing
/// in Slice 3) cast from <see cref="IModelSession"/> to this concrete
/// type at the use site to reach <see cref="Executor"/>.
/// </para>
/// <para>
/// <strong>Context size.</strong> Set at session-load time and immutable
/// thereafter — the KV cache is sized to <see cref="ContextSize"/>. Per-call
/// runtime overrides (max_tokens, temperature) flow through the LLM-shaped
/// scalars, not through the session itself.
/// </para>
/// <para>
/// <strong>Estimated resident bytes.</strong> Approximated as the GGUF file
/// size + a context-proportional KV-cache term. Real residency exceeds the
/// file size by the KV cache, activation buffers, and CUDA-side allocator
/// overhead — the residency manager errs on the high side per the
/// <see cref="IModelSession.EstimatedResidentBytes"/> contract.
/// </para>
/// </remarks>
public sealed class LlamaSharpSession : IModelSession
{
    private bool _disposed;

    internal LlamaSharpSession(
        LLamaWeights weights,
        ModelParams modelParams,
        StatelessExecutor executor,
        InferenceDevice device,
        long estimatedResidentBytes)
    {
        Weights = weights;
        ModelParams = modelParams;
        Executor = executor;
        Device = device;
        EstimatedResidentBytes = estimatedResidentBytes;
    }

    /// <summary>
    /// The loaded GGUF weights. Exposed so the LLM-shaped scalars can
    /// construct a <see cref="LLamaTemplate"/> against the model's
    /// native handle — llama.cpp's <c>llama_chat_apply_template</c>
    /// reads the GGUF's embedded chat template and tokenizes special
    /// tokens by id, which is the only reliable way to get a chat-format
    /// prompt the model actually recognises (hand-rolled template strings
    /// get tokenized as plain text on quants whose metadata is incomplete
    /// for the role markers).
    /// </summary>
    public LLamaWeights Weights { get; }

    /// <inheritdoc />
    public InferenceBackendId Backend => InferenceBackendId.LlamaSharp;

    /// <inheritdoc />
    public InferenceDevice Device { get; }

    /// <inheritdoc />
    public long EstimatedResidentBytes { get; }

    /// <summary>
    /// The configured context window (tokens). The KV cache is sized to
    /// this and cannot be grown without reloading the session.
    /// </summary>
    public uint ContextSize => ModelParams.ContextSize ?? 0;

    /// <summary>
    /// Load-time configuration passed to LlamaSharp. Exposed so the
    /// LLM-shaped scalars can reconstruct <see cref="InferenceParams"/>
    /// against the same template / sampling defaults the session was
    /// loaded with.
    /// </summary>
    public ModelParams ModelParams { get; }

    /// <summary>
    /// The reusable stateless executor. One executor per session; KV
    /// cache resets at every <c>InferAsync</c> call (no chat history
    /// retention across calls). Thread-safety: LlamaSharp's
    /// <see cref="StatelessExecutor"/> serialises per-instance — call
    /// from one row at a time per session.
    /// </summary>
    public StatelessExecutor Executor { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Weights.Dispose();
    }
}
