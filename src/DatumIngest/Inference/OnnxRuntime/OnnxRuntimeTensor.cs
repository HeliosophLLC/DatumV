using DatumIngest.Model;
using Microsoft.ML.OnnxRuntime;

namespace DatumIngest.Inference.OnnxRuntime;

/// <summary>
/// <see cref="IInferenceTensor"/> implementation wrapping an
/// ONNX Runtime <see cref="OrtValue"/>. Owns the wrapped value and
/// disposes it on <see cref="Dispose"/>.
/// </summary>
/// <remarks>
/// <para>
/// V1 places every tensor in CPU-addressable memory. ORT internally
/// transfers to/from the EP's device during <c>Run</c>; we never see GPU
/// memory addresses at the C# layer. This costs one host↔device copy per
/// tensor at the EP boundary.
/// </para>
/// <para>
/// <strong>When this matters.</strong> For single-stage models the copy
/// is &lt;2% of total inference time and the CPU-resident design is
/// strictly correct. For chained adapters — Florence-2's 4-session
/// pipeline, SD's denoising loop within one MODEL body — the intermediate
/// tensors that <em>could</em> stay on device instead cross CPU at every
/// session boundary. At batch=8 the fraction is still small (~2%) but it
/// compounds: a 100K-row Florence-2 run spends ~5 minutes purely on
/// host↔device copies that IO binding could elide. SD denoising at
/// small batch sizes pushes that fraction to 5–8%.
/// </para>
/// <para>
/// <strong>The path to IO binding is open.</strong> <see cref="IsResidentOnCpu"/>
/// is already a runtime property, not an invariant. A future
/// device-resident <c>OnnxRuntimeDeviceTensor</c> can wrap an OrtValue
/// allocated against the EP's allocator; <see cref="AsSpan{T}"/> would
/// trigger a download on first call and cache. The TensorBag layer would
/// learn a target-device hint at allocation time. The interface contract
/// does not change.
/// </para>
/// <para>
/// Not implementing IO binding yet because: (a) the existing OnnxModel
/// pipeline is CPU-resident and we want one migration at a time; (b) no
/// chained adapter has been profiled in anger yet; (c) the IO-binding
/// API needs the dispatcher to thread device handles through, which lands
/// after the first end-to-end migration. Revisit once Florence-2 or SD
/// is running through the new abstraction at scale.
/// </para>
/// </remarks>
internal sealed class OnnxRuntimeTensor : IInferenceTensor
{
    private readonly OrtValue _value;
    private bool _disposed;

    public OnnxRuntimeTensor(string name, OrtValue value)
    {
        Name = name;
        _value = value;

        OrtTensorTypeAndShapeInfo info = _value.GetTensorTypeAndShape();
        ElementKind = OnnxElementTypes.ToDataKind(info.ElementDataType);

        long[] shapeLongs = info.Shape;
        int[] shape = new int[shapeLongs.Length];
        for (int i = 0; i < shapeLongs.Length; i++)
        {
            shape[i] = checked((int)shapeLongs[i]);
        }
        Shape = shape;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public DataKind ElementKind { get; }

    /// <inheritdoc />
    public IReadOnlyList<int> Shape { get; }

    /// <inheritdoc />
    public bool IsResidentOnCpu => true;

    /// <summary>The wrapped OrtValue. Internal — only the ORT backend touches it.</summary>
    internal OrtValue Value => _value;

    /// <inheritdoc />
    public ReadOnlySpan<T> AsSpan<T>() where T : unmanaged
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // GetTensorDataAsSpan returns a span over the OrtValue's pinned
        // memory. The OrtValue must outlive the span — caller dispose
        // discipline (TensorBag owns this tensor's lifetime) makes that
        // safe.
        return _value.GetTensorDataAsSpan<T>();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _value.Dispose();
    }
}
