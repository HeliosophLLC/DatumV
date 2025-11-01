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
    private readonly DatumFileSchema _schema;
    private readonly DatumRowGroupDescriptor[] _rowGroups;
    private readonly long _totalRowCount;
    private readonly DatumFileFlags _flags;
    private readonly ulong? _sidecarFingerprint;

    private DatumFileReader(
        FileStream stream,
        DatumFileSchema schema,
        DatumRowGroupDescriptor[] rowGroups,
        long totalRowCount,
        DatumFileFlags flags,
        ulong? sidecarFingerprint)
    {
        _stream = stream;
        _schema = schema;
        _rowGroups = rowGroups;
        _totalRowCount = totalRowCount;
        _flags = flags;
        _sidecarFingerprint = sidecarFingerprint;
    }

    /// <summary>Opens a <c>.datum</c> file and reads its footer and schema.</summary>
    /// <param name="filePath">Absolute path to the <c>.datum</c> file.</param>
    /// <param name="store">Optional value store for zone map DataValues. Creates a new Arena if null.</param>
    /// <exception cref="InvalidDataException">
    /// Thrown when the file header or tail magic bytes do not match the expected values.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when the file format version is not supported by this reader.
    /// </exception>
    public static DatumFileReader Open(string filePath, Model.IValueStore? store = null)
    {
        FileStream stream = File.OpenRead(filePath);

        try
        {
            (DatumFileSchema schema, DatumRowGroupDescriptor[] rowGroups, long totalRowCount,
                DatumFileFlags flags, ulong? sidecarFingerprint) = ReadFooterAndHeader(stream);

            var reader = new DatumFileReader(
                stream, schema, rowGroups, totalRowCount, flags, sidecarFingerprint);

            return reader;
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

    /// <summary>File-level flags read from the header.</summary>
    internal DatumFileFlags Flags => _flags;

    /// <summary>
    /// The 64-bit sidecar fingerprint recorded in the footer when
    /// <see cref="DatumFileFlags.HasSidecarBlobs"/> is set; <see langword="null"/>
    /// otherwise. The companion <c>.datum-blob</c> sidecar must carry the same value
    /// in its header — <see cref="Sidecar.SidecarReadStore"/> does the cross-check on open.
    /// </summary>
    public ulong? SidecarFingerprint => _sidecarFingerprint;

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

        DatumDecoderContext context = new();

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
    /// <param name="columnBatch">The column batch containing pre-allocated column buffers.</param>
    /// <param name="compressedBuffer">
    /// Caller-owned buffer reused for reading compressed page bytes. Must be at least as
    /// large as the largest compressed page across all row groups and columns being read.
    /// Passing a single buffer avoids per-column <see cref="ArrayPool{T}"/> rent/return
    /// churn that leads to Gen 2 deaths from pool trimming.
    /// </param>
    /// <param name="decompressedBuffer">
    /// Caller-owned buffer reused for page decompression output. Must be at least as large
    /// as the largest uncompressed page. Eliminates repeated <c>new byte[]</c> allocations
    /// inside the decompressor.
    /// </param>
    internal void ReadColumnsInto(
        int rowGroupIndex,
        ColumnBatch columnBatch,
        byte[] compressedBuffer,
        byte[] decompressedBuffer)
    {
        DatumRowGroupDescriptor rowGroup = _rowGroups[rowGroupIndex];
        int rowCount = (int)rowGroup.RowCount;
        DatumDecoderContext context = new()
        {
            // Route arena-backed string payloads into the batch's shared arena.
            // Previously the context defaulted Store to a fresh `new Arena()` that was
            // never Disposed — its backing MemoryMappedFile got finalized per row group,
            // costing tens of seconds of finalizer-thread time on large ingests.
            Store = columnBatch.Arena,
        };

        columnBatch.SetRowCount(rowCount);

        ColumnLookup columnLookup = columnBatch.ColumnLookup;
        for (int i = 0; i < columnLookup.Count; i++)
        {
            int columnIndex = columnLookup.GetSchemaColumnIndex(i);
            DatumColumnChunkDescriptor chunk = rowGroup.ColumnChunks[columnIndex];
            DatumColumnDescriptor descriptor = _schema.Columns[columnIndex];
            int compressedLength = (int)chunk.CompressedByteLength;

            DataValue[] columnBuffer = columnBatch.GetColumnBuffer(i);

            _stream.Seek(chunk.PageOffset, SeekOrigin.Begin);
            _stream.ReadExactly(compressedBuffer.AsSpan(0, compressedLength));

            DatumColumnDecoder decoder = DatumDecoderFactory.GetDecoder(descriptor, chunk.Encoding);

            // Pass the full rented buffer — decompressors are frame-aware and
            // ignore trailing bytes beyond the compressed frame.
            decoder.DecodeInto(
                compressedBuffer,
                chunk.Encoding,
                chunk.Compression,
                (int)chunk.UncompressedByteLength,
                rowCount,
                descriptor,
                context,
                columnBuffer,
                payloadLength: compressedLength,
                decompressedBuffer: decompressedBuffer);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _stream.Dispose();

    // ──────────────────── Footer reading ────────────────────

    internal static (DatumFileSchema Schema, DatumRowGroupDescriptor[] RowGroups, long TotalRowCount,
        DatumFileFlags Flags, ulong? SidecarFingerprint)
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

        DatumFileFlags flags = (DatumFileFlags)BinaryPrimitives.ReadUInt16LittleEndian(headerBytes.AsSpan(6));
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
        bool hasTombstones = flags.HasFlag(DatumFileFlags.HasTombstones);
        DatumRowGroupDescriptor[] rowGroups = new DatumRowGroupDescriptor[rowGroupCount];

        for (int groupIndex = 0; groupIndex < (int)rowGroupCount; groupIndex++)
        {
            rowGroups[groupIndex] = DatumRowGroupDescriptor.Deserialize(reader, schema.ColumnCount, hasTombstones);
        }

        // The sidecar fingerprint follows the row group directory when (and only when)
        // the file declares a companion .datum-blob via DatumFileFlags.HasSidecarBlobs.
        // See DatumFileWriter.WriteFooter for the symmetric write side.
        ulong? sidecarFingerprint = null;
        if (flags.HasFlag(DatumFileFlags.HasSidecarBlobs))
        {
            sidecarFingerprint = reader.ReadUInt64();
        }

        return (schema, rowGroups, totalRowCount, flags, sidecarFingerprint);
    }
}
