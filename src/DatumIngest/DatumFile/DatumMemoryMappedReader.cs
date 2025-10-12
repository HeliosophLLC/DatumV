using System.IO.MemoryMappedFiles;
using DatumIngest.DatumFile.Compression;
using DatumIngest.DatumFile.Decoding;
using DatumIngest.Model;

namespace DatumIngest.DatumFile;

/// <summary>
/// Reads a <c>.datum</c> file using a memory-mapped view, allowing multiple column pages
/// to be decoded in parallel without contention on a single stream.
/// </summary>
/// <remarks>
/// The file footer is read eagerly on <see cref="Open"/> using a standard <see cref="FileStream"/>.
/// Subsequent column accesses create independent <see cref="MemoryMappedViewStream"/> instances,
/// enabling concurrent decode with <see cref="ReadColumnsParallel"/>.
/// <para>
/// Use <see cref="GetColumnMemory"/> for zero-copy access to <see cref="DatumEncoding.FixedFloat"/>
/// column pages, bypassing <see cref="DataValue"/> boxing entirely.
/// </para>
/// </remarks>
public sealed class DatumMemoryMappedReader : IDisposable
{
    private readonly MemoryMappedFile _mappedFile;
    private readonly string _filePath;
    private readonly DatumFileSchema _schema;
    private readonly DatumRowGroupDescriptor[] _rowGroups;
    private readonly long _totalRowCount;

    /// <summary>
    /// Optional value store for decoding string columns into Arena-backed values.
    /// </summary>
    public IValueStore Store { get; set; } = new Arena();

    private DatumMemoryMappedReader(
        MemoryMappedFile mappedFile,
        string filePath,
        DatumFileSchema schema,
        DatumRowGroupDescriptor[] rowGroups,
        long totalRowCount)
    {
        _mappedFile = mappedFile;
        _filePath = filePath;
        _schema = schema;
        _rowGroups = rowGroups;
        _totalRowCount = totalRowCount;
    }

    /// <summary>
    /// Opens a <c>.datum</c> file and reads its footer and schema into memory.
    /// The file is then mapped for parallel column access.
    /// </summary>
    /// <param name="filePath">Absolute path to the <c>.datum</c> file.</param>
    /// <exception cref="InvalidDataException">
    /// Thrown when the file header or tail magic bytes do not match the expected values.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when the file format version is not supported by this reader.
    /// </exception>
    public static DatumMemoryMappedReader Open(string filePath)
    {
        // Use a temporary FileStream solely to read the compact footer — then
        // create the long-lived MemoryMappedFile for all subsequent data reads.
        (DatumFileSchema schema, DatumRowGroupDescriptor[] rowGroups, long totalRowCount, DatumFileFlags _) =
            ReadFooterFromPath(filePath);

        MemoryMappedFile mappedFile = MemoryMappedFile.CreateFromFile(
            filePath, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);

        return new DatumMemoryMappedReader(mappedFile, filePath, schema, rowGroups, totalRowCount);
    }

    /// <summary>The column schema of this file.</summary>
    public Schema Schema => _schema.ToSchema();

    /// <summary>Number of row groups in this file.</summary>
    public int RowGroupCount => _rowGroups.Length;

    /// <summary>Total number of rows across all row groups.</summary>
    public long TotalRowCount => _totalRowCount;

    /// <summary>
    /// Decodes the specified columns for the given row group using <c>Parallel.For</c>
    /// over the column index list, each column reading from an independent mapped view.
    /// Returns a parallel array: <c>result[i]</c> is the decoded values for
    /// <c>columnIndices[i]</c>, one <see cref="DataValue"/> per row.
    /// </summary>
    /// <param name="rowGroupIndex">Zero-based row group index.</param>
    /// <param name="columnIndices">Schema column indices to decode. May be in any order.</param>
    public DataValue[][] ReadColumnsParallel(int rowGroupIndex, int[] columnIndices)
    {
        DatumRowGroupDescriptor rowGroup = _rowGroups[rowGroupIndex];
        int rowCount = (int)rowGroup.RowCount;
        DataValue[][] result = new DataValue[columnIndices.Length][];

        DatumDecoderContext context = new() { DatumFilePath = _filePath, Store = Store };

        Parallel.For(0, columnIndices.Length, resultIndex =>
        {
            int columnIndex = columnIndices[resultIndex];
            DatumColumnChunkDescriptor chunk = rowGroup.ColumnChunks[columnIndex];
            DatumColumnDescriptor descriptor = _schema.Columns[columnIndex];

            byte[] compressedBytes = ReadPageBytes(chunk);

            DatumColumnDecoder decoder = DatumDecoderFactory.GetDecoder(descriptor, chunk.Encoding);
            result[resultIndex] = decoder.Decode(
                compressedBytes,
                chunk.Encoding,
                chunk.Compression,
                (int)chunk.UncompressedByteLength,
                rowCount,
                descriptor,
                context);
        });

        return result;
    }

    /// <summary>
    /// Decodes the specified columns for the given row group directly into a
    /// <see cref="ColumnBatch"/> using <c>Parallel.For</c> over columns.
    /// Each column decoder writes into the batch's pre-allocated column buffer;
    /// string and binary payloads are decoded into per-column private arenas
    /// and then merged into the batch's shared arenas.
    /// </summary>
    /// <param name="rowGroupIndex">Zero-based row group index.</param>
    /// <param name="columnIndices">Schema column indices to decode.</param>
    /// <param name="columnNames">Projected column names in the same order as <paramref name="columnIndices"/>.</param>
    /// <param name="nameIndex">Case-insensitive name-to-ordinal mapping.</param>
    /// <returns>A <see cref="ColumnBatch"/> with <see cref="ColumnBatch.RowCount"/> set. Caller must dispose.</returns>
    public ColumnBatch ReadColumnsAsColumnBatch(
        int rowGroupIndex,
        int[] columnIndices,
        string[] columnNames,
        Dictionary<string, int> nameIndex)
    {
        DatumRowGroupDescriptor rowGroup = _rowGroups[rowGroupIndex];
        int rowCount = (int)rowGroup.RowCount;

        ColumnBatch batch = ColumnBatch.Create(columnNames, nameIndex, rowCount);
        DatumDecoderContext context = new() { DatumFilePath = _filePath, Store = Store };

        // Each parallel column gets a private arena; after decode completes,
        // its contents are bulk-copied into the batch's shared arena and offsets
        // in the DataValues are adjusted.
        Arena[] perColumnArenas = new Arena[columnIndices.Length];

        Parallel.For(0, columnIndices.Length, resultIndex =>
        {
            Arena localArena = new();
            perColumnArenas[resultIndex] = localArena;

            int columnIndex = columnIndices[resultIndex];
            DatumColumnChunkDescriptor chunk = rowGroup.ColumnChunks[columnIndex];
            DatumColumnDescriptor descriptor = _schema.Columns[columnIndex];

            byte[] compressedBytes = ReadPageBytes(chunk);

            DatumColumnDecoder decoder = DatumDecoderFactory.GetDecoder(descriptor, chunk.Encoding);
            decoder.DecodeIntoColumn(
                compressedBytes,
                chunk.Encoding,
                chunk.Compression,
                (int)chunk.UncompressedByteLength,
                rowCount,
                descriptor,
                context,
                batch.GetColumnBuffer(resultIndex),
                localArena);
        });

        // Merge per-column arenas into the batch's shared arena sequentially.
        for (int i = 0; i < columnIndices.Length; i++)
        {
            Arena localArena = perColumnArenas[i];

            if (localArena.BytesWritten > 0)
            {
                int baseOffset = batch.Arena.CopyFrom(localArena);
                if (baseOffset > 0)
                {
                    ColumnBatch.AdjustArenaOffsets(batch.GetColumnBuffer(i), rowCount, baseOffset);
                }
            }

            localArena.Dispose();
        }

        batch.SetRowCount(rowCount);
        return batch;
    }

    /// <summary>
    /// Decodes a <see cref="DatumEncoding.FixedFloat"/> column page into a contiguous
    /// float array without creating <see cref="DataValue"/> wrappers, providing a
    /// zero-boxing path for embedding and tensor columns.
    /// </summary>
    /// <param name="columnIndex">Schema column index. The column must use <see cref="DatumEncoding.FixedFloat"/>.</param>
    /// <param name="rowGroupIndex">Zero-based row group index.</param>
    /// <returns>
    /// A <see cref="Memory{T}"/> of <c>float</c> containing all elements in row-major order.
    /// Null rows are represented as <see cref="float.NaN"/> blocks at their element positions.
    /// </returns>
    /// <exception cref="NotSupportedException">
    /// Thrown when the column page does not use <see cref="DatumEncoding.FixedFloat"/> encoding.
    /// </exception>
    public Memory<float> GetColumnMemory(int columnIndex, int rowGroupIndex)
    {
        DatumRowGroupDescriptor rowGroup = _rowGroups[rowGroupIndex];
        int rowCount = (int)rowGroup.RowCount;
        DatumColumnChunkDescriptor chunk = rowGroup.ColumnChunks[columnIndex];
        DatumColumnDescriptor descriptor = _schema.Columns[columnIndex];

        if (chunk.Encoding != DatumEncoding.FixedFloat)
        {
            throw new NotSupportedException(
                $"GetColumnMemory requires a FixedFloat column page, but column '{descriptor.Name}' " +
                $"uses {chunk.Encoding} in row group {rowGroupIndex}.");
        }

        byte[] compressedBytes = ReadPageBytes(chunk);
        byte[] raw = DatumCompressor.Decompress(compressedBytes, (int)chunk.UncompressedByteLength, chunk.Compression);

        int bitmapByteCount = DatumNullBitmap.ByteCount(rowCount);
        int floatBytes = raw.Length - bitmapByteCount;

        // All-null page: no float data present.
        if (floatBytes == 0 || rowCount == 0)
        {
            return Memory<float>.Empty;
        }

        int elementsPerRow = descriptor.HasFixedShape
            ? descriptor.ElementsPerRow()
            : floatBytes / (sizeof(float) * rowCount);

        float[] floats = new float[rowCount * elementsPerRow];
        ByteLaneShuffle.Unshuffle(raw.AsSpan(bitmapByteCount), floats);

        return floats.AsMemory();
    }

    /// <inheritdoc/>
    public void Dispose() => _mappedFile.Dispose();

    // ──────────────────── Private helpers ────────────────────

    private static (DatumFileSchema Schema, DatumRowGroupDescriptor[] RowGroups, long TotalRowCount, DatumFileFlags Flags)
        ReadFooterFromPath(string filePath)
    {
        using FileStream stream = File.OpenRead(filePath);
        return DatumFileReader.ReadFooterAndHeader(stream);
    }

    private byte[] ReadPageBytes(DatumColumnChunkDescriptor chunk)
    {
        byte[] buffer = new byte[chunk.CompressedByteLength];
        using MemoryMappedViewStream view = _mappedFile.CreateViewStream(
            chunk.PageOffset, chunk.CompressedByteLength, MemoryMappedFileAccess.Read);
        view.ReadExactly(buffer);
        return buffer;
    }
}
