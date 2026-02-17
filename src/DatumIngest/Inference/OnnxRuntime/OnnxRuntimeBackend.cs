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
    private IReadOnlyList<DeviceProbeResult>? _probedDevices;
    private IReadOnlyList<InferenceDevice>? _availableDevices;
    private readonly object _probeLock = new();

    /// <inheritdoc />
    public InferenceBackendId Id => InferenceBackendId.OnnxRuntime;

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
            IReadOnlyList<DeviceProbeResult> probed = ProbeAllDevicesCore();
            List<InferenceDevice> available = new(probed.Count);
            foreach (DeviceProbeResult r in probed)
            {
                if (r.Available) available.Add(r.Device);
            }
            _probedDevices = probed;
            _availableDevices = available;
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

    /// <summary>
    /// Probes every device kind this backend recognises and records the
    /// outcome with a human-readable reason on failure. CPU is unconditionally
    /// available — ORT registers it by default when no other EP is appended.
    /// DirectML and CoreML carry platform constraints (Windows / macOS) that
    /// surface as <c>reason</c> rather than a thrown probe.
    /// </summary>
    private static IReadOnlyList<DeviceProbeResult> ProbeAllDevicesCore()
    {
        List<DeviceProbeResult> results = new(4)
        {
            new(InferenceDevice.OnnxRuntimeCpu, Available: true, Reason: ""),
        };

        results.Add(ProbeOne(InferenceDevice.OnnxRuntimeCuda, opts =>
        {
            CudaRuntimeProbe.EnsureOnPath();
            opts.AppendExecutionProvider_CUDA(0);
        }));

        if (OperatingSystem.IsWindows())
        {
            results.Add(ProbeOne(InferenceDevice.OnnxRuntimeDirectMl,
                opts => opts.AppendExecutionProvider_DML(0)));
        }
        else
        {
            results.Add(new(InferenceDevice.OnnxRuntimeDirectMl,
                Available: false, Reason: "DirectML is Windows-only."));
        }

        if (OperatingSystem.IsMacOS())
        {
            results.Add(ProbeOne(InferenceDevice.OnnxRuntimeCoreMl,
                opts => opts.AppendExecutionProvider("CoreML")));
        }
        else
        {
            results.Add(new(InferenceDevice.OnnxRuntimeCoreMl,
                Available: false, Reason: "CoreML is macOS-only."));
        }

        return results;
    }

    /// <summary>
    /// Attempts to register an execution provider on a throwaway
    /// <see cref="SessionOptions"/>. Returns a successful
    /// <see cref="DeviceProbeResult"/> on attach success; on any expected
    /// failure mode (DLL missing, provider not built into this ORT binary,
    /// device unavailable) returns the result tagged unavailable with the
    /// exception's message. Unexpected exceptions are NOT swallowed.
    /// </summary>
    private static DeviceProbeResult ProbeOne(
        InferenceDevice device, Action<SessionOptions> register)
    {
        using SessionOptions probe = new();
        try
        {
            register(probe);
            return new(device, Available: true, Reason: "");
        }
        catch (OnnxRuntimeException ex)        { return new(device, Available: false, Reason: ex.Message); }
        catch (DllNotFoundException ex)        { return new(device, Available: false, Reason: ex.Message); }
        catch (EntryPointNotFoundException ex) { return new(device, Available: false, Reason: ex.Message); }
        catch (PlatformNotSupportedException ex) { return new(device, Available: false, Reason: ex.Message); }
    }
}
