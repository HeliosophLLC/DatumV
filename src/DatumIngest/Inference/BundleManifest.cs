namespace Heliosoph.DatumV.Inference;

/// <summary>
/// Minimal metadata a backend needs to make a load decision about a bundle:
/// what file(s) it has, what opset / ops are involved, and any author hints
/// about which backends or devices should be tried first.
/// </summary>
/// <remarks>
/// <para>
/// This is the inference-layer's view of a bundle. The richer applet-level
/// bundle format (with UDF declarations, schema migrations, tokenizer
/// configuration, etc.) lives one layer up; only the fields relevant to
/// "which backend, which device" appear here.
/// </para>
/// <para>
/// Most fields are optional. A bundle that declares nothing falls into the
/// dispatcher's default policy.
/// </para>
/// </remarks>
/// <param name="BundleId">
/// Stable identifier for the bundle (e.g. <c>"Heliosoph.DatumV/bge-small-en-v1.5-onnx"</c>).
/// Used for caching and diagnostics.
/// </param>
/// <param name="Sessions">
/// Logical session name → file path. Single-session bundles have one entry
/// keyed <c>"default"</c>. Multi-session bundles (Florence-2) list each
/// ONNX file under its role name (<c>"vision_encoder"</c>, <c>"decoder"</c>,
/// etc.).
/// </param>
/// <param name="PreferredBackends">
/// Ordered list of backend ids the bundle author thinks fit best. The
/// dispatcher walks this list and picks the first whose
/// <see cref="IInferenceBackend.Inspect"/> returns supported. Empty means
/// "use dispatcher defaults."
/// </param>
/// <param name="MinOpsetVersion">
/// Minimum ONNX opset version the bundle requires. Backends below this are
/// rejected pre-load.
/// </param>
/// <param name="RequiredOps">
/// ONNX op names the bundle is known to use (e.g.
/// <c>"LayerNormalization"</c>, <c>"MultiHeadAttention"</c>). Backends that
/// declare these in their support set qualify; others are skipped.
/// </param>
/// <param name="DeclaredResidentBytes">
/// Per-session author-declared estimate of resident memory after load
/// (weights + activation arenas + EP-internal buffers). Used by the
/// dispatcher and residency manager to budget VRAM/RAM before loading.
/// <para>
/// Treated as a hint, not a contract. The engine enforces two safety
/// rails: (1) the declared value must be ≥ the ONNX file size on disk
/// (rejected as misconfigured if below — catches obvious lies), and (2)
/// the loaded session's actual resident bytes are measured on first
/// load and cached per (bundle, device) for subsequent loads, so a
/// declared-low / actually-large bundle is detected the first time it
/// runs.
/// </para>
/// <para>
/// Missing entries fall back to a default estimate of <c>1.5 × file_size</c>.
/// Keys are session names matching <paramref name="Sessions"/>.
/// </para>
/// </param>
public sealed record BundleManifest(
    string BundleId,
    IReadOnlyDictionary<string, string> Sessions,
    IReadOnlyList<InferenceBackendId> PreferredBackends,
    int MinOpsetVersion = 0,
    IReadOnlyList<string>? RequiredOps = null,
    IReadOnlyDictionary<string, long>? DeclaredResidentBytes = null);
