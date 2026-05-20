namespace Heliosoph.DatumV.Inference;

/// <summary>
/// A backend's verdict on whether it can load a given bundle. Returned from
/// <see cref="IInferenceBackend.Inspect"/> so the dispatcher can rank
/// candidate backends without paying load cost up front.
/// </summary>
/// <param name="IsSupported">
/// <see langword="true"/> when this backend can load the bundle on at least
/// one of its available devices. <see langword="false"/> when something
/// disqualifies it — unsupported opset, missing operator, wrong tensor
/// type.
/// </param>
/// <param name="UnsupportedReason">
/// Human-readable explanation when <see cref="IsSupported"/> is
/// <see langword="false"/>. Used for diagnostics — the dispatcher logs the
/// reason when it falls back to a different backend.
/// </param>
/// <param name="EstimatedLoadCostMs">
/// Rough estimate of how long the first <see cref="IInferenceBackend.LoadAsync"/>
/// call will take. The dispatcher uses this to bias toward fast-loading
/// backends when latency-sensitive flags are set. Backends that don't know
/// can leave it as 0.
/// </param>
public readonly record struct BackendCompatibility(
    bool IsSupported,
    string? UnsupportedReason,
    int EstimatedLoadCostMs)
{
    /// <summary>Factory for the supported case. Optional load-cost estimate biases dispatcher selection.</summary>
    public static BackendCompatibility Supported(int estimatedLoadCostMs = 0) =>
        new(true, null, estimatedLoadCostMs);

    /// <summary>Factory for the unsupported case. The reason string surfaces in dispatcher diagnostics.</summary>
    public static BackendCompatibility NotSupported(string reason) =>
        new(false, reason, 0);
}
