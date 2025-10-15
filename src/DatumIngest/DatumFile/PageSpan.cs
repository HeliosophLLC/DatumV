using DatumIngest.Model;

namespace DatumIngest.DatumFile;

/// <summary>
/// Describes a contiguous slice of rows within a column buffer that all share
/// the same page of arena data inside the writer's <see cref="DatumIngest.Model.Arena"/>.
/// </summary>
/// <remarks>
/// <para>
/// When a <see cref="DatumIngest.Model.RowBatch"/> is appended to the writer, its
/// arena bytes are copied verbatim into the writer's arena at <see cref="ArenaBase"/>.
/// The <see cref="DataValue"/>s in the column buffers retain their original
/// batch-relative offsets — they are resolved at encode time by looking up the
/// page's <see cref="ArenaBase"/> and slicing the writer arena accordingly.
/// </para>
/// <para>
/// <see cref="RowStart"/> and <see cref="RowCount"/> describe which entries in the
/// column buffer belong to this page. They are indexed into the flat per-column
/// <c>List&lt;DataValue&gt;</c> held by the writer.
/// </para>
/// </remarks>
/// <param name="RowStart">Zero-based index of the first row in the column buffer that belongs to this page.</param>
/// <param name="RowCount">Number of rows in this page.</param>
/// <param name="ArenaBase">Byte offset within the writer's arena at which this page's data begins.</param>
/// <param name="ArenaLength">Length in bytes of this page's region in the writer's arena.</param>
internal readonly record struct PageSpan(
    int RowStart,
    int RowCount,
    int ArenaBase,
    int ArenaLength);
