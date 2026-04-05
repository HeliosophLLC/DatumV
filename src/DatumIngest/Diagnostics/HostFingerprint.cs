namespace DatumIngest.Diagnostics;

/// <summary>
/// Identifies the host's GPU + driver + runtime configuration. Used as the
/// primary invalidation key for persisted model-calibration data — any
/// field mismatch on startup discards the entire calibration cache, since
/// per-batch VRAM measurements only apply to the hardware they were
/// measured on.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why each field is here.</strong>
/// <list type="bullet">
///   <item><description><c>GpuUuid</c>: hardware identity. Swap the card,
///     calibrate from scratch.</description></item>
///   <item><description><c>VramTotalBytes</c>: catches BIOS reconfig (vGPU
///     slicing, MIG mode changes) that present "same UUID, different
///     budget" — calibration curves keyed to the larger budget would
///     overshoot.</description></item>
///   <item><description><c>DriverVersion</c>: NVIDIA driver upgrades
///     periodically change CUDA allocator behaviour (workspace caching,
///     fragmentation heuristics) that show up as per-batch VRAM
///     drift.</description></item>
///   <item><description><c>OrtVersion</c>: ONNX Runtime arena allocator
///     and execution-provider memory-layout details change between
///     versions; calibration curves are not portable across them.</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Detection failure is OK.</strong> Hosts without NVIDIA GPUs
/// (CPU-only build server, AMD/Intel GPU, NVML unloadable) return
/// <see langword="null"/> from <see cref="Detect"/>. Callers should treat
/// that as "no fingerprint, no persistence" — calibration data is not
/// written and not loaded. The engine runs without persisted curves; it
/// just re-measures every process.
/// </para>
/// </remarks>
public sealed record HostFingerprint(
    string GpuUuid,
    long VramTotalBytes,
    string DriverVersion,
    string OrtVersion)
{
    /// <summary>
    /// Probes the host and returns a fingerprint, or <see langword="null"/>
    /// when any required field can't be determined (no NVML, no GPU 0,
    /// missing ORT version). All-or-nothing on purpose: a partial
    /// fingerprint is worse than none — it would match other partial
    /// fingerprints from unrelated configurations.
    /// </summary>
    /// <param name="ortVersion">
    /// The loaded ONNX Runtime assembly's informational version, computed
    /// by the caller via a hard <c>typeof(InferenceSession).Assembly</c>
    /// reference rather than reflective <c>Assembly.Load</c>. This layer
    /// (<see cref="DatumIngest.Diagnostics"/>) deliberately doesn't
    /// reference Microsoft.ML.OnnxRuntime so non-inference call paths
    /// don't drag the dependency in; the inference layer's
    /// <c>OnnxRuntimeVersion.Value</c> helper supplies the string. Pass
    /// <see langword="null"/> on hosts that ship without ORT — the call
    /// returns <see langword="null"/> and calibration falls through to
    /// in-memory-only.
    /// </param>
    public static HostFingerprint? Detect(string? ortVersion)
    {
        if (!VramProbe.TryGetGpuUuid(out string? uuid)) return null;
        if (!VramProbe.TryGetUsage(out _, out long totalBytes)) return null;
        if (!VramProbe.TryGetDriverVersion(out string? driverVersion)) return null;
        if (string.IsNullOrEmpty(ortVersion)) return null;

        return new HostFingerprint(uuid, totalBytes, driverVersion, ortVersion);
    }

    /// <summary>
    /// True when every field matches <paramref name="other"/>. Value
    /// equality on the record handles this — the named method exists
    /// purely as a readable call site at the use site ("does this
    /// persisted fingerprint match the live host?").
    /// </summary>
    public bool Matches(HostFingerprint other) => Equals(other);
}
