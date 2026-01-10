using DatumIngest.Model;

namespace DatumIngest.Catalog;

/// <summary>
/// One row's worth of updates passed to
/// <see cref="ITableProvider.UpdateRows"/>. Names a target row by its
/// linear position in the table's live row sequence (post-tombstone,
/// matching a fresh <c>SELECT * FROM table</c> emission order) and
/// supplies a sparse column-index → new-value map.
/// </summary>
/// <param name="LiveRowIndex">
/// 0-based linear index over the table's live rows. Must be in
/// <c>[0, GetRowCount-after-tombstones)</c>.
/// </param>
/// <param name="NewValues">
/// Sparse column-index → new-value map. Columns absent from the map
/// keep their existing values. The provider is responsible for resolving
/// non-inline <see cref="DataValue"/> payloads against the
/// <c>sourceStore</c> passed alongside the request list.
/// </param>
public sealed record RowUpdateRequest(
    long LiveRowIndex,
    IReadOnlyDictionary<int, DataValue> NewValues);
