using System.Runtime.InteropServices;

using Heliosoph.DatumV.Inference;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Inference;

/// <summary>
/// Heap-backed <see cref="TensorBag"/> for unit-testing inference helpers
/// without ONNX Runtime. Stores raw bytes per tensor; <see cref="StubTensor.AsSpan{T}"/>
/// casts via <see cref="MemoryMarshal"/>. Same shape as the (private) stub
/// used inside <c>ModelRegistrationTests</c>; lifted into a shared file so
/// the new <c>InferFunction</c> unit tests can build inputs / outputs
/// against the same primitive without duplicating the stub in every file.
/// </summary>
internal sealed class StubTensorBag : TensorBag
{
    private readonly Dictionary<string, StubTensor> _tensors = new(StringComparer.Ordinal);
    private readonly List<string> _names = new();

    public override int Count => _tensors.Count;
    public override IReadOnlyList<string> Names => _names;
    public override IInferenceTensor this[string name] => _tensors[name];

    public override bool TryGet(string name, out IInferenceTensor tensor)
    {
        if (_tensors.TryGetValue(name, out StubTensor? hit))
        {
            tensor = hit;
            return true;
        }
        tensor = null!;
        return false;
    }

    public override IInferenceTensor Add<T>(
        string name, DataKind elementKind, ReadOnlySpan<int> shape, ReadOnlySpan<T> data)
    {
        byte[] bytes = MemoryMarshal.AsBytes(data).ToArray();
        StubTensor tensor = new(name, elementKind, shape.ToArray(), bytes);
        _tensors[name] = tensor;
        _names.Add(name);
        return tensor;
    }

    public override void Dispose() { /* heap-backed */ }
}

/// <summary>
/// Heap-backed <see cref="IInferenceTensor"/>. Mirrors the private stub
/// inside <c>ModelRegistrationTests</c>; lives here so new tests can use
/// it without re-rolling.
/// </summary>
internal sealed class StubTensor : IInferenceTensor
{
    private readonly byte[] _bytes;

    public StubTensor(string name, DataKind elementKind, int[] shape, byte[] bytes)
    {
        Name = name;
        ElementKind = elementKind;
        Shape = shape;
        _bytes = bytes;
    }

    public string Name { get; }
    public DataKind ElementKind { get; }
    public IReadOnlyList<int> Shape { get; }
    public bool IsResidentOnCpu => true;

    public ReadOnlySpan<T> AsSpan<T>() where T : unmanaged => MemoryMarshal.Cast<byte, T>(_bytes);

    public void Dispose() { /* heap-backed */ }
}
