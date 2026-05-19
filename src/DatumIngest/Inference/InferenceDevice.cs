namespace Heliosoph.DatumV.Inference;

/// <summary>
/// Names a concrete inference backend. The dispatcher routes load requests
/// to an <see cref="IInferenceBackend"/> matching the chosen id.
/// </summary>
/// <remarks>
/// The id is split from <see cref="InferenceDevice"/> because one backend
/// can serve multiple devices (ONNX Runtime: CPU + CUDA + DirectML + CoreML)
/// and one device class can be served by multiple backends (Intel iGPU is
/// addressable by both ONNX Runtime via DirectML and OpenVINO directly,
/// which may benchmark differently for the same model).
/// </remarks>
public enum InferenceBackendId
{
    /// <summary>Microsoft.ML.OnnxRuntime — broadest model coverage, most mature.</summary>
    OnnxRuntime,

    /// <summary>Intel OpenVINO — preferred for Intel CPUs / iGPUs / NPUs.</summary>
    OpenVino,

    /// <summary>
    /// LlamaSharp / llama.cpp — GGUF text-generation models. Non-tensor
    /// dispatch shape; sessions produced by this backend implement only
    /// <see cref="IModelSession"/>, not <see cref="IInferenceSession"/>.
    /// </summary>
    LlamaSharp,
}

/// <summary>
/// Specific compute target. A backend exposes the devices it can address
/// via <see cref="IInferenceBackend.AvailableDevices"/>. The dispatcher
/// chooses one per session at load time.
/// </summary>
/// <remarks>
/// Device names are intentionally non-exhaustive — we add entries as new
/// hardware classes become relevant. The choice between (for example)
/// <see cref="OnnxRuntimeDirectMl"/> and <see cref="OpenVinoGpu"/> for the
/// same physical Intel iGPU is a policy decision encoded in the dispatcher,
/// not the device enum.
/// </remarks>
public enum InferenceDevice
{
    /// <summary>Generic CPU via ONNX Runtime. Always available.</summary>
    OnnxRuntimeCpu,

    /// <summary>NVIDIA GPU via ONNX Runtime's CUDA execution provider.</summary>
    OnnxRuntimeCuda,

    /// <summary>Any DirectX 12 GPU via ONNX Runtime's DirectML execution provider (Windows).</summary>
    OnnxRuntimeDirectMl,

    /// <summary>Apple Silicon / Intel Mac via ONNX Runtime's CoreML execution provider.</summary>
    OnnxRuntimeCoreMl,

    /// <summary>Generic CPU via OpenVINO. Always available where OpenVINO loads.</summary>
    OpenVinoCpu,

    /// <summary>Intel integrated or discrete GPU via OpenVINO.</summary>
    OpenVinoGpu,

    /// <summary>
    /// Intel NPU (Core Ultra / Meteor Lake / Lunar Lake / Arrow Lake) via OpenVINO.
    /// Designed for sustained INT8 inference at very low power.
    /// </summary>
    OpenVinoNpu,

    /// <summary>Generic CPU via LlamaSharp / llama.cpp. Always available.</summary>
    LlamaSharpCpu,

    /// <summary>NVIDIA GPU via LlamaSharp / llama.cpp's CUDA backend.</summary>
    LlamaSharpCuda,
}

/// <summary>
/// Hint to the dispatcher's policy layer. Defaults split the choice between
/// throughput, latency, and battery in a sensible way; user settings can
/// override per-model or globally.
/// </summary>
public enum PowerPreference
{
    /// <summary>Pick the highest-throughput device the model fits on.</summary>
    HighPerformance,

    /// <summary>Default. Sane mix; biases toward dedicated accelerators when present.</summary>
    Balanced,

    /// <summary>Prefer devices with low idle power and low active wattage (NPU &gt; iGPU &gt; CPU on Intel; CPU &gt; everything on plug-only systems).</summary>
    LowPower,
}

/// <summary>
/// Hint to the dispatcher's policy layer. Trades sustained throughput for
/// time-to-first-output. Interactive UIs typically want
/// <see cref="LowLatency"/>; batch ETL pipelines typically want
/// <see cref="Throughput"/>.
/// </summary>
public enum LatencyPreference
{
    /// <summary>Default. Reasonable for both interactive and batch.</summary>
    Balanced,

    /// <summary>Optimize first-call latency at potential cost to steady-state throughput.</summary>
    LowLatency,

    /// <summary>Optimize sustained throughput at potential cost to first-call latency.</summary>
    Throughput,
}

/// <summary>
/// Graph optimization aggressiveness to pass to the backend. ONNX Runtime
/// maps these to its <c>GraphOptimizationLevel</c>; OpenVINO maps to its
/// <c>PERFORMANCE_HINT</c> property. The exact mapping is backend-specific
/// but the semantics are common: more aggressive = longer first-load,
/// potentially faster steady-state.
/// </summary>
public enum InferenceOptimization
{
    /// <summary>No graph optimization. Useful for debugging.</summary>
    None,

    /// <summary>Basic optimizations only (constant folding, dead-code elimination). Fast load.</summary>
    Basic,

    /// <summary>Default. Includes layout transforms and operator fusion. Balanced.</summary>
    Standard,

    /// <summary>All optimizations including those that may take significant load time. Best steady-state.</summary>
    Aggressive,
}
