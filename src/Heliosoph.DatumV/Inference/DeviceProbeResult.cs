namespace Heliosoph.DatumV.Inference;

/// <summary>
/// One row in a backend's full device-probe picture: a device the backend
/// could theoretically address, paired with whether it is actually usable on
/// this machine right now and a human-readable reason when it isn't.
/// </summary>
/// <param name="Device">The device kind the backend recognises.</param>
/// <param name="Available">
/// <see langword="true"/> when the backend successfully attached the execution
/// provider on a probe <c>SessionOptions</c>; <see langword="false"/> when the
/// probe threw (missing native library, EP not built into this ORT binary,
/// platform mismatch, etc.).
/// </param>
/// <param name="Reason">
/// Short explanation when <see cref="Available"/> is <see langword="false"/>
/// — typically the probe exception's message or a platform-mismatch note
/// ("CoreML is macOS-only"). Empty string when <see cref="Available"/> is
/// <see langword="true"/>.
/// </param>
public sealed record DeviceProbeResult(
    InferenceDevice Device,
    bool Available,
    string Reason);
