using DatumIngest.Models.Llama;

using Microsoft.ML.OnnxRuntime;

namespace DatumIngest.Models.Onnx;

/// <summary>
/// Single source of truth for creating ONNX Runtime <see cref="InferenceSession"/>
/// instances with the right execution provider chain. CUDA when available,
/// CPU as the implicit fallback.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why centralise this.</strong> Every ONNX-backed model in the
/// engine creates one or more sessions. Without a shared factory, each
/// model would carry its own try/catch around <c>AppendExecutionProvider_CUDA</c>
/// and the CUDA-availability state — a cross-cutting concern that belongs
/// in one place. This class is that place.
/// </para>
/// <para>
/// <strong>CUDA-or-CPU fallback.</strong> The first call probes CUDA. If
/// the CUDA EP loads, every subsequent session adds it. If CUDA fails
/// (no GPU, missing CUDA Runtime, version mismatch with cuDNN), the
/// availability cache flips and every subsequent session skips the CUDA
/// add — saving the cost of the failed call. ONNX Runtime always has the
/// CPU EP loaded as the default catch-all, so a session created with
/// no execution providers explicitly registered runs on CPU correctly.
/// </para>
/// <para>
/// <strong>Why not Probe-and-set-once.</strong> Probing requires building
/// a throwaway <see cref="SessionOptions"/> just to attempt the CUDA add.
/// We can do the work that was going to be wasted anyway and remember the
/// outcome, since we're about to construct a real <see cref="SessionOptions"/>
/// regardless.
/// </para>
/// </remarks>
internal static class OnnxSessionFactory
{
    /// <summary>
    /// Cached CUDA-availability state. <c>0</c> = unknown (haven't probed),
    /// <c>1</c> = available, <c>-1</c> = unavailable. Volatile so a worker
    /// thread sees the result of probing on the constructor thread.
    /// </summary>
    private static volatile int _cudaState = 0;

    /// <summary>
    /// Creates an <see cref="InferenceSession"/> for the ONNX model at
    /// <paramref name="modelFilePath"/>, configured with CUDA when
    /// available and CPU as the implicit fallback.
    /// </summary>
    public static InferenceSession Create(string modelFilePath)
    {
        SessionOptions options = new();
        TryAppendCuda(options);
        return new InferenceSession(modelFilePath, options);
    }

    /// <summary>
    /// Returns <see langword="true"/> if the most-recent probe (or any
    /// probe in the lifetime of this process) found CUDA loadable. Mostly
    /// useful for diagnostics — production code shouldn't branch on this;
    /// it should call <see cref="Create"/> and trust the fallback.
    /// </summary>
    public static bool IsCudaLikelyAvailable => _cudaState == 1;

    private static void TryAppendCuda(SessionOptions options)
    {
        // If we previously confirmed CUDA isn't available, skip the
        // probe — saves the cost of constructing the failed call each
        // time. If CUDA is available, we still need to add it to each
        // new SessionOptions instance (it's not a global default).
        if (_cudaState == -1) return;

        // Make sure CUDA Runtime DLLs are reachable. The probe locates
        // cudart/cublas/cublasLt from Ollama's bundled install or the
        // system CUDA toolkit and prepends them to PATH. Idempotent;
        // calling before each session is fine. Without this,
        // onnxruntime_providers_cuda.dll fails LoadLibrary with error
        // 126 even on machines that "have CUDA" via tools that bundle
        // their own copies (Ollama, LLamaSharp).
        CudaRuntimeProbe.EnsureOnPath();

        try
        {
            options.AppendExecutionProvider_CUDA(0);
            _cudaState = 1;
        }
        catch (OnnxRuntimeException)
        {
            // CUDA EP isn't loadable on this machine — missing GPU,
            // missing CUDA Runtime, or cuDNN version mismatch. The
            // SessionOptions' implicit CPU EP handles dispatch from
            // here; nothing more to configure.
            _cudaState = -1;
        }
        catch (DllNotFoundException)
        {
            // Native CUDA DLLs not on PATH — same outcome, fall back
            // to CPU.
            _cudaState = -1;
        }
    }
}
