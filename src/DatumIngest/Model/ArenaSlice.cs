namespace DatumIngest.Model;

/// <summary>
/// A read-only view over a sub-region ("page") of an <see cref="Arena"/>.
/// All <c>Retrieve*</c> calls forward to the backing arena with the page base
/// offset added to <c>p0</c>. Write methods throw — slices exist to resolve
/// page-relative offsets copied verbatim from another arena, not to append data.
/// </summary>
/// <remarks>
/// Used by the .datum writer so encoders can see the same offsets that originally
/// existed in a source <see cref="RowBatch"/>'s arena without requiring the bytes
/// to be rewritten after <see cref="Arena.CopyFrom"/>.
/// </remarks>
public sealed class ArenaSlice : IValueStore
{
    private readonly Arena _arena;
    private readonly int _base;
    private readonly int _length;

    internal ArenaSlice(Arena arena, int pageBase, int pageLength)
    {
        _arena = arena;
        _base = pageBase;
        _length = pageLength;
    }

    /// <summary>The byte offset within the backing arena at which this slice begins.</summary>
    public int Base => _base;

    /// <summary>The length in bytes of this slice.</summary>
    public int Length => _length;

    // ───────────────────────── Reads (page-relative) ─────────────────────────

    /// <inheritdoc />
    public string RetrieveString(int p0, int p1) => _arena.GetString(_base + p0, p1);

    /// <inheritdoc />
    public ReadOnlySpan<byte> RetrieveUtf8Span(int p0, int p1) => _arena.GetSpan(_base + p0, p1);

    /// <inheritdoc />
    public byte[] RetrieveBytes(int p0, int p1) => _arena.MaterializeBytes(_base + p0, p1);

    /// <inheritdoc />
    public float[] RetrieveFloats(int p0, int p1) => _arena.MaterializeFloats(_base + p0, p1);

    /// <inheritdoc />
    public float[] RetrieveTensor(int p0, int p1, out int[] shape) => _arena.MaterializeTensor(_base + p0, p1, out shape);

    /// <inheritdoc />
    public DataValue[] RetrieveDataValues(int p0, int p1) => _arena.RetrieveDataValues(_base + p0, p1);

    /// <inheritdoc />
    public object RetrieveObject(int p0, int p1) => _arena.RetrieveObject(p0, p1);

    // ───────────────────────── Writes (forbidden) ─────────────────────────

    /// <inheritdoc />
    public (int P0, int P1) StoreString(string value) => ThrowWriteNotSupported();

    /// <inheritdoc />
    public (int P0, int P1) StoreUtf8(ReadOnlySpan<byte> utf8) => ThrowWriteNotSupported();

    /// <inheritdoc />
    public (int P0, int P1) StoreChars(ReadOnlySpan<char> chars) => ThrowWriteNotSupported();

    /// <inheritdoc />
    public (int P0, int P1) StoreBytes(ReadOnlySpan<byte> bytes) => ThrowWriteNotSupported();

    /// <inheritdoc />
    public (int P0, int P1) StoreFloats(ReadOnlySpan<float> floats) => ThrowWriteNotSupported();

    /// <inheritdoc />
    public (int P0, int P1) StoreTensor(ReadOnlySpan<float> data, ReadOnlySpan<int> shape) => ThrowWriteNotSupported();

    /// <inheritdoc />
    public (int P0, int P1) StoreDataValues(ReadOnlySpan<DataValue> values) => ThrowWriteNotSupported();

    /// <inheritdoc />
    public (int P0, int P1) StoreObject(object value) => ThrowWriteNotSupported();

    private static (int, int) ThrowWriteNotSupported() =>
        throw new NotSupportedException(
            "ArenaSlice is a read-only view. To write, use the backing arena directly.");
}
