using LLama.Native;

namespace DatumIngest.Models.Llama;

/// <summary>
/// Process-wide singleton configuration for LlamaSharp's native loader. The
/// underlying <see cref="NativeLibraryConfig"/> is itself a singleton: the
/// first <c>LoadFromFile</c> call locks in the choice and subsequent
/// loaders cannot change it. This static gates both the legacy
/// <see cref="LlamaModel"/> constructor path and the newer
/// <c>LlamaSharpBackend.LoadAsync</c> path through one set of pre-load
/// decisions so they agree on CUDA-vs-CPU regardless of which lands first.
/// </summary>
public static class LlamaNativeConfig
{
    private static readonly object _lock = new();
    private static bool _configured;

    /// <summary>
    /// When <see langword="true"/> (the default), require the CUDA backend to load
    /// successfully and refuse silent CPU fallback. Set to <see langword="false"/>
    /// to allow CPU when CUDA isn't available — useful for CI / non-GPU machines.
    /// </summary>
    /// <remarks>
    /// Process-wide static state because LlamaSharp's
    /// <see cref="NativeLibraryConfig"/> is a singleton — set this before the
    /// first GGUF load. Subsequent changes are no-ops.
    /// </remarks>
    public static bool RequireCuda { get; set; } = true;

    /// <summary>
    /// Runs once before the first <c>LLamaWeights.LoadFromFile</c> call.
    /// When <see cref="RequireCuda"/> is set, disables LlamaSharp's auto-fallback
    /// so a missing CUDA Runtime surfaces as a clear error rather than a silent
    /// drop to CPU (which is fast to "work" but slow to actually run).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <see cref="RequireCuda"/> is set and the CUDA 12 Runtime DLLs were not
    /// found on the process PATH or in any known bundled location.
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

            // Make the CUDA Runtime DLLs reachable via PATH if we can find them
            // somewhere LlamaSharp's loader can see. The CUDA backend NuGet ships
            // ggml-cuda.dll but not the NVIDIA-installed cudart64_12.dll — without
            // this probe, the loader silently falls back to CPU even on a
            // GPU-capable machine when the user hasn't installed the CUDA toolkit
            // system-wide.
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
                        "or set LlamaNativeConfig.RequireCuda = false to allow CPU fallback (significantly slower).");
                }
                cudaDirAdded = dir;
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

            // PreferVulkan is on by default and ranks above CUDA in the
            // selection policy, so without explicitly disabling it the loader
            // tries the (non-existent) Vulkan native folder first and — with
            // AllowFallback off — gives up before reaching CUDA. We don't ship
            // the Vulkan backend, so opt out unconditionally.
            //
            // SkipCheck(true) is also load-bearing: LlamaSharp's pre-flight
            // CUDA viability probe uses lookup paths that don't include our
            // process PATH prepend, so it concludes "no CUDA available" and
            // silently demotes UseCuda to False — pushing the loader to the
            // CPU AVX2 backend even when PreferCuda is True. Skipping the
            // pre-flight tells the policy to trust our preference and just
            // attempt the CUDA load; if cudart isn't actually reachable, the
            // load itself fails loudly (with AutoFallback off).
            // SkipCheck(true) and WithAutoFallback(true) are mutually exclusive
            // in LlamaSharp 0.27 — the loader throws ArgumentException if both
            // are set. We only need SkipCheck when forcing CUDA (where
            // LlamaSharp's pre-flight CUDA-availability probe is unreliable).
            // When fallback is allowed (CPU-OK builds, CI), let the check run.
            NativeLibraryConfig.LLama
                .WithLogCallback(logCallback)
                .WithVulkan(false)
                .WithCuda(true)
                .SkipCheck(RequireCuda)
                .WithAutoFallback(!RequireCuda);

            if (cudaDirAdded is not null)
            {
                Console.Error.WriteLine($"[llama] Using CUDA Runtime from: {cudaDirAdded}");
            }

            _configured = true;
        }
    }
}
