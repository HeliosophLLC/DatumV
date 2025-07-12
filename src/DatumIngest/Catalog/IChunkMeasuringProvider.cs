using DatumIngest.Indexing;

namespace DatumIngest.Catalog;

/// <summary>
/// Optional extension of <see cref="ITableProvider"/> for providers whose source
/// format supports byte-level chunking. The provider pre-scans the source file
/// and reports byte ranges for each row chunk, enabling the index builder to
/// record <see cref="IndexChunk.SourceByteOffset"/> and
/// <see cref="IndexChunk.SourceByteLength"/> for later byte-level seeking.
/// </summary>
/// <remarks>
/// Only line-oriented formats (CSV, JSONL) can implement this meaningfully.
/// Binary formats like Parquet and HDF5 use their own internal chunking and
/// are not suited to byte-offset measurement.
/// </remarks>
public interface IChunkMeasuringProvider : ITableProvider
{
    /// <summary>
    /// Scans the source file and returns byte ranges for row chunks of the
    /// specified size. Each <see cref="ChunkByteRange"/> covers exactly
    /// <paramref name="chunkSize"/> rows except possibly the last range,
    /// which may contain fewer rows.
    /// </summary>
    /// <param name="descriptor">Table descriptor identifying the source file.</param>
    /// <param name="chunkSize">Number of data rows per chunk.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Ordered list of chunk byte ranges covering the entire data region of the file.</returns>
    Task<IReadOnlyList<ChunkByteRange>> MeasureChunkByteRangesAsync(
        TableDescriptor descriptor,
        int chunkSize,
        CancellationToken cancellationToken);
}
