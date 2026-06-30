using System.Runtime.InteropServices;

using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions;

/// <summary>
/// Managed payload backing a <see cref="DataKind.ListBuilder"/>
/// <see cref="ValueRef"/>: a growable, in-place-mutable accumulator of
/// primitive elements used inside a procedural body. Elements are held in a
/// single contiguous byte buffer that grows by doubling, so <c>APPEND</c> is
/// amortised O(1) rather than the O(K²) copy churn of repeatedly rebuilding an
/// immutable <c>T[]</c> via <c>array_append</c> / <c>array_concat</c>.
/// </summary>
/// <remarks>
/// <para>
/// The buffer is kind-agnostic: the element kind fixes a <see cref="Stride"/>
/// (its <see cref="DataValue.ScalarByteSize"/>) and every append writes a whole
/// number of strides. A single scalar append writes one stride; appending a
/// peer array writes its element bytes verbatim. <see cref="ValueRef.FreezeToArray"/>
/// reinterprets the accumulated bytes as the matching typed array exactly once,
/// at the boundary where the list leaves the body.
/// </para>
/// <para>
/// <strong>Body-local; auto-freezes at materialisation.</strong> A
/// <see cref="ListBuilderValue"/> is an intra-body intermediate; when it reaches
/// a <see cref="ValueRef.ToDataValue"/> boundary it auto-freezes to its
/// <c>Array&lt;T&gt;</c> and materialises that — the array is the list's correct
/// storable form, so freezing (rather than refusing, as <see cref="LambdaValue"/>
/// does) is the SQL-natural promotion.
/// </para>
/// <para>
/// Not thread-safe: a list is owned by the single procedural-body invocation that
/// declared it and never shared across rows.
/// </para>
/// </remarks>
public sealed class ListBuilderValue
{
    private byte[] _buffer;
    private int _byteCount;

    /// <summary>The fixed element kind, set at construction.</summary>
    public DataKind ElementKind { get; }

    /// <summary>
    /// Per-element byte width (<see cref="DataValue.ScalarByteSize"/> of
    /// <see cref="ElementKind"/>). Every append is a multiple of this.
    /// </summary>
    public int Stride { get; }

    /// <summary>
    /// Creates an empty list of <paramref name="elementKind"/>, optionally
    /// pre-sized to hold <paramref name="reserveElements"/> without reallocating.
    /// </summary>
    /// <param name="elementKind">
    /// A fixed-width primitive kind; reference / blob kinds (String, Image,
    /// Struct, …) are rejected via <see cref="DataValue.ScalarByteSize"/>.
    /// </param>
    /// <param name="reserveElements">Initial capacity hint, in elements (≥ 0).</param>
    public ListBuilderValue(DataKind elementKind, int reserveElements = 0)
    {
        // ScalarByteSize throws for kinds with no fixed element width, which is
        // exactly the "primitive element kinds only" guard we want here.
        Stride = DataValue.ScalarByteSize(elementKind);
        ElementKind = elementKind;
        _buffer = reserveElements > 0
            ? new byte[checked((long)reserveElements * Stride)]
            : [];
        _byteCount = 0;
    }

    /// <summary>Number of elements currently in the list.</summary>
    public int Count => _byteCount / Stride;

    /// <summary>Read-only view over the accumulated element bytes (row-major).</summary>
    public ReadOnlySpan<byte> Bytes => _buffer.AsSpan(0, _byteCount);

    /// <summary>
    /// Ensures capacity for at least <paramref name="elementCount"/> elements
    /// without changing <see cref="Count"/>. A one-time O(N) allocation that lets
    /// a body with a known survivor bound skip the doubling-growth reallocations.
    /// </summary>
    public void Reserve(int elementCount)
    {
        if (elementCount <= 0)
        {
            return;
        }
        EnsureCapacity(checked((long)elementCount * Stride));
    }

    /// <summary>
    /// Appends raw element bytes. <paramref name="elementBytes"/>.Length must be a
    /// whole multiple of <see cref="Stride"/> — one stride for a scalar append,
    /// N strides when concatenating a peer array of the same element kind.
    /// </summary>
    public void AppendBytes(ReadOnlySpan<byte> elementBytes)
    {
        if (elementBytes.Length == 0)
        {
            return;
        }
        if (elementBytes.Length % Stride != 0)
        {
            throw new ArgumentException(
                $"Append of {elementBytes.Length} byte(s) is not a whole multiple of the "
                + $"{ElementKind} element stride ({Stride}).",
                nameof(elementBytes));
        }
        EnsureCapacity((long)_byteCount + elementBytes.Length);
        elementBytes.CopyTo(_buffer.AsSpan(_byteCount));
        _byteCount += elementBytes.Length;
    }

    private void EnsureCapacity(long requiredBytes)
    {
        if (requiredBytes <= _buffer.Length)
        {
            return;
        }
        // Doubling growth from a small floor; the freeze copy at the boundary is
        // the only place the accumulated bytes are duplicated.
        long capacity = _buffer.Length == 0 ? Math.Max(requiredBytes, (long)Stride * 4) : _buffer.Length;
        while (capacity < requiredBytes)
        {
            capacity *= 2;
        }
        if (capacity > Array.MaxLength)
        {
            throw new InvalidOperationException(
                $"List of {ElementKind} would exceed the maximum array length "
                + $"({Array.MaxLength} bytes).");
        }
        Array.Resize(ref _buffer, (int)capacity);
    }

    /// <summary>
    /// Copies the accumulated bytes into a freshly allocated typed array of
    /// <typeparamref name="T"/> (which must match <see cref="ElementKind"/>'s
    /// width). The single copy that materialises the list at a body boundary.
    /// </summary>
    internal T[] FreezeTo<T>() where T : unmanaged => MemoryMarshal.Cast<byte, T>(Bytes).ToArray();
}
