using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Indexing;

/// <summary>
/// A single entry in a column index, mapping a key value to the chunk and row
/// offset where it appears. Shared across sorted, B+Tree, and bitmap index layouts.
/// </summary>
/// <param name="Key">The indexed column value.</param>
/// <param name="ChunkIndex">Zero-based index of the chunk containing this value.</param>
/// <param name="RowOffsetInChunk">Zero-based row offset within the chunk.</param>
public readonly record struct ValueIndexEntry(DataValue Key, int ChunkIndex, long RowOffsetInChunk);
