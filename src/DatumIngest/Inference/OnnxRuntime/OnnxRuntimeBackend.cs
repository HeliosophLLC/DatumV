using DatumIngest.Models.Llama;
using Microsoft.ML.OnnxRuntime;

namespace DatumIngest.Inference.OnnxRuntime;

/// <summary>
/// <see cref="IInferenceBackend"/> implementation backed by Microsoft's
/// ONNX Runtime. Discovers available execution providers (CUDA, DirectML,
/// CoreML, CPU) on first access of <see cref="AvailableDevices"/> and
/// caches the result for the process lifetime.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Probe strategy.</strong> CPU is always reported as available
/// (ORT's CPU EP is built-in). The accelerator EPs are probed by attempting
/// to register them on a throwaway <see cref="SessionOptions"/>; success
/// means the native libraries loaded and the runtime accepted the EP, not
/// that the device is fast or capable. The first failure for an EP marks
/// it permanently unavailable for the process.
/// </para>
/// <para>
/// <strong>This is the same logic as the legacy <c>OnnxSessionFactory</c></strong>
/// (which still exists for un-migrated models). When every existing
/// <c>OnnxModel</c> subclass is moved to the dispatcher API, the factory
/// can be retired and this backend becomes the single source of truth for
/// ORT session creation.
/// </para>
/// </remarks>
public sealed class OnnxRuntimeBackend : IInferenceBackend
{
    private IReadOnlyList<InferenceDevice>? _availableDevices;
    private readonly object _probeLock = new();

    /// <inheritdoc />
    public InferenceBackendId Id => InferenceBackendId.OnnxRuntime;

    /// <inheritdoc />
    public IReadOnlyList<InferenceDevice> AvailableDevices
    {
        get
        {
            if (_availableDevices is not null) return _availableDevices;
            lock (_probeLock)
            {
                _availableDevices ??= ProbeDevices();
            }
            return _availableDevices;
        }
    }

    /// <inheritdoc />
    public BackendCompatibility Inspect(BundleManifest bundle)
    {
        // V1: ORT accepts essentially every ONNX model the export tools
        // produce. The real failure surfaces at LoadAsync (missing op,
        // opset out of range). Refine this once we have empirical data
        // about what gets rejected at load time.
        return BackendCompatibility.Supported(estimatedLoadCostMs: 0);
    }

    /// <inheritdoc />
    public ValueTask<IInferenceSession> LoadAsync(
        InferenceLoadRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(request.ModelFilePath))
        {
            throw new FileNotFoundException(
                $"ONNX model file not found at '{request.ModelFilePath}'.",
                request.ModelFilePath);
        }

        SessionOptions options = new();
        ApplyOptimization(options, request.Optimization);
        AttachExecutionProvider(options, request.Device);

        long fileBytes = new FileInfo(request.ModelFilePath).Length;
        long estimatedBytes = ResolveResidentBytes(request, fileBytes);

        InferenceSession ortSession = new(request.ModelFilePath, options);

        OnnxRuntimeSession session = new(ortSession, request.Device, estimatedBytes);
        return new ValueTask<IInferenceSession>(session);
    }

    /// <summary>
    /// Resolves the <see cref="IInferenceSession.EstimatedResidentBytes"/>
    /// value for a session being loaded. Enforces the file-size floor on
    /// the author-declared estimate (rejecting bundles whose
    /// <see cref="InferenceLoadRequest.DeclaredResidentBytes"/> is below
    /// the on-disk file size as misconfigured), then returns
    /// <c>max(declared, 1.5 × file_size)</c>.
    /// </summary>
    private static long ResolveResidentBytes(InferenceLoadRequest request, long fileBytes)
    {
        long defaultEstimate = (long)(fileBytes * 1.5);

        if (request.DeclaredResidentBytes is not long declared)
        {
            return defaultEstimate;
        }

        if (declared < fileBytes)
        {
            throw new InvalidOperationException(
                $"Bundle declares session '{request.SessionName}' resident-bytes = {declared:N0} " +
                $"but the on-disk file at '{request.ModelFilePath}' is {fileBytes:N0} bytes. " +
                "DeclaredResidentBytes must be at least the file size — a bundle declaring less " +
                "is either misconfigured or attempting to under-report memory usage to the " +
                "residency manager. Update the bundle's bundle.json or remove the declaration.");
        }

        return Math.Max(declared, defaultEstimate);
    }

    private static void ApplyOptimization(SessionOptions options, InferenceOptimization level)
    {
        options.GraphOptimizationLevel = level switch
        {
            InferenceOptimization.None       => GraphOptimizationLevel.ORT_DISABLE_ALL,
            InferenceOptimization.Basic      => GraphOptimizationLevel.ORT_ENABLE_BASIC,
            InferenceOptimization.Standard   => GraphOptimizationLevel.ORT_ENABLE_EXTENDED,
            InferenceOptimization.Aggressive => GraphOptimizationLevel.ORT_ENABLE_ALL,
            _                                => GraphOptimizationLevel.ORT_ENABLE_EXTENDED,
        };
    }

    private static void AttachExecutionProvider(SessionOptions options, InferenceDevice device)
    {
        switch (device)
        {
            case InferenceDevice.OnnxRuntimeCpu:
                // CPU EP is built-in; appending nothing is correct.
                break;

            case InferenceDevice.OnnxRuntimeCuda:
                CudaRuntimeProbe.EnsureOnPath();
                options.AppendExecutionProvider_CUDA(0);
                break;

            case InferenceDevice.OnnxRuntimeDirectMl:
                options.AppendExecutionProvider_DML(0);
                break;

            case InferenceDevice.OnnxRuntimeCoreMl:
                options.AppendExecutionProvider("CoreML");
                break;

            default:
                throw new NotSupportedException(
                    $"InferenceDevice {device} is not addressable by the ONNX Runtime backend. " +
                    "OpenVINO devices route through OpenVinoBackend.");
        }
    }

    private static IReadOnlyList<InferenceDevice> ProbeDevices()
    {
        // CPU is always available — it's the EP ORT registers by default
        // when no others are appended.
        List<InferenceDevice> devices = new() { InferenceDevice.OnnxRuntimeCpu };

        if (TryRegister(opts =>
            {
                CudaRuntimeProbe.EnsureOnPath();
                opts.AppendExecutionProvider_CUDA(0);
            }))
        {
            devices.Add(InferenceDevice.OnnxRuntimeCuda);
        }

        if (OperatingSystem.IsWindows() &&
            TryRegister(opts => opts.AppendExecutionProvider_DML(0)))
        {
            devices.Add(InferenceDevice.OnnxRuntimeDirectMl);
        }

        if (OperatingSystem.IsMacOS() &&
            TryRegister(opts => opts.AppendExecutionProvider("CoreML")))
        {
            devices.Add(InferenceDevice.OnnxRuntimeCoreMl);
        }

        return devices;
    }

    /// <summary>
    /// Attempts to register an execution provider on a throwaway
    /// <see cref="SessionOptions"/>. Returns <see langword="true"/> on
    /// success, <see langword="false"/> on any expected failure mode
    /// (DLL missing, provider not built into this ORT binary, device
    /// unavailable). Unexpected exceptions are NOT swallowed.
    /// </summary>
    private static bool TryRegister(Action<SessionOptions> register)
    {
        using SessionOptions probe = new();
        try
        {
            register(probe);
            return true;
        }
        catch (OnnxRuntimeException)        { return false; }
        catch (DllNotFoundException)        { return false; }
        catch (EntryPointNotFoundException) { return false; }
        catch (PlatformNotSupportedException) { return false; }
    }
}
