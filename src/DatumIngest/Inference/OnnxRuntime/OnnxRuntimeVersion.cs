using System.Reflection;

using Microsoft.ML.OnnxRuntime;

namespace DatumIngest.Inference.OnnxRuntime;

/// <summary>
/// Statically-anchored helper that exposes the loaded
/// <c>Microsoft.ML.OnnxRuntime</c> assembly's informational version.
/// Used by <see cref="DatumIngest.Diagnostics.HostFingerprint"/> as one
/// of the four host-identity fields that key the calibration cache.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists separately from
/// <see cref="DatumIngest.Diagnostics.HostFingerprint"/>.</strong> The
/// Diagnostics project layer deliberately does NOT reference ONNX
/// Runtime — it's a leaf used by code paths that don't need inference.
/// Earlier versions of <c>HostFingerprint</c> looked up ORT
/// reflectively via <c>Assembly.Load("Microsoft.ML.OnnxRuntime")</c>,
/// which works at runtime but is unsafe for two reasons: NativeAOT
/// throws <c>PlatformNotSupportedException</c> on dynamic name-based
/// loads, and the trimmer can't see the dependency so it emits IL2026
/// "requires unreferenced code" warnings. Anchoring on
/// <see cref="InferenceSession"/>'s assembly via <c>typeof(...).Assembly</c>
/// gives the trimmer a hard reference + lets NativeAOT statically
/// resolve everything; the helper lives in the Inference layer where
/// the ORT reference is already in the project graph.
/// </para>
/// <para>
/// Returns the assembly's
/// <see cref="AssemblyInformationalVersionAttribute"/> when present
/// (the NuGet semver) and falls back to the four-part assembly
/// version otherwise. Both are static metadata on the already-loaded
/// assembly; no reflection happens at the per-query hot path.
/// </para>
/// </remarks>
internal static class OnnxRuntimeVersion
{
    /// <summary>
    /// Cached version string for the running ORT assembly. Computed
    /// once via <c>typeof(InferenceSession).Assembly</c> on first
    /// access; the assembly itself is loaded eagerly by the engine's
    /// project reference, so the lookup is metadata-only.
    /// </summary>
    public static string Value { get; } = ComputeVersion();

    private static string ComputeVersion()
    {
        Assembly assembly = typeof(InferenceSession).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }
}
