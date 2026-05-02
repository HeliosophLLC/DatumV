namespace DatumIngest.Inference;

/// <summary>
/// One concrete inference runtime — ONNX Runtime, OpenVINO, etc. Backends
/// are registered with the <see cref="IInferenceDispatcher"/> at host
/// startup; the dispatcher's policy layer picks which one handles each
/// load request.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Hardware discovery is lazy.</strong> Backends should not probe
/// their device list at construction; that work is expensive (CUDA driver
/// init, OpenVINO core construction) and many process lifetimes never need
/// it. <see cref="AvailableDevices"/> is allowed to do the discovery on
/// first access and cache.
/// </para>
/// <para>
/// <strong>Backends do not own model files.</strong> The dispatcher passes
/// a fully-resolved path in the <see cref="InferenceLoadRequest"/>. Bundle
/// discovery, download, and license-acceptance gating happen above this
/// layer — by the time <see cref="LoadAsync"/> is called, the bytes are
/// on disk and the user has agreed to the terms.
/// </para>
/// </remarks>
public interface IInferenceBackend
{
    /// <summary>Identity of this backend.</summary>
    InferenceBackendId Id { get; }

    /// <summary>
    /// Devices this backend can dispatch to on the current machine. May
    /// trigger driver / runtime probing on first access; subsequent
    /// accesses return cached results. An empty list means the backend
    /// loaded but has no usable hardware (e.g. ONNX Runtime built without
    /// the CUDA provider on a machine with only an NVIDIA GPU).
    /// </summary>
    IReadOnlyList<InferenceDevice> AvailableDevices { get; }

    /// <summary>
    /// Full probe picture: one entry per device kind this backend recognises,
    /// regardless of whether it actually loaded on this machine. The
    /// <see cref="DeviceProbeResult.Reason"/> on unavailable entries explains
    /// why (platform mismatch, missing driver, EP not built). Powers the
    /// <c>inference.devices()</c> TVF so users see "DirectML — unavailable,
    /// Linux not supported" instead of a silently-truncated list.
    /// </summary>
    /// <remarks>
    /// Backends should cache the probe results between calls — the
    /// expectation is one probe pass per process. <see cref="AvailableDevices"/>
    /// is naturally derivable from this list (filter where
    /// <see cref="DeviceProbeResult.Available"/>) but kept as a separate
    /// property so the hot dispatch path doesn't allocate.
    /// </remarks>
    IReadOnlyList<DeviceProbeResult> ProbeAllDevices();

    /// <summary>
    /// Highest <c>ai.onnx</c> opset version this backend's underlying
    /// runtime can consume. Compared by the
    /// <c>inference.infer_compatibility()</c> introspection TVF against
    /// the opset declared in the candidate ONNX file so users can answer
    /// "is this model too new for what I have installed?" without an
    /// actual load attempt.
    /// </summary>
    /// <remarks>
    /// Hardcoded per backend implementation — ORT and OpenVINO don't expose
    /// the value through their C# bindings. Bump when bumping the
    /// underlying runtime version. The OnnxRuntime backend tracks the
    /// ai.onnx opset ceiling specifically (custom-domain opsets like
    /// com.microsoft have their own version space that's not compared).
    /// </remarks>
    int MaxSupportedOpset { get; }

    /// <summary>
    /// Pre-load check: can this backend load the bundle described by
    /// <paramref name="bundle"/> on any of its available devices? Allows
    /// the dispatcher to rank candidates and skip backends that would
    /// reject the bundle at load time anyway.
    /// </summary>
    /// <param name="bundle">
    /// Bundle-level metadata the backend can consult — preferred-backend
    /// list, opset version, required ops, expected element types. Backends
    /// that don't care about the bundle metadata may return
    /// <see cref="BackendCompatibility.Supported"/> unconditionally; the
    /// real failure surfaces at load time.
    /// </param>
    BackendCompatibility Inspect(BundleManifest bundle);

    /// <summary>
    /// Construct a session for one model file on the requested device.
    /// Throws if the device is unsupported, the file is unreadable, or
    /// the graph has ops this backend doesn't implement.
    /// </summary>
    /// <remarks>
    /// Return type is the narrow <see cref="IModelSession"/> handle so
    /// backends that don't surface tensor I/O (LlamaSharp, future RPC
    /// clients) can satisfy the contract. Tensor-graph backends (ONNX
    /// Runtime, OpenVINO) return concrete sessions implementing the
    /// derived <see cref="IInferenceSession"/>; consumers that need the
    /// tensor surface cast at the use site.
    /// </remarks>
    ValueTask<IModelSession> LoadAsync(
        InferenceLoadRequest request,
        CancellationToken cancellationToken);
}
