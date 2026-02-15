using DatumIngest.Model;

namespace DatumIngest.Inference;

/// <summary>
/// A backend-agnostic handle to one tensor — input or output of an
/// <see cref="IInferenceSession"/>. Concrete implementations wrap their
/// runtime's native type (<c>OrtValue</c>, <c>ov::Tensor</c>) and expose it
/// to engine code through this surface.
/// </summary>
/// <remarks>
/// <para>
/// <strong>CPU residency is not guaranteed.</strong> A tensor returned from
/// a GPU/NPU inference call may live in device memory. <see cref="IsResidentOnCpu"/>
/// reports the current state; <see cref="AsSpan{T}"/> triggers a download
/// (and possibly an allocation) when called on a non-CPU tensor. Backends
/// keep results on-device when subsequent ops will consume them on-device,
/// only paying the copy when CPU code actually reads the bytes.
/// </para>
/// <para>
/// <strong>Lifetime.</strong> Tensors are disposable. A <see cref="TensorBag"/>
/// owns the tensors it holds and disposes them when the bag itself is
/// disposed. Engine code that pulls a single result tensor out for longer-
/// lived storage must copy the bytes into managed memory before disposing
/// the bag.
/// </para>
/// </remarks>
public interface IInferenceTensor : IDisposable
{
    /// <summary>The tensor name as declared in the model graph.</summary>
    string Name { get; }

    /// <summary>Engine-side element type. Matches one of the kinds the backend supports.</summary>
    DataKind ElementKind { get; }

    /// <summary>Concrete shape — every dimension is a positive int. No null entries here, unlike <see cref="TensorSpec"/>.</summary>
    IReadOnlyList<int> Shape { get; }

    /// <summary>
    /// Whether the tensor's bytes are addressable from CPU memory right
    /// now. <see langword="false"/> means a backend-managed device buffer;
    /// calling <see cref="AsSpan{T}"/> will trigger a download.
    /// </summary>
    bool IsResidentOnCpu { get; }

    /// <summary>
    /// Read-only view of the tensor's element bytes, flat in row-major
    /// order matching <see cref="Shape"/>. Triggers a device-to-host
    /// download for non-CPU tensors; the result is cached so subsequent
    /// calls are free until the tensor is disposed.
    /// </summary>
    /// <typeparam name="T">
    /// Element type, must match <see cref="ElementKind"/>. Calling
    /// <c>AsSpan&lt;float&gt;()</c> on an <see cref="DataKind.Int64"/>
    /// tensor throws.
    /// </typeparam>
    ReadOnlySpan<T> AsSpan<T>() where T : unmanaged;
}
