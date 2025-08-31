using System.Buffers;
using System.Buffers.Binary;
using DatumIngest.DatumFile.Decoding;
using DatumIngest.Model;

namespace DatumIngest.DatumFile;

/// <summary>
/// Reads the <c>.datum</c> binary column-store format, loading the schema and row group
/// directory from the file footer on open, then decoding column pages on demand.
/// </summary>
/// <remarks>
/// The reader seeks to the footer via the 8-byte tail sentinel without scanning forward
/// from the beginning. All row group metadata is eagerly loaded; column data is decoded
/// lazily per row group.
/// </remarks>
public sealed class DatumFileReader : IDisposable
{
    private readonly FileStream _stream;
    private readonly string _filePath;
    private readonly DatumFileSchema _schema;
    private readonly DatumRowGroupDescriptor[] _rowGroups;
    private readonly long _totalRowCount;

    private DatumFileReader(
        FileStream stream,
        string filePath,
        DatumFileSchema schema,
        DatumRowGroupDescriptor[] rowGroups,
        long totalRowCount)
    {
        _stream = stream;
        _filePath = filePath;
        _schema = schema;
        _rowGroups = rowGroups;
        _totalRowCount = totalRowCount;
    }

    /// <summary>Opens a <c>.datum</c> file and reads its footer and schema.</summary>
    /// <param name="filePath">Absolute path to the <c>.datum</c> file.</param>
    /// <exception cref="InvalidDataException">
    /// Thrown when the file header or tail magic bytes do not match the expected values.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when the file format version is not supported by this reader.
    /// </exception>
    public static DatumFileReader Open(string filePath)
    {
        FileStream stream = File.OpenRead(filePath);

        try
        {
            (DatumFileSchema schema, DatumRowGroupDescriptor[] rowGroups, long totalRowCount) =
                ReadFooterAndHeader(stream);

            return new DatumFileReader(stream, filePath, schema, rowGroups, totalRowCount);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>The column schema of this file, converted to the query-engine model.</summary>
    public Schema Schema => _schema.ToSchema();

    /// <summary>Number of row groups in this file.</summary>
    public int RowGroupCount => _rowGroups.Length;

    /// <summary>Total number of rows across all row groups.</summary>
    public long TotalRowCount => _totalRowCount;

    /// <summary>The raw schema descriptor, used internally for provider wiring.</summary>
    internal DatumFileSchema FileSchema => _schema;

    /// <summary>
    /// Returns per-column row group statistics for use in zone-map-based predicate pruning.
    /// </summary>
    /// <param name="rowGroupIndex">Zero-based row group index.</param>
    internal DatumRowGroupDescriptor GetRowGroupDescriptor(int rowGroupIndex)
        => _rowGroups[rowGroupIndex];

    /// <summary>
    /// Reads and decodes the specified columns for the given row group.
    /// Returns a parallel array: <c>result[i]</c> is the decoded values for
    /// <c>columnIndices[i]</c>, one <see cref="DataValue"/> per row.
    /// </summary>
    /// <param name="rowGroupIndex">Zero-based row group index.</param>
    /// <param name="columnIndices">
    /// Schema column indices to decode, in any order. Columns not listed are not read.
    /// </param>
    public DataValue[][] ReadColumns(int rowGroupIndex, int[] columnIndices)
    {
        DatumRowGroupDescriptor rowGroup = _rowGroups[rowGroupIndex];
        int rowCount = (int)rowGroup.RowCount;
        DataValue[][] result = new DataValue[columnIndices.Length][];

        DatumDecoderContext context = new() { DatumFilePath = _filePath };

        for (int i = 0; i < columnIndices.Length; i++)
        {
            int columnIndex = columnIndices[i];
            DatumColumnChunkDescriptor chunk = rowGroup.ColumnChunks[columnIndex];
            DatumColumnDescriptor descriptor = _schema.Columns[columnIndex];

            byte[] compressedBytes = new byte[chunk.CompressedByteLength];
            _stream.Seek(chunk.PageOffset, SeekOrigin.Begin);
            _stream.ReadExactly(compressedBytes);

            DatumColumnDecoder decoder = DatumDecoderFactory.GetDecoder(descriptor, chunk.Encoding);
            result[i] = decoder.Decode(
                compressedBytes,
                chunk.Encoding,
                chunk.Compression,
                (int)chunk.UncompressedByteLength,
                rowCount,
                descriptor,
                context);
        }

        return result;
    }

    /// <summary>
    /// Reads and decodes the specified columns into pre-allocated <see cref="DataValue"/> arrays,
    /// avoiding per-row-group allocation of result arrays. Compressed page buffers are rented from
    /// <see cref="ArrayPool{T}"/> and returned immediately after decoding.
    /// </summary>
    /// <param name="rowGroupIndex">Zero-based row group index.</param>
    /// <param name="columnIndices">Schema column indices to decode.</param>
    /// <param name="target">
    /// Pre-allocated column buffers. <c>target[i]</c> must have at least as many elements as the
    /// row group's row count. Reused across row groups to avoid LOH churn.
    /// </param>
    internal void ReadColumnsInto(
        int rowGroupIndex,
        int[] columnIndices,
        DataValue[][] target)
    {
        DatumRowGroupDescriptor rowGroup = _rowGroups[rowGroupIndex];
        int rowCount = (int)rowGroup.RowCount;
        DatumDecoderContext context = new() { DatumFilePath = _filePath };

        for (int i = 0; i < columnIndices.Length; i++)
        {
            int columnIndex = columnIndices[i];
            DatumColumnChunkDescriptor chunk = rowGroup.ColumnChunks[columnIndex];
            DatumColumnDescriptor descriptor = _schema.Columns[columnIndex];

            byte[] compressedBuffer = ArrayPool<byte>.Shared.Rent((int)chunk.CompressedByteLength);
            try
            {
                _stream.Seek(chunk.PageOffset, SeekOrigin.Begin);
                _stream.ReadExactly(compressedBuffer.AsSpan(0, (int)chunk.CompressedByteLength));

                DatumColumnDecoder decoder = DatumDecoderFactory.GetDecoder(descriptor, chunk.Encoding);
                decoder.DecodeInto(
                    compressedBuffer[..(int)chunk.CompressedByteLength],
                    chunk.Encoding,
                    chunk.Compression,
                    (int)chunk.UncompressedByteLength,
                    rowCount,
                    descriptor,
                    context,
                    target[i]);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(compressedBuffer);
            }
        }
    }

    /// <summary>
    /// Reads and decodes the specified columns for the given row group directly into
    /// a <see cref="ColumnBatch"/>.  String and JSON columns are decoded into the
    /// batch's <see cref="StringArena"/> as raw UTF-8 bytes rather than managed strings.
    /// </summary>
    /// <param name="rowGroupIndex">Zero-based row group index.</param>
    /// <param name="columnIndices">Schema column indices to decode.</param>
    /// <param name="columnNames">Projected column names in the same order as <paramref name="columnIndices"/>.</param>
    /// <param name="nameIndex">Case-insensitive name-to-ordinal mapping (shared with caller).</param>
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
        DatumDecoderContext context = new() { DatumFilePath = _filePath };

        for (int i = 0; i < columnIndices.Length; i++)
        {
            int columnIndex = columnIndices[i];
            DatumColumnChunkDescriptor chunk = rowGroup.ColumnChunks[columnIndex];
            DatumColumnDescriptor descriptor = _schema.Columns[columnIndex];

            byte[] compressedBytes = new byte[chunk.CompressedByteLength];
            _stream.Seek(chunk.PageOffset, SeekOrigin.Begin);
            _stream.ReadExactly(compressedBytes);

            DatumColumnDecoder decoder = DatumDecoderFactory.GetDecoder(descriptor, chunk.Encoding);
            decoder.DecodeIntoColumn(
                compressedBytes,
                chunk.Encoding,
                chunk.Compression,
                (int)chunk.UncompressedByteLength,
                rowCount,
                descriptor,
                context,
                batch.GetColumnBuffer(i),
                batch.StringArena,
                batch.DataArena);
        }

        batch.SetRowCount(rowCount);
        return batch;
    }

    /// <inheritdoc/>
    public void Dispose() => _stream.Dispose();

    // ──────────────────── Footer reading ────────────────────

    internal static (DatumFileSchema Schema, DatumRowGroupDescriptor[] RowGroups, long TotalRowCount)
        ReadFooterAndHeader(Stream stream)
    {
        // Validate header magic and read totalRowCount from position 12.
        byte[] headerBytes = new byte[DatumFileConstants.HeaderSize];
        stream.Seek(0, SeekOrigin.Begin);
        stream.ReadExactly(headerBytes);

        if (!headerBytes.AsSpan(0, 4).SequenceEqual(DatumFileConstants.Magic))
        {
            throw new InvalidDataException("File does not have a valid .datum header magic.");
        }

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(headerBytes.AsSpan(4));
        if (version != DatumFileConstants.FormatVersion)
        {
            throw new NotSupportedException(
                $"Unsupported .datum format version {version}. Expected {DatumFileConstants.FormatVersion}.");
        }

        long totalRowCount = BinaryPrimitives.ReadInt64LittleEndian(headerBytes.AsSpan(12));

        // Read tail to locate the footer.
        byte[] tailBytes = new byte[DatumFileConstants.TailSize];
        stream.Seek(-DatumFileConstants.TailSize, SeekOrigin.End);
        stream.ReadExactly(tailBytes);

        if (!tailBytes.AsSpan(4).SequenceEqual(DatumFileConstants.TailMagic))
        {
            throw new InvalidDataException("File does not have a valid .datum tail magic.");
        }

        uint footerByteLength = BinaryPrimitives.ReadUInt32LittleEndian(tailBytes.AsSpan(0));
        long footerOffset = stream.Length - DatumFileConstants.TailSize - footerByteLength;

        byte[] footerBytes = new byte[footerByteLength];
        stream.Seek(footerOffset, SeekOrigin.Begin);
        stream.ReadExactly(footerBytes);

        using MemoryStream footerStream = new(footerBytes);
        using BinaryReader reader = new(footerStream, System.Text.Encoding.UTF8, leaveOpen: true);

        DatumFileSchema schema = DatumFileSchema.Deserialize(reader);
        uint rowGroupCount = reader.ReadUInt32();
        DatumRowGroupDescriptor[] rowGroups = new DatumRowGroupDescriptor[rowGroupCount];

        for (int groupIndex = 0; groupIndex < (int)rowGroupCount; groupIndex++)
        {
            rowGroups[groupIndex] = DatumRowGroupDescriptor.Deserialize(reader, schema.ColumnCount);
        }

        return (schema, rowGroups, totalRowCount);
    }
}
