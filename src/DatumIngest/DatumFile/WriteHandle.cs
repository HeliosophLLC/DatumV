using DatumIngest.Model;

namespace DatumIngest.DatumFile;

/// <summary>
/// Handle returned by <see cref="DatumFileWriter.WriteRowBatch"/> describing the
/// page just appended to the writer's arena. Consumers (e.g. statistics collectors)
/// can read the batch's DataValues through <see cref="PageStore"/> after the
/// source <see cref="RowBatch"/> has been returned to its pool.
/// </summary>
/// <remarks>
/// <para>
/// When <see cref="RequiresFlush"/> is <c>true</c> the writer's in-memory row group
/// has reached its target size; the caller must call
/// <see cref="DatumFileWriter.FlushRowGroup"/> before the next
/// <see cref="DatumFileWriter.WriteRowBatch"/> — otherwise that call throws.
/// </para>
/// </remarks>
public readonly struct WriteHandle
{
    /// <summary>
    /// Read-only view over the region of the writer's arena that this batch's
    /// DataValues resolve against. <c>null</c> when the batch stored no reference
    /// data (purely inline types).
    /// </summary>
    public ArenaSlice? PageStore { get; }

    /// <summary>
    /// <c>true</c> when the row group has reached its target size and the caller
    /// must call <see cref="DatumFileWriter.FlushRowGroup"/> before writing again.
    /// </summary>
    public bool RequiresFlush { get; }

    internal WriteHandle(ArenaSlice? pageStore, bool requiresFlush)
    {
        PageStore = pageStore;
        RequiresFlush = requiresFlush;
    }
}
