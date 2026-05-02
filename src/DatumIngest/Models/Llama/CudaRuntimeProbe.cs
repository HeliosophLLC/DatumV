using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DatumIngest.Models.Llama;

/// <summary>
/// Locates the CUDA 12 Runtime DLLs (<c>cudart64_12.dll</c>, <c>cublas64_12.dll</c>,
/// <c>cublasLt64_12.dll</c>) on the host. LlamaSharp's CUDA backend NuGet ships
/// the llama.cpp shared libs (<c>ggml-cuda.dll</c>, <c>llama.dll</c>) but not the
/// CUDA Runtime itself — that's an NVIDIA-installed system dependency.
/// </summary>
/// <remarks>
/// <para>
/// Probes the system PATH first; if not found there, checks well-known bundled
/// install locations (currently Ollama, which ships its own copy of the CUDA
/// Runtime under <c>%LOCALAPPDATA%\Programs\Ollama\lib\ollama\cuda_v12</c>).
/// When a viable directory is found, it's prepended to the process-local PATH
/// so the OS's DLL search picks it up — equivalent to the user adding the
/// directory to PATH manually, but scoped to this process.
/// </para>
/// <para>
/// The probe runs at most once per process. Failure is non-fatal: callers
/// decide what to do (LlamaModel / LlamaSharpBackend surface it as a clear
/// error when <see cref="LlamaNativeConfig.RequireCuda"/> is set, or silently
/// allow CPU fallback otherwise).
/// </para>
/// <para>
/// <strong>Windows-only.</strong> All probe paths walk Windows-specific
/// directory layouts (<c>C:\Program Files\NVIDIA\...</c>,
/// <c>%LOCALAPPDATA%\Programs\Ollama</c>) and the DLL search behaviour
/// being augmented (process PATH, same-directory search, kernel32
/// <c>GetModuleFileName</c>) is Windows-specific too. On other platforms
/// the entry points are no-ops — Linux's standard <c>LD_LIBRARY_PATH</c>
/// + system <c>/usr/local/cuda*/lib64</c> conventions handle this natively
/// and don't need probing.
/// </para>
/// </remarks>
internal static class CudaRuntimeProbe
{
    private const string Cudart12 = "cudart64_12.dll";

    /// <summary>
    /// The three CUDA 12 Runtime DLLs that <c>ggml-cuda.dll</c> depends on.
    /// Listed together because they version-lock — copying one without the
    /// others produces missing-symbol errors at native init.
    /// </summary>
    private static readonly string[] RequiredCudaDlls =
    [
        "cudart64_12.dll",
        "cublas64_12.dll",
        "cublasLt64_12.dll",
    ];

    /// <summary>
    /// Result describing how the CUDA Runtime was located, for logging.
    /// </summary>
    public enum Result
    {
        /// <summary>Already on the process PATH; no changes made.</summary>
        FoundOnPath,
        /// <summary>Located in a bundled install directory and prepended to PATH.</summary>
        FoundInBundle,
        /// <summary>Not found anywhere — caller decides how to proceed.</summary>
        NotFound,
    }

    /// <summary>
    /// Ensures CUDA 12 Runtime DLLs are reachable for native loaders. Both
    /// prepends the source directory to the process PATH (legacy DLL search)
    /// <strong>and</strong> copies the DLLs into LlamaSharp's runtime native
    /// folder when present (same-directory search, which restricted-search
    /// flags like <c>LOAD_LIBRARY_SEARCH_DEFAULT_DIRS</c> still honor).
    /// Returns the outcome and (when applicable) the directory that was used.
    /// </summary>
    public static (Result Outcome, string? Directory) EnsureOnPath()
    {
        // Non-Windows hosts rely on standard CUDA conventions (LD_LIBRARY_PATH,
        // /usr/local/cuda*/lib64 system-wide install). Nothing here applies —
        // return NotFound and let the caller's normal fallback path proceed.
        if (!OperatingSystem.IsWindows())
        {
            return (Result.NotFound, null);
        }

        // Run cuDNN discovery on every call. The cuDNN install directory is
        // independent of the CUDA Runtime; LLamaSharp didn't need it but
        // ONNX Runtime's CUDA EP does. Idempotent: if already on PATH,
        // EnsureCudnnOnPath is a no-op. We don't gate cuDNN discovery on
        // CUDA Runtime status because cuDNN's bin dir might already be on
        // PATH even when cudart isn't.
        EnsureCudnnOnPath();

        if (IsCudartOnPath())
        {
            return (Result.FoundOnPath, null);
        }

        foreach (string candidate in BundledCudaPaths())
        {
            if (File.Exists(Path.Combine(candidate, Cudart12)))
            {
                PrependToProcessPath(candidate);
                CopyDllsToLlamaNativeFolder(candidate);
                return (Result.FoundInBundle, candidate);
            }
        }

        return (Result.NotFound, null);
    }

    /// <summary>
    /// Locates an installed cuDNN 9.x bin directory and prepends it to the
    /// process PATH. cuDNN is required by ONNX Runtime's CUDA EP but not by
    /// LLamaSharp (llama.cpp implements its own GEMM). Standard install
    /// locations on Windows:
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>C:\Program Files\NVIDIA\CUDNN\v9.x\bin\&lt;cuda-major.minor&gt;\x64</c> —
    ///     NVIDIA's modern cuDNN installer layout. cuDNN 9.x ships DLLs
    ///     organised by the CUDA version they were built against (e.g.
    ///     <c>12.9</c>, <c>13.2</c>); we walk each <c>v9.*</c> install and
    ///     pick the highest-versioned <c>12.*</c> subdirectory.
    ///   </description></item>
    ///   <item><description>
    ///     <c>C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.x\bin</c> —
    ///     when users have copied cuDNN DLLs directly into the CUDA Toolkit
    ///     bin folder (the older recommended layout, before cuDNN got its
    ///     own installer).
    ///   </description></item>
    /// </list>
    /// Idempotent: if cuDNN is already on PATH, returns without changes.
    /// Non-fatal on failure: caller proceeds and CUDA EP load reports the
    /// underlying error.
    /// </summary>
    private static void EnsureCudnnOnPath()
    {
        if (IsCudnnOnPath()) return;

        foreach (string candidate in BundledCudnnPaths())
        {
            if (HasCudnnDlls(candidate))
            {
                PrependToProcessPath(candidate);
                return;
            }
        }
    }

    private static IEnumerable<string> BundledCudnnPaths()
    {
        // 1) NVIDIA cuDNN installer layout: C:\Program Files\NVIDIA\CUDNN\v9.x\bin\<cuda-version>\x64
        string cudnnRoot = @"C:\Program Files\NVIDIA\CUDNN";
        if (Directory.Exists(cudnnRoot))
        {
            foreach (string installDir in Directory.EnumerateDirectories(cudnnRoot, "v9.*")
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
            {
                string binRoot = Path.Combine(installDir, "bin");
                if (!Directory.Exists(binRoot)) continue;

                // Prefer 12.* CUDA-version subdirectories; sort descending so
                // 12.9 is preferred over 12.6 when both are present.
                IOrderedEnumerable<string> cudaDirs = Directory.EnumerateDirectories(binRoot, "12.*")
                    .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase);
                foreach (string cudaDir in cudaDirs)
                {
                    string x64Dir = Path.Combine(cudaDir, "x64");
                    if (Directory.Exists(x64Dir)) yield return x64Dir;
                }
            }
        }

        // 2) cuDNN copied into CUDA Toolkit bin (older convention).
        string toolkitRoot = @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA";
        if (Directory.Exists(toolkitRoot))
        {
            foreach (string versionDir in Directory.EnumerateDirectories(toolkitRoot, "v12.*"))
            {
                yield return Path.Combine(versionDir, "bin");
            }
        }
    }

    /// <summary>
    /// Tests whether the canonical cuDNN umbrella loader (<c>cudnn64_9.dll</c>)
    /// is reachable via the current process PATH. cuDNN 9 ships several
    /// DLLs (<c>cudnn_graph64_9.dll</c>, <c>cudnn_ops64_9.dll</c>, etc.); we
    /// probe for the umbrella because it's required and its presence
    /// implies the rest were extracted alongside it.
    /// </summary>
    private static bool IsCudnnOnPath()
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return false;

        foreach (string dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(dir, "cudnn64_9.dll"))) return true;
            }
            catch
            {
                // Same defensive skip as IsCudartOnPath — malformed PATH
                // entries shouldn't break startup.
            }
        }
        return false;
    }

    private static bool HasCudnnDlls(string directory)
    {
        try
        {
            // cuDNN 9 split the monolithic DLL into several. We require at
            // least the umbrella loader; the others sit alongside.
            return File.Exists(Path.Combine(directory, "cudnn64_9.dll"));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Copies the three CUDA Runtime DLLs from <paramref name="sourceDir"/>
    /// into LlamaSharp's CUDA 12 native folder
    /// (<c>runtimes/win-x64/native/cuda12/</c> under the app's base directory)
    /// so the OS finds them via same-directory search when loading
    /// <c>ggml-cuda.dll</c>. Skips files that already exist; non-fatal on copy
    /// failure (we still have the PATH prepend as a fallback).
    /// </summary>
    private static void CopyDllsToLlamaNativeFolder(string sourceDir)
    {
        string targetDir = Path.Combine(
            AppContext.BaseDirectory,
            "runtimes",
            "win-x64",
            "native",
            "cuda12");

        if (!Directory.Exists(targetDir))
        {
            // The CUDA backend NuGet didn't deploy here — nothing to augment.
            // PATH prepend will have to carry the day.
            return;
        }

        foreach (string dll in RequiredCudaDlls)
        {
            string src = Path.Combine(sourceDir, dll);
            string dst = Path.Combine(targetDir, dll);

            if (!File.Exists(src) || File.Exists(dst)) continue;

            try
            {
                File.Copy(src, dst);
            }
            catch
            {
                // Permission denied, file in use, etc. — best-effort, don't
                // break startup over it. The PATH prepend is the fallback.
            }
        }
    }

    private static IEnumerable<string> BundledCudaPaths()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        // Ollama ships a CUDA Runtime alongside its own llama.cpp build.
        yield return Path.Combine(localAppData, "Programs", "Ollama", "lib", "ollama", "cuda_v12");
        // System-wide CUDA toolkit installs land here. Walk all 12.x versions
        // since users may have multiple side-by-side; the loader picks whichever
        // we add first.
        string toolkitRoot = @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA";
        if (Directory.Exists(toolkitRoot))
        {
            foreach (string versionDir in Directory.EnumerateDirectories(toolkitRoot, "v12.*"))
            {
                yield return Path.Combine(versionDir, "bin");
            }
        }
    }

    private static bool IsCudartOnPath()
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return false;

        foreach (string dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(dir, Cudart12))) return true;
            }
            catch
            {
                // PATH entries can be malformed (invalid chars, network paths
                // we can't reach); skip and keep probing the rest.
            }
        }
        return false;
    }

    private static void PrependToProcessPath(string directory)
    {
        string? existing = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        Environment.SetEnvironmentVariable("PATH", $"{directory};{existing}");
    }

    /// <summary>
    /// Verifies the CUDA Runtime DLLs can actually be loaded by Windows from
    /// their target locations. Reports per-DLL outcome — successful load
    /// (returns the resolved path) or the OS error message if it failed.
    /// Writes results to <paramref name="log"/> for diagnostic visibility.
    /// </summary>
    /// <remarks>
    /// Calling <c>LoadLibrary</c> ourselves before LlamaSharp triggers the
    /// real native init lets us pin down "which DLL is the loader actually
    /// failing on" — LlamaSharp's wrapped error doesn't tell us. We free the
    /// handle immediately afterward so we don't perturb LlamaSharp's own load.
    /// </remarks>
    public static void DiagnoseDllLoad(Action<string> log)
    {
        if (!OperatingSystem.IsWindows())
        {
            log("[cuda-probe] skipped: not running on Windows");
            return;
        }

        foreach (string dll in RequiredCudaDlls)
        {
            try
            {
                IntPtr handle = NativeLibrary.Load(dll);
                try
                {
                    string? resolved = TryGetModuleFileName(handle);
                    log($"[cuda-probe] {dll}: loaded OK ({resolved ?? "(path query failed)"})");
                }
                finally
                {
                    NativeLibrary.Free(handle);
                }
            }
            catch (DllNotFoundException ex)
            {
                log($"[cuda-probe] {dll}: NOT FOUND — {ex.Message}");
            }
            catch (BadImageFormatException ex)
            {
                log($"[cuda-probe] {dll}: BAD IMAGE — {ex.Message}");
            }
            catch (Exception ex)
            {
                log($"[cuda-probe] {dll}: failed — {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetModuleFileName(IntPtr hModule, [Out] char[] lpFilename, int nSize);

    [SupportedOSPlatform("windows")]
    private static string? TryGetModuleFileName(IntPtr handle)
    {
        char[] buffer = new char[1024];
        int len = GetModuleFileName(handle, buffer, buffer.Length);
        if (len == 0)
        {
            int err = Marshal.GetLastWin32Error();
            return $"(GetModuleFileName failed: {new Win32Exception(err).Message})";
        }
        return new string(buffer, 0, len);
    }
}
