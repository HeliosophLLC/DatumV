namespace Heliosoph.DatumV.Indexing.BTree.MutableBytes;

/// <summary>
/// A single entry in a bytes-keyed B+Tree, mapping an opaque byte-array key
/// to the chunk and row offset where the source value lives. Mirrors
/// <see cref="Heliosoph.DatumV.Indexing.ValueIndexEntry"/> but with the key
/// already encoded by <see cref="Heliosoph.DatumV.Indexing.CompositeKeyEncoder"/>
/// (so the tree never needs to know about <c>DataValue</c>).
/// </summary>
/// <param name="Key">
/// The encoded key bytes. Sort order is determined by
/// <see cref="MemoryExtensions.SequenceCompareTo{T}(System.ReadOnlySpan{T}, System.ReadOnlySpan{T})"/>;
/// the encoder is responsible for producing a memcmp-orderable representation.
/// </param>
/// <param name="ChunkIndex">Zero-based index of the chunk containing this value.</param>
/// <param name="RowOffsetInChunk">Zero-based row offset within the chunk.</param>
public readonly record struct BytesIndexEntry(byte[] Key, int ChunkIndex, long RowOffsetInChunk);
