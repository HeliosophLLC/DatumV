using DatumIngest.Model;

namespace DatumIngest.Tests;

/// <summary>
/// Test-only helpers for working with <see cref="Row"/>. These deliberately live in the
/// test project because they perform allocations that would be unacceptable on the
/// production hot path.
/// </summary>
internal static class RowExtensions
{
    /// <summary>
    /// Allocates a fresh <see cref="DataValue"/> array, copies the row's values into it, and
    /// returns a new <see cref="Row"/> bound to the same <see cref="ColumnLookup"/>. Use this
    /// when a test needs to keep a row alive after its source <see cref="RowBatch"/> is
    /// returned to the pool.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a shallow copy of the values: arena-backed <see cref="DataValue"/>s still
    /// reference offsets in the original arena. If that arena is also returned to the pool
    /// before the cloned row is read, those payloads dangle. For tests that exercise
    /// arena-backed values across pool returns, use a different strategy (e.g. capture the
    /// values you need into local variables before returning the batch).
    /// </para>
    /// <para>
    /// Per-row allocation makes this unsuitable for production use; the symmetric
    /// production helper <c>Row.Clone()</c> was deliberately removed.
    /// </para>
    /// </remarks>
    public static Row CloneForTest(this Row row)
    {
        DataValue[] copy = new DataValue[row.RawValues.Length];
        row.RawValues.CopyTo(copy.AsSpan());
        return new Row(row.ColumnLookup, copy);
    }

    /// <summary>
    /// Deep-clones a <see cref="Row"/> into <paramref name="retentionStore"/> by
    /// stabilising each value's payload via <see cref="DataValueRetention.Stabilize"/>.
    /// Use when a test needs to read arena-backed values (strings, arrays, JSON, etc.)
    /// after the source <see cref="Pooling.Pool"/> has reclaimed the producing batch's arena.
    /// </summary>
    public static Row CloneForTest(this Row row, IValueStore sourceStore, IValueStore retentionStore)
    {
        DataValue[] copy = new DataValue[row.RawValues.Length];
        for (int i = 0; i < row.RawValues.Length; i++)
        {
            copy[i] = DataValueRetention.Stabilize(row.RawValues[i], sourceStore, retentionStore);
        }
        return new Row(row.ColumnLookup, copy);
    }
}
