using Heliosoph.DatumV.Models.Llama;
using LLama;
using LLama.Common;

namespace Heliosoph.DatumV.Inference.LlamaSharp;

/// <summary>
/// <see cref="IInferenceBackend"/> implementation backed by LlamaSharp /
/// llama.cpp. Loads <c>.gguf</c> files and produces non-tensor
/// <see cref="LlamaSharpSession"/> handles for the LLM-shaped scalars
/// (<c>llama_generate</c>, <c>llama_chat</c>) to dispatch through.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Probe strategy.</strong> CPU is always reported as available
/// (LlamaSharp's CPU backend is built-in). The accelerator entry depends
/// on the build variant:
/// </para>
/// <list type="bullet">
///   <item>CUDA variant: CUDA availability is derived from
///   <see cref="CudaRuntimeProbe"/> — if the CUDA 12 Runtime DLLs are
///   reachable, CUDA is listed; otherwise it isn't.
///   <see cref="LlamaNativeConfig.RequireCuda"/> affects only the failure
///   mode at load time (loud error vs silent CPU fallback), not the
///   device list.</item>
///   <item>Standard variant: Vulkan is reported as available
///   unconditionally — LlamaSharp doesn't expose a pre-load Vulkan probe,
///   so the actual driver/device check happens during
///   <c>LLamaWeights.LoadFromFile</c>. AutoFallback demotes to CPU on
///   real failures and the log callback surfaces the underlying reason.</item>
/// </list>
/// <para>
/// <strong>Single device per session.</strong> LlamaSharp loads the entire
/// model onto one target (GpuLayerCount = 999 offloads everything to GPU
/// when an accelerator is available; falls back to CPU otherwise).
/// Mixed-device dispatch is a LlamaSharp 0.27+ feature we don't take
/// advantage of.
/// </para>
/// </remarks>
public sealed class LlamaSharpBackend : IInferenceBackend
{
    // Default context window when no per-load override is supplied. Wide
    // enough for typical chat sessions; far below the 128K Phi-3.5 / Llama
    // 3.1 ceiling to keep KV cache costs predictable. Per-CREATE-MODEL
    // override syntax lands with the LLM-shaped scalars in a later slice.
    private const uint DefaultContextSize = 8192;

    private IReadOnlyList<DeviceProbeResult>? _probedDevices;
    private IReadOnlyList<InferenceDevice>? _availableDevices;
    private readonly object _probeLock = new();

    /// <inheritdoc />
    public InferenceBackendId Id => InferenceBackendId.LlamaSharp;

    /// <inheritdoc />
    /// <remarks>
    /// Opset version is meaningless for GGUF (it's not an ONNX format), so
    /// the value is unused by the dispatcher's GGUF path. Kept as int.MaxValue
    /// so opset-version filtering in the bundle manifest never excludes this
    /// backend.
    /// </remarks>
    public int MaxSupportedOpset => int.MaxValue;

    /// <inheritdoc />
    public IReadOnlyList<InferenceDevice> AvailableDevices
    {
        get
        {
            EnsureProbed();
            return _availableDevices!;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<DeviceProbeResult> ProbeAllDevices()
    {
        EnsureProbed();
        return _probedDevices!;
    }

    private void EnsureProbed()
    {
        if (_probedDevices is not null) return;
        lock (_probeLock)
        {
            if (_probedDevices is not null) return;

            List<DeviceProbeResult> probed = new(2);
            List<InferenceDevice> available = new(2);

            // CPU is always available — LlamaSharp ships an AVX2 CPU backend
            // alongside every accelerator backend.
            probed.Add(new DeviceProbeResult(
                InferenceDevice.LlamaSharpCpu,
                Available: true,
                Reason: "LlamaSharp CPU backend (AVX2) is always available."));
            available.Add(InferenceDevice.LlamaSharpCpu);

#if GPU_VARIANT_CUDA
            // CUDA depends on whether the CUDA 12 Runtime DLLs are reachable.
            // This is the same probe LlamaModel's constructor relies on to
            // decide whether to throw at load time; running it during device
            // discovery means `inference.devices()` can answer "is CUDA
            // available?" without paying the model-load cost.
            (CudaRuntimeProbe.Result outcome, string? dir) = CudaRuntimeProbe.EnsureOnPath();
            if (outcome != CudaRuntimeProbe.Result.NotFound)
            {
                probed.Add(new DeviceProbeResult(
                    InferenceDevice.LlamaSharpCuda,
                    Available: true,
                    Reason: dir is not null
                        ? $"CUDA 12 Runtime found at: {dir}"
                        : "CUDA 12 Runtime already on PATH."));
                available.Add(InferenceDevice.LlamaSharpCuda);
            }
            else
            {
                probed.Add(new DeviceProbeResult(
                    InferenceDevice.LlamaSharpCuda,
                    Available: false,
                    Reason: "CUDA 12 Runtime DLLs (cudart64_12.dll, cublas64_12.dll, "
                        + "cublasLt64_12.dll) not found on PATH or in bundled locations."));
            }
#else
            // LlamaSharp doesn't expose a pre-load Vulkan probe; the loader
            // itself attempts vkEnumeratePhysicalDevices during LoadFromFile.
            // Report as available and let AutoFallback demote to CPU on
            // real driver/device failures (the log callback surfaces why).
            probed.Add(new DeviceProbeResult(
                InferenceDevice.LlamaSharpVulkan,
                Available: true,
                Reason: "Vulkan backend bundled; actual driver availability verified at load time."));
            available.Add(InferenceDevice.LlamaSharpVulkan);
#endif

            _probedDevices = probed;
            _availableDevices = available;
        }
    }

    /// <inheritdoc />
    public BackendCompatibility Inspect(BundleManifest bundle)
    {
        // Extension gate: LlamaSharp only handles .gguf files. The
        // dispatcher walks backends and picks the first whose Inspect
        // succeeds — rejecting non-GGUF files here lets ORT pick up
        // .onnx bundles without misroute.
        foreach ((_, string filePath) in bundle.Sessions)
        {
            if (!filePath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            {
                return BackendCompatibility.NotSupported(
                    $"LlamaSharp backend handles .gguf files only; got '{System.IO.Path.GetFileName(filePath)}'.");
            }
        }

        return BackendCompatibility.Supported(estimatedLoadCostMs: 0);
    }

    /// <inheritdoc />
    public ValueTask<IModelSession> LoadAsync(
        InferenceLoadRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(request.ModelFilePath))
        {
            throw new FileNotFoundException(
                $"GGUF model file not found at '{request.ModelFilePath}'.",
                request.ModelFilePath);
        }

        // Native config has to land before the first LoadFromFile — the
        // singleton locks after that. EnsureConfigured is idempotent;
        // LlamaModel's constructor calls the same entry point.
        try
        {
            LlamaNativeConfig.EnsureConfigured();
        }
        catch (TypeInitializationException ex)
        {
            // .NET wraps native-load failures in TypeInitializationException.
            // Walk the chain to the real cause for the diagnostic.
            Exception root = ex;
            while (root.InnerException is not null) root = root.InnerException;
            throw new InvalidOperationException(
                $"LlamaSharp native libraries failed to initialize. Root cause: "
                    + $"{root.GetType().Name}: {root.Message}",
                ex);
        }

        // Honour the device the dispatcher picked. GpuLayerCount = 999
        // offloads every layer to GPU; llama.cpp caps to the model's
        // actual layer count so this is safe. CPU device → 0 layers.
        int gpuLayers = request.Device switch
        {
            InferenceDevice.LlamaSharpCuda => 999,
            InferenceDevice.LlamaSharpVulkan => 999,
            _ => 0,
        };

        ModelParams modelParams = new(request.ModelFilePath)
        {
            ContextSize = DefaultContextSize,
            GpuLayerCount = gpuLayers,
        };

        LLamaWeights weights;
        try
        {
            weights = LLamaWeights.LoadFromFile(modelParams);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"LlamaSharp failed to load GGUF model '{request.ModelFilePath}'. "
                    + $"Device: {request.Device}. Cause: {ex.GetType().Name}: {ex.Message}",
                ex);
        }

        StatelessExecutor executor = new(weights, modelParams);

        // File size as the lower bound + a context-proportional KV cache
        // estimate. KV cache is roughly 2 × layers × heads × head_dim ×
        // sizeof(fp16) × context_tokens — we don't have layer counts at
        // this layer, so use a coarse 2 MiB-per-1K-tokens upper bound that
        // covers most consumer LLMs at fp16 KV. Errs high per the
        // EstimatedResidentBytes contract.
        long fileBytes = new FileInfo(request.ModelFilePath).Length;
        long kvCacheEstimate = (long)DefaultContextSize * 2 * 1024;
        long declared = request.DeclaredResidentBytes ?? (fileBytes + kvCacheEstimate);

        LlamaSharpSession session = new(
            weights,
            modelParams,
            executor,
            request.Device,
            estimatedResidentBytes: declared);

        return new ValueTask<IModelSession>(session);
    }
}
