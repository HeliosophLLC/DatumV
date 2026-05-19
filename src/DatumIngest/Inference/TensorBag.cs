using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Inference;

/// <summary>
/// A named collection of <see cref="IInferenceTensor"/>s — the input or
/// output of a single <see cref="IInferenceSession.RunAsync"/> call. Keys
/// are the tensor names as declared in the model graph; values are the
/// concrete tensor handles.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Backend-aware allocation.</strong> A bag is created by an
/// <see cref="IInferenceSession"/> (via
/// <see cref="IInferenceSession.CreateInputBag"/>) so it can use the
/// backend's allocator and produce tensors of the runtime's native type
/// without a managed-to-native copy.
/// </para>
/// <para>
/// <strong>Ownership.</strong> The bag owns the tensors it holds. Disposing
/// the bag disposes every tensor in it. Engine code that needs to retain a
/// single output tensor past the bag's lifetime must copy the bytes out
/// (typically by calling <see cref="IInferenceTensor.AsSpan{T}"/> and
/// snapshotting into a managed array) before the bag is disposed.
/// </para>
/// </remarks>
public abstract class TensorBag : IDisposable
{
    /// <summary>The number of tensors currently in the bag.</summary>
    public abstract int Count { get; }

    /// <summary>The names of the tensors in the bag, in the order they were added.</summary>
    public abstract IReadOnlyList<string> Names { get; }

    /// <summary>Retrieves a tensor by name. Throws if the name is not present.</summary>
    public abstract IInferenceTensor this[string name] { get; }

    /// <summary>Attempts to retrieve a tensor by name without throwing on miss.</summary>
    public abstract bool TryGet(string name, out IInferenceTensor tensor);

    /// <summary>
    /// Adds a new tensor to the bag, copying the provided data into a
    /// backend-allocated buffer. The returned tensor handle is owned by
    /// the bag and disposed with it.
    /// </summary>
    /// <typeparam name="T">
    /// Element type. Must match <paramref name="elementKind"/>'s scalar
    /// representation — passing <c>float</c> with
    /// <see cref="DataKind.Int64"/> throws.
    /// </typeparam>
    public abstract IInferenceTensor Add<T>(
        string name,
        DataKind elementKind,
        ReadOnlySpan<int> shape,
        ReadOnlySpan<T> data) where T : unmanaged;

    /// <inheritdoc cref="IDisposable.Dispose"/>
    public abstract void Dispose();
}
