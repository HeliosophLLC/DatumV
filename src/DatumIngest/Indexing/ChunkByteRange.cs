namespace DatumIngest.Indexing;

/// <summary>
/// Byte-level boundaries of a single row chunk within a line-based source file.
/// Produced by <see cref="Catalog.IChunkMeasuringProvider"/> during a pre-scan pass
/// and consumed by <see cref="SourceIndexBuilder"/> to populate
/// <see cref="IndexChunk.SourceByteOffset"/> and <see cref="IndexChunk.SourceByteLength"/>.
/// </summary>
/// <param name="ByteOffset">Byte offset in the source file where this chunk's first row begins.</param>
/// <param name="ByteLength">Number of bytes spanned by this chunk's rows.</param>
/// <param name="RowCount">Number of data rows in this chunk.</param>
public readonly record struct ChunkByteRange(long ByteOffset, long ByteLength, long RowCount);
