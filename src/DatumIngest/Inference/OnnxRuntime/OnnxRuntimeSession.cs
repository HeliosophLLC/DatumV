using Microsoft.ML.OnnxRuntime;

namespace DatumIngest.Inference.OnnxRuntime;

/// <summary>
/// <see cref="IInferenceSession"/> implementation wrapping an ONNX Runtime
/// <see cref="InferenceSession"/>. Reads its input/output signature from
/// the loaded session metadata at construction; dispatch is per-call via
/// the session's <c>Run</c> overload that takes a name-to-OrtValue
/// dictionary and a list of output names.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Synchronous Run wrapped on a thread pool.</strong> ORT's
/// synchronous <c>Run</c> blocks the calling thread until the EP finishes.
/// We wrap it with <see cref="Task.Run(Action)"/> so the caller's async
/// chain stays unblocked. ORT 1.20 also exposes <c>RunAsync</c>, but its
/// API requires pre-allocated output OrtValues which is impractical for
/// dynamic-shape models — the simpler path serves us until profiling
/// shows the threadpool hop matters.
/// </para>
/// <para>
/// <strong>EstimatedResidentBytes.</strong> ORT does not expose the
/// session's true memory footprint. We use the on-disk file size as a
/// floor and multiply by 1.5× as a fudge factor for activation arenas and
/// EP-internal buffers. Models with very large dynamic activation shapes
/// (SDXL at 1024×1024) will under-estimate; consumers can override per-
/// session at construction time when they have better information.
/// </para>
/// </remarks>
internal sealed class OnnxRuntimeSession : IInferenceSession
{
    private readonly InferenceSession _session;
    private bool _disposed;

    public OnnxRuntimeSession(
        InferenceSession session,
        InferenceDevice device,
        long estimatedResidentBytes)
    {
        _session = session;
        Device = device;
        EstimatedResidentBytes = estimatedResidentBytes;

        Inputs  = BuildSpecs(session.InputMetadata);
        Outputs = BuildSpecs(session.OutputMetadata);
    }

    /// <inheritdoc />
    public IReadOnlyList<TensorSpec> Inputs { get; }

    /// <inheritdoc />
    public IReadOnlyList<TensorSpec> Outputs { get; }

    /// <inheritdoc />
    public InferenceBackendId Backend => InferenceBackendId.OnnxRuntime;

    /// <inheritdoc />
    public InferenceDevice Device { get; }

    /// <inheritdoc />
    public long EstimatedResidentBytes { get; }

    /// <inheritdoc />
    public TensorBag CreateInputBag() => new OnnxRuntimeTensorBag();

    /// <inheritdoc />
    public async ValueTask<TensorBag> RunAsync(TensorBag inputs, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (inputs is not OnnxRuntimeTensorBag ortBag)
        {
            throw new ArgumentException(
                $"Expected an OnnxRuntimeTensorBag; got {inputs.GetType().Name}. " +
                "Use IInferenceSession.CreateInputBag() to allocate a backend-compatible bag.",
                nameof(inputs));
        }

        Dictionary<string, OrtValue> inputDict = new(ortBag.Count, StringComparer.Ordinal);
        foreach (string name in ortBag.Names)
        {
            OnnxRuntimeTensor t = (OnnxRuntimeTensor)ortBag[name];
            inputDict[name] = t.Value;
        }

        string[] outputNames = new string[Outputs.Count];
        for (int i = 0; i < Outputs.Count; i++) outputNames[i] = Outputs[i].Name;

        // RunOptions wraps a native handle that must be disposed; leaving
        // it to finalization (the original bug) leaked one native handle
        // per invocation and after ~hundreds-of-thousands of calls ORT's
        // allocator faulted inside Run with a raw 0xC0000005.
        //
        // Bridge the .NET CancellationToken onto RunOptions.Terminate so
        // a co-operative cancel actually reaches the executing kernel.
        // The registration runs synchronously on cancel; the lambda flips
        // Terminate true and ORT's per-kernel cancellation polling tears
        // the in-flight Run down. The registration is disposed inside the
        // same scope as runOptions, so ORT never sees Terminate flip
        // after the Run has already returned.
        using RunOptions runOptions = new();
        using CancellationTokenRegistration ctReg = cancellationToken.Register(
            static state => ((RunOptions)state!).Terminate = true,
            runOptions);

        // Hop to the thread pool so the EP's blocking Run doesn't tie up
        // the caller's async context.
        using IDisposableReadOnlyCollection<OrtValue> ortOutputs = await Task.Run(
            () => _session.Run(runOptions, inputDict, outputNames),
            cancellationToken).ConfigureAwait(false);
            
        // Materialise every output tensor into managed memory before the
        // ortOutputs `using` disposes the underlying OrtValues. The bag
        // we return only holds managed copies — no shared lifetime with
        // ORT-owned native handles.
        OnnxRuntimeTensorBag outputBag = new();
        int idx = 0;
        foreach (OrtValue value in ortOutputs)
        {
            outputBag.AdoptMaterialized(outputNames[idx++], value);
        }

        return outputBag;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Dispose();
    }

    private static IReadOnlyList<TensorSpec> BuildSpecs(IReadOnlyDictionary<string, NodeMetadata> metadata)
    {
        TensorSpec[] specs = new TensorSpec[metadata.Count];
        int i = 0;
        foreach ((string name, NodeMetadata md) in metadata)
        {
            int[]? dims = md.Dimensions;
            int?[] shape = dims is null
                ? Array.Empty<int?>()
                : new int?[dims.Length];

            if (dims is not null)
            {
                for (int d = 0; d < dims.Length; d++)
                {
                    // ORT uses -1 for dynamic dimensions.
                    shape[d] = dims[d] < 0 ? null : dims[d];
                }
            }

            specs[i++] = new TensorSpec(
                name,
                OnnxElementTypes.ToDataKind(OnnxElementTypesForType(md.ElementType)),
                shape);
        }
        return specs;
    }

    /// <summary>
    /// Maps the System.Type ORT exposes in NodeMetadata.ElementType back to
    /// a TensorElementType. ORT's metadata uses managed Type rather than
    /// the enum because the enum was internal in older versions; this
    /// translation lets us reuse <see cref="OnnxElementTypes"/> uniformly.
    /// </summary>
    private static Microsoft.ML.OnnxRuntime.Tensors.TensorElementType OnnxElementTypesForType(Type t)
    {
        if (t == typeof(float))     return Microsoft.ML.OnnxRuntime.Tensors.TensorElementType.Float;
        if (t == typeof(byte))      return Microsoft.ML.OnnxRuntime.Tensors.TensorElementType.UInt8;
        if (t == typeof(sbyte))     return Microsoft.ML.OnnxRuntime.Tensors.TensorElementType.Int8;
        if (t == typeof(ushort))    return Microsoft.ML.OnnxRuntime.Tensors.TensorElementType.UInt16;
        if (t == typeof(short))     return Microsoft.ML.OnnxRuntime.Tensors.TensorElementType.Int16;
        if (t == typeof(int))       return Microsoft.ML.OnnxRuntime.Tensors.TensorElementType.Int32;
        if (t == typeof(long))      return Microsoft.ML.OnnxRuntime.Tensors.TensorElementType.Int64;
        if (t == typeof(bool))      return Microsoft.ML.OnnxRuntime.Tensors.TensorElementType.Bool;
        if (t == typeof(Half))      return Microsoft.ML.OnnxRuntime.Tensors.TensorElementType.Float16;
        // ORT exposes its own Float16 wrapper type here, not System.Half.
        if (t == typeof(Microsoft.ML.OnnxRuntime.Float16)) return Microsoft.ML.OnnxRuntime.Tensors.TensorElementType.Float16;
        if (t == typeof(double))    return Microsoft.ML.OnnxRuntime.Tensors.TensorElementType.Double;
        if (t == typeof(uint))      return Microsoft.ML.OnnxRuntime.Tensors.TensorElementType.UInt32;
        if (t == typeof(ulong))     return Microsoft.ML.OnnxRuntime.Tensors.TensorElementType.UInt64;
        throw new NotSupportedException(
            $"ORT NodeMetadata exposes element type {t.FullName} which has no DatumIngest mapping.");
    }
}
