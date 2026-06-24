// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member

namespace Heliosoph.DatumV.GpuRuntime;

/// <summary>
/// Compile-time flags describing which GPU stack this build was compiled
/// for. Sourced from the GpuVariant MSBuild property which writes either
/// GPU_VARIANT_CUDA or GPU_VARIANT_STANDARD into DefineConstants.
///
/// Used by the GPU section in Settings to hide the "Install CUDA support"
/// affordance from standard-variant builds — those don't ship
/// libonnxruntime_providers_cuda.so / libggml-cuda.so, so installing the
/// runtime libs wouldn't enable any actual acceleration.
/// </summary>
public static class GpuBuildVariant
{
#if GPU_VARIANT_CUDA
    public const bool SupportsCuda = true;
    public const string Name = "cuda";
#else
    public const bool SupportsCuda = false;
    public const string Name = "standard";
#endif
}
