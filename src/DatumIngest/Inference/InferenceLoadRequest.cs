namespace DatumIngest.Inference;

/// <summary>
/// All the inputs the dispatcher passes to <see cref="IInferenceBackend.LoadAsync"/>
/// when asking it to instantiate a session. Composed by the dispatcher from
/// the bundle metadata + user preferences + the device the dispatcher's
/// policy layer chose.
/// </summary>
/// <param name="ModelFilePath">
/// Absolute path to the ONNX file the session should load. For multi-session
/// bundles the dispatcher invokes <see cref="IInferenceBackend.LoadAsync"/>
/// once per session, each with its own path.
/// </param>
/// <param name="SessionName">
/// Logical name for this session within the bundle (e.g. <c>"default"</c>,
/// <c>"vision_encoder"</c>, <c>"decoder"</c>). Used in diagnostics and by
/// multi-session MODEL adapters to dispatch <c>infer('session-name', ...)</c>
/// calls.
/// </param>
/// <param name="Device">
/// The device the dispatcher's policy layer picked. The backend honours
/// this; if it can't (driver missing, op unsupported), it throws and the
/// dispatcher tries the next candidate device.
/// </param>
/// <param name="Optimization">
/// Graph-optimization aggressiveness. The backend maps this to its native
/// equivalent.
/// </param>
/// <param name="DeclaredResidentBytes">
/// Author-declared estimate of resident bytes for this specific session.
/// Populated by the dispatcher from
/// <see cref="BundleManifest.DeclaredResidentBytes"/>. When <see langword="null"/>,
/// the backend falls back to its default estimate (typically
/// <c>1.5 × file_size</c>). When set, the backend enforces that the
/// value is ≥ the ONNX file size and uses it as the
/// <see cref="IInferenceSession.EstimatedResidentBytes"/>.
/// </param>
public sealed record InferenceLoadRequest(
    string ModelFilePath,
    string SessionName,
    InferenceDevice Device,
    InferenceOptimization Optimization,
    long? DeclaredResidentBytes = null);

/// <summary>
/// User-tunable preferences considered by the dispatcher when picking a
/// backend + device for a bundle. All fields are optional; defaults pick
/// the highest-throughput device the bundle fits on.
/// </summary>
/// <param name="ForcedBackend">
/// When set, the dispatcher uses this backend even when others might
/// benchmark better. Useful for diagnostic comparisons ("force OpenVINO
/// for this model").
/// </param>
/// <param name="ForcedDevice">
/// When set, the dispatcher uses this device even when its preferred-
/// backend ranking would pick something else. Pair with
/// <see cref="ForcedBackend"/> for full override.
/// </param>
/// <param name="Power">Power-budget hint.</param>
/// <param name="Latency">Latency-vs-throughput hint.</param>
public sealed record InferencePreferences(
    InferenceBackendId? ForcedBackend = null,
    InferenceDevice? ForcedDevice = null,
    PowerPreference Power = PowerPreference.Balanced,
    LatencyPreference Latency = LatencyPreference.Balanced);
