namespace DatumIngest.Model;

/// <summary>
/// Strongly-typed offset into an <see cref="IValueStore"/> — typically a byte offset
/// into an <see cref="Arena"/>'s backing buffer or an index into an object-registry
/// store. Wraps a 64-bit integer with no implicit conversion to/from <see cref="int"/> /
/// <see cref="long"/>, so the compiler enforces explicit handling at every boundary.
/// 64-bit width lifts the prior 2GB arena cap; the storage shape inside a
/// <see cref="DataValue"/> still spans <c>_p0</c>+<c>_p1</c> as today.
/// </summary>
/// <remarks>
/// Use <see cref="Zero"/> for the empty / unused slot (e.g. the second word of
/// <see cref="IValueStore.StoreObject"/>, which doesn't carry a length).
/// </remarks>
public readonly record struct ArenaOffset(long Value)
{
    /// <summary>The zero offset. Equivalent to <c>new ArenaOffset(0)</c>.</summary>
    public static ArenaOffset Zero => default;
}

/// <summary>
/// Strongly-typed second-word companion to <see cref="ArenaOffset"/>. Semantics
/// depend on the calling <see cref="IValueStore"/> method: byte length for
/// strings / byte arrays / tensors, element count for typed float and
/// <see cref="DataValue"/> arrays, unused (0) for object-registry slots.
/// </summary>
/// <remarks>
/// Parallel to <see cref="ArenaOffset"/>: wraps a 64-bit integer with no implicit
/// conversion. 1 TiB+ arenas and 4B-row typed arrays are addressable; the
/// <see cref="DataValue"/> 32-byte layout encodes this back into a 40-bit on-struct
/// field, capping per-value length at ~1 TiB.
/// </remarks>
public readonly record struct ArenaLength(long Value)
{
    /// <summary>The zero length. Equivalent to <c>new ArenaLength(0)</c>.</summary>
    public static ArenaLength Zero => default;
}
