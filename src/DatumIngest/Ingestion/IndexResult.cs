using DatumIngest.Indexing;
using DatumIngest.Model;

namespace DatumIngest.Ingestion;

/// <summary>
/// The result of indexing a single <c>.datum</c> file.
/// </summary>
/// <param name="OutputPath">Path to the written <c>.datum-index</c> file.</param>
/// <param name="RowCount">Total number of rows observed during the build.</param>
/// <param name="ChunkCount">Number of index chunks produced.</param>
/// <param name="BytesWritten">Size of the written <c>.datum-index</c> file in bytes.</param>
/// <param name="Schema">Cached schema of the indexed table.</param>
/// <param name="Fingerprint">
/// Striped SHA-256 fingerprint of the input <c>.datum</c> file, used at query time to
/// detect staleness when the source changes.
/// </param>
/// <param name="IndexedColumns">Columns that actually received any index type.</param>
/// <param name="BloomColumns">Columns with a bloom filter, or empty if none.</param>
/// <param name="SortedColumns">Columns with a sorted or B+Tree index, or empty if none.</param>
/// <param name="BitmapColumns">Columns with a bitmap index, or empty if none.</param>
/// <param name="DeferredReindexColumns">
/// Columns whose hinted index type failed during the scan (e.g. bitmap hint exceeded
/// cardinality, sorted hint exceeded max string length). Empty when no hints were given
/// or all hints were respected.
/// </param>
/// <param name="Elapsed">Wall time of the index build, including fingerprint and serialization.</param>
public sealed record IndexResult(
    string OutputPath,
    long RowCount,
    int ChunkCount,
    long BytesWritten,
    Schema Schema,
    SourceFingerprint Fingerprint,
    IReadOnlyList<string> IndexedColumns,
    IReadOnlyList<string> BloomColumns,
    IReadOnlyList<string> SortedColumns,
    IReadOnlyList<string> BitmapColumns,
    IReadOnlyList<string> DeferredReindexColumns,
    TimeSpan Elapsed);
