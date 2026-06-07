using LLama.Native;

namespace Heliosoph.DatumV.Models.Llama;

/// <summary>
/// Process-wide singleton configuration for LlamaSharp's native loader. The
/// underlying <see cref="NativeLibraryConfig"/> is itself a singleton: the
/// first <c>LoadFromFile</c> call locks in the choice and subsequent
/// loaders cannot change it. This static gates both the legacy
/// <see cref="LlamaModel"/> constructor path and the newer
/// <c>LlamaSharpBackend.LoadAsync</c> path through one set of pre-load
/// decisions so they agree on which backend is in use regardless of which
/// lands first.
/// </summary>
/// <remarks>
/// The body splits on the GPU_VARIANT_* build constant set by the
/// <c>GpuVariant</c> MSBuild property:
/// <list type="bullet">
///   <item><c>GPU_VARIANT_CUDA</c> (default): NVIDIA-only stack via the
///   bundled <c>LLamaSharp.Backend.Cuda12.*</c> package. PreferVulkan off,
///   PreferCuda on, no auto-fallback when <see cref="RequireCuda"/>.</item>
///   <item><c>GPU_VARIANT_STANDARD</c>: cross-vendor stack via the bundled
///   <c>LLamaSharp.Backend.Vulkan</c> package. PreferVulkan on, PreferCuda
///   off, auto-fallback on so machines without a Vulkan-capable GPU demote
///   to LlamaSharp's CPU AVX2 backend silently.</item>
/// </list>
/// </remarks>
public static class LlamaNativeConfig
{
    private static readonly object _lock = new();
    private static bool _configured;

    /// <summary>
    /// When <see langword="true"/> (the default in the CUDA variant),
    /// require the CUDA backend to load successfully and refuse silent CPU
    /// fallback. Set to <see langword="false"/> to allow CPU when CUDA isn't
    /// available — useful for CI / non-GPU machines.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Process-wide static state because LlamaSharp's
    /// <see cref="NativeLibraryConfig"/> is a singleton — set this before the
    /// first GGUF load. Subsequent changes are no-ops.
    /// </para>
    /// <para>
    /// In the standard (Vulkan) variant this property has no effect — there
    /// is no CUDA backend bundled, and the Vulkan loader's own
    /// AutoFallback handles missing-driver cases. The property is retained
    /// so test code that sets it compiles against either variant.
    /// </para>
    /// </remarks>
    public static bool RequireCuda { get; set; } =
#if GPU_VARIANT_CUDA
        true;
#else
        false;
#endif

    /// <summary>
    /// Runs once before the first <c>LLamaWeights.LoadFromFile</c> call.
    /// Selects CUDA vs Vulkan based on the build variant.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// (CUDA variant only) <see cref="RequireCuda"/> is set and the CUDA 12
    /// Runtime DLLs were not found on the process PATH or in any known
    /// bundled location.
    /// </exception>
    public static void EnsureConfigured()
    {
        lock (_lock)
        {
            if (_configured) return;
            // The NativeLibraryConfig singleton locks once any library has loaded;
            // skip configuration if we're already past that point (e.g. another
            // model in this process loaded first).
            if (NativeLibraryConfig.LLama.LibraryHasLoaded)
            {
                _configured = true;
                return;
            }

            // Route only Warning+ from LlamaSharp to the console — the lower
            // levels are extremely chatty (per-tensor / per-token traces during
            // load and inference) and drown out actual problems. Bump to a
            // lower threshold here when debugging native-load issues.
            //
            // Continue is a special llama.cpp marker meaning "no newline
            // before me; this is a continuation of the previous log line"
            // (used to print the tensor-loading "..........." dots one at a
            // time). It slips through the >= Warning filter because its
            // numeric value happens to be high; exclude it explicitly.
            NativeLogConfig.LLamaLogCallback logCallback = (level, message) =>
            {
                if (level >= LLamaLogLevel.Warning && level != LLamaLogLevel.Continue)
                {
                    Console.Error.Write($"[llama:{level}] {message}");
                }
            };

            NativeLibraryConfig.LLama.WithLogCallback(logCallback);

#if GPU_VARIANT_CUDA
            ConfigureCudaBackend();
#else
            ConfigureVulkanBackend();
#endif

            _configured = true;
        }
    }

#if GPU_VARIANT_CUDA
    /// <summary>
    /// CUDA variant: bundled <c>LLamaSharp.Backend.Cuda12.*</c>.
    /// PreferVulkan off (we don't ship the Vulkan native in this variant);
    /// PreferCuda on; CUDA Runtime DLL discovery via
    /// <see cref="CudaRuntimeProbe"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>WithVulkan(false)</c> is load-bearing because LlamaSharp's policy
    /// ranks PreferVulkan above PreferCuda; without disabling it the loader
    /// tries the (non-existent) Vulkan native folder first and — with
    /// AllowFallback off — gives up before reaching CUDA.
    /// </para>
    /// <para>
    /// <c>SkipCheck(true)</c> is also load-bearing when forcing CUDA:
    /// LlamaSharp's pre-flight CUDA viability probe uses lookup paths that
    /// don't include our process PATH prepend, so it concludes "no CUDA
    /// available" and silently demotes UseCuda to False — pushing the
    /// loader to the CPU AVX2 backend even when PreferCuda is True.
    /// </para>
    /// <para>
    /// <c>SkipCheck(true)</c> and <c>WithAutoFallback(true)</c> are mutually
    /// exclusive in LlamaSharp 0.27 (the loader throws ArgumentException if
    /// both are set), so we only skip the pre-flight when forcing CUDA;
    /// when fallback is allowed (CPU-OK builds, CI), let the check run.
    /// </para>
    /// </remarks>
    private static void ConfigureCudaBackend()
    {
        string? cudaDirAdded = null;
        if (RequireCuda)
        {
            (CudaRuntimeProbe.Result outcome, string? dir) = CudaRuntimeProbe.EnsureOnPath();
            if (outcome == CudaRuntimeProbe.Result.NotFound)
            {
                throw new InvalidOperationException(
                    "CUDA 12 Runtime DLLs (cudart64_12.dll, cublas64_12.dll, cublasLt64_12.dll) " +
                    "are not on the process PATH and weren't found in known bundled locations " +
                    "(Ollama's lib\\ollama\\cuda_v12 or NVIDIA's CUDA Toolkit install). " +
                    "Either install the CUDA 12.x Runtime from NVIDIA, ensure Ollama is installed (its bundled CUDA folder will be detected automatically), " +
                    "or set LlamaNativeConfig.RequireCuda = false to allow CPU fallback (significantly slower). " +
                    "If you have an AMD or Intel GPU, install the standard variant of DatumV instead — it bundles Vulkan in place of CUDA.");
            }
            cudaDirAdded = dir;
        }

        NativeLibraryConfig.LLama
            .WithVulkan(false)
            .WithCuda(true)
            .SkipCheck(RequireCuda)
            .WithAutoFallback(!RequireCuda);

        if (cudaDirAdded is not null)
        {
            Console.Error.WriteLine($"[llama] Using CUDA Runtime from: {cudaDirAdded}");
        }
    }
#else
    /// <summary>
    /// Standard variant: bundled <c>LLamaSharp.Backend.Vulkan</c>.
    /// PreferVulkan on, PreferCuda off (CUDA backend isn't packaged in this
    /// variant — turning it off prevents LlamaSharp's loader from searching
    /// for it and emitting confusing "CUDA not found" warnings).
    /// AutoFallback on so machines without a Vulkan-capable GPU demote to
    /// LlamaSharp's CPU AVX2 backend silently.
    /// </summary>
    private static void ConfigureVulkanBackend()
    {
        NativeLibraryConfig.LLama
            .WithVulkan(true)
            .WithCuda(false)
            .WithAutoFallback(true);
    }
#endif
}
