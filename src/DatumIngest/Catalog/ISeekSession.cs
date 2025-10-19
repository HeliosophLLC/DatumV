using DatumIngest.Model;

namespace DatumIngest.Catalog;

/// <summary>
/// A scoped, caller-owned handle for repeated seeks into a single table. Opened via
/// <see cref="ITableProvider.OpenSeekSession"/>, disposed when the caller is done.
/// </summary>
/// <remarks>
/// <para>
/// Sessions exist so providers can amortise expensive setup (opening a
/// <c>DatumFileReader</c>, sizing and renting decode scratch buffers, resolving
/// projection metadata) across many <see cref="SeekAsync"/> calls without sharing that
/// state on the provider itself. Provider-scoped state would race across concurrent
/// queries — session-scoped state does not, because each caller owns its session.
/// </para>
/// <para>
/// Sessions are single-threaded. The caller must not interleave <see cref="SeekAsync"/>
/// enumerations on the same session from multiple threads.
/// </para>
/// </remarks>
public interface ISeekSession : IDisposable
{
    /// <summary>
    /// Reads a contiguous range of rows starting at <paramref name="startRow"/>. The
    /// range is bounded by the session's table and the projection it was opened with.
    /// </summary>
    /// <param name="startRow">Zero-based absolute row index into the table.</param>
    /// <param name="count">Maximum number of rows to read. The stream may yield fewer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async enumerable of row batches covering the requested range.</returns>
    IAsyncEnumerable<RowBatch> SeekAsync(long startRow, int count, CancellationToken cancellationToken);
}
