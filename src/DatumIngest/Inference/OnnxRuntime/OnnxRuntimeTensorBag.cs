using System.Runtime.InteropServices;
using DatumIngest.Model;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace DatumIngest.Inference.OnnxRuntime;

/// <summary>
/// <see cref="TensorBag"/> implementation backed by a list of
/// <see cref="OnnxRuntimeTensor"/>s. Adding a tensor allocates an
/// <see cref="OrtValue"/> on the default CPU allocator and copies the
/// caller's bytes in.
/// </summary>
/// <remarks>
/// Insertion-ordered so callers can recover the order they added inputs
/// (handy for the <c>session.Run</c> name-to-value dictionary). Disposing
/// the bag disposes every tensor it owns.
/// </remarks>
internal sealed class OnnxRuntimeTensorBag : TensorBag
{
    private readonly Dictionary<string, OnnxRuntimeTensor> _tensors = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();
    private bool _disposed;

    /// <inheritdoc />
    public override int Count => _tensors.Count;

    /// <inheritdoc />
    public override IReadOnlyList<string> Names => _order;

    /// <inheritdoc />
    public override IInferenceTensor this[string name] =>
        _tensors.TryGetValue(name, out OnnxRuntimeTensor? t)
            ? t
            : throw new KeyNotFoundException($"Tensor '{name}' not present in this bag.");

    /// <inheritdoc />
    public override bool TryGet(string name, out IInferenceTensor tensor)
    {
        if (_tensors.TryGetValue(name, out OnnxRuntimeTensor? t))
        {
            tensor = t;
            return true;
        }
        tensor = null!;
        return false;
    }

    /// <inheritdoc />
    public override IInferenceTensor Add<T>(
        string name, DataKind elementKind, ReadOnlySpan<int> shape, ReadOnlySpan<T> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_tensors.ContainsKey(name))
        {
            throw new ArgumentException($"Tensor '{name}' is already present in this bag.", nameof(name));
        }

        TensorElementType ortType = OnnxElementTypes.ToOnnxElementType(elementKind);

        long[] shapeLongs = new long[shape.Length];
        long expected = 1;
        for (int i = 0; i < shape.Length; i++)
        {
            shapeLongs[i] = shape[i];
            expected *= shape[i];
        }
        if (expected != data.Length)
        {
            throw new ArgumentException(
                $"Data length {data.Length} does not match shape {string.Join('x', shape.ToArray())} = {expected} elements.",
                nameof(data));
        }

        // Allocate ORT-managed tensor + copy data in. CreateAllocatedTensorValue
        // returns an OrtValue whose memory is owned by ORT; we get a Span<T>
        // view to fill, then it's ready for Run() without additional pinning.
        OrtValue value = OrtValue.CreateAllocatedTensorValue(
            OrtAllocator.DefaultInstance, ortType, shapeLongs);

        Span<T> dest = MemoryMarshal.Cast<byte, T>(value.GetTensorMutableRawData());
        data.CopyTo(dest);

        OnnxRuntimeTensor tensor = new(name, value);
        _tensors.Add(name, tensor);
        _order.Add(name);
        return tensor;
    }

    /// <summary>
    /// Adopts an <see cref="OrtValue"/> produced by the ORT session (e.g.
    /// from <c>session.Run</c>) into this bag. Internal — only the ORT
    /// session uses this when constructing the output bag.
    /// </summary>
    internal void Adopt(string name, OrtValue value)
    {
        OnnxRuntimeTensor tensor = new(name, value);
        _tensors.Add(name, tensor);
        _order.Add(name);
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (OnnxRuntimeTensor t in _tensors.Values)
        {
            t.Dispose();
        }
        _tensors.Clear();
        _order.Clear();
    }
}
