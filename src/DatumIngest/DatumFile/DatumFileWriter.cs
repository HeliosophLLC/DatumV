using System.Buffers.Binary;
using DatumIngest.DatumFile.Encoding;
using DatumIngest.Model;

namespace DatumIngest.DatumFile;

/// <summary>
/// Low-level writer for the <c>.datum</c> binary column-store format.
/// Buffers incoming rows, flushes a compressed column page per column when the row
/// group size is reached, then writes the schema + row group directory footer on
/// <see cref="Finalize"/> and patches the file header.
/// </summary>
/// <remarks>
/// The underlying stream must be both writable and seekable so the writer can patch the
/// <c>rowGroupCount</c>, <c>totalRowCount</c>, and <c>footerOffset</c> fields in the header
/// after all data has been written. A <see cref="FileStream"/> opened with
/// <c>FileMode.Create</c> fulfills this requirement.
/// </remarks>
public sealed class DatumFileWriter : IDisposable
{
    private Stream _stream;
    private bool _ownsStream;
    private readonly string? _filePath;

    private DatumFileSchema? _schema;
    private DatumColumnDescriptor[]? _descriptors;
    private List<DataValue>[]? _columnBuffers;
    private int _rowGroupSize = DatumFileConstants.DefaultRowGroupSize;
    private long _totalRowsWritten;
    private readonly List<DatumRowGroupDescriptor> _rowGroupDescriptors = new();
    private long _footerOffset;
    private bool _initialized;
    private bool _finalized;
    private bool _disposed;

    /// <summary>
    /// Writer-owned arena holding verbatim byte copies of every incoming batch's arena.
    /// Each <see cref="WriteRowBatch"/> appends a page; <see cref="_pages"/> tracks the
    /// per-page layout so encoders can resolve page-relative DataValue offsets via
    /// <see cref="Arena.Slice(int, int)"/>. Reset between row groups; disposed with the writer.
    /// </summary>
    private readonly Arena _writerArena;

    /// <summary>
    /// Per-page layout of the current row group's column buffers. Cleared on
    /// row group flush. One entry per appended <see cref="RowBatch"/>.
    /// </summary>
    private readonly List<PageSpan> _pages = new();

    /// <summary>
    /// Initializes a <see cref="DatumFileWriter"/> that writes to an existing seekable stream.
    /// The caller retains ownership of the stream and is responsible for disposing it.
    /// </summary>
    /// <param name="stream">A writable, seekable stream to receive the datum bytes.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="stream"/> is not seekable or writable.</exception>
    public DatumFileWriter(Stream stream)
    {
        if (!stream.CanWrite)
        {
            throw new ArgumentException("Stream must be writable.", nameof(stream));
        }

        if (!stream.CanSeek)
        {
            throw new ArgumentException("Stream must be seekable for header patching.", nameof(stream));
        }

        _stream = stream;
        _ownsStream = false;
        _writerArena = new Arena(owner: this);
    }

    /// <summary>
    /// Initializes a <see cref="DatumFileWriter"/> that creates and writes to the specified file.
    /// The writer opens the file stream during <see cref="Initialize"/> and disposes it on <see cref="Finalize"/>.
    /// </summary>
    /// <param name="filePath">Absolute path to the <c>.datum</c> file to create.</param>
    public DatumFileWriter(string filePath)
    {
        _filePath = filePath;
        // _stream will be opened in Initialize once we know the file path is valid.
        _stream = Stream.Null;
        _ownsStream = false;
        _writerArena = new Arena(owner: this);
    }

    /// <summary>
    /// Overrides the row group size used for flushing.
    /// Must be called before <see cref="Initialize"/>. Intended for testing only.
    /// </summary>
    /// <param name="rowGroupSize">The maximum number of rows per row group.</param>
    internal void SetRowGroupSize(int rowGroupSize) => _rowGroupSize = rowGroupSize;

    /// <summary>
    /// Initializes the writer with a schema and writes the file header.
    /// Must be called exactly once before any calls to <see cref="WriteRowBatch"/>.
    /// </summary>
    /// <param name="schema">The schema describing the columns to be written.</param>
    /// <exception cref="InvalidOperationException">Thrown when already initialized.</exception>
    public void Initialize(DatumFileSchema schema)
    {
        if (_initialized)
        {
            throw new InvalidOperationException("DatumFileWriter is already initialized.");
        }

        _schema = schema;

        // Mutable working copies of column descriptors so we can freeze shapes on first flush.
        _descriptors = new DatumColumnDescriptor[schema.ColumnCount];
        for (int index = 0; index < schema.ColumnCount; index++)
        {
            _descriptors[index] = schema.Columns[index];
        }

        _columnBuffers = new List<DataValue>[schema.ColumnCount];
        for (int index = 0; index < schema.ColumnCount; index++)
        {
            _columnBuffers[index] = new List<DataValue>(_rowGroupSize);
        }

        if (_filePath is not null)
        {
            string? directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _stream = new FileStream(_filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 65_536, FileOptions.SequentialScan);
            _ownsStream = true;
        }

        WriteHeader();
        _initialized = true;
    }

    /// <summary>
    /// Appends a batch of rows to the current row group. The batch's arena bytes are
    /// copied verbatim into the writer's arena as a new page, and the batch's
    /// <see cref="DataValue"/>s are appended to the column buffers with their original
    /// page-relative offsets preserved. The caller may dispose or return
    /// <paramref name="batch"/> immediately after this call — the writer retains no
    /// reference to its arena.
    /// </summary>
    /// <param name="batch">The batch of rows to append.</param>
    public void WriteRowBatch(RowBatch batch)
    {
        ThrowIfNotReady();

        if (batch.Count == 0) return;

        // Copy the batch's arena bytes into the writer's arena as a page. If the
        // batch never wrote any reference data, pageLength is zero and the encoders'
        // per-page slices resolve no offsets — still correct.
        int pageLength = batch.Arena.BytesWritten;
        int pageBase = pageLength > 0 ? _writerArena.CopyFrom(batch.Arena) : 0;

        int rowStart = _columnBuffers![0].Count;
        _pages.Add(new PageSpan(rowStart, batch.Count, pageBase, pageLength));

        for (int rowIndex = 0; rowIndex < batch.Count; rowIndex++)
        {
            Row row = batch[rowIndex];
            for (int columnIndex = 0; columnIndex < _descriptors!.Length; columnIndex++)
            {
                _columnBuffers[columnIndex].Add(row[columnIndex]);
            }
        }

        if (_columnBuffers[0].Count >= _rowGroupSize)
        {
            FlushRowGroup();
        }
    }

    /// <summary>
    /// Flushes any remaining buffered rows, writes the footer and tail, and patches the header.
    /// Returns the total number of bytes written to the stream (including header, footer, and tail).
    /// </summary>
    /// <returns>Total bytes written.</returns>
    /// <exception cref="InvalidOperationException">Thrown when not initialized or already finalized.</exception>
    public long Finalize()
    {
        ThrowIfNotReady();
        _finalized = true;

        if (_columnBuffers![0].Count > 0)
        {
            FlushRowGroup();
        }

        WriteFooter();
        PatchHeader();
        _stream.Flush();

        long bytesWritten = _stream.Position;

        if (_ownsStream)
        {
            _stream.Dispose();
            _ownsStream = false;
        }

        // Footer is already written — the writer arena is no longer needed.
        _writerArena.Dispose();

        return bytesWritten;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_ownsStream)
        {
            _stream.Dispose();
            _ownsStream = false;
        }

        _writerArena.Dispose();
    }

    // ──────────────────── Row group flush ────────────────────

    private void FlushRowGroup()
    {
        int rowCount = _columnBuffers![0].Count;
        int columnCount = _descriptors!.Length;

        FreezeFixedShapes();

        DatumEncoderContext context = new()
        {
            DatumFilePath = _filePath ?? string.Empty,
            RowGroupIndex = _rowGroupDescriptors.Count,
            Store = _writerArena,
            Pages = _pages,
        };

        // Encode all columns in parallel — encoders are stateless singletons and
        // compression uses [ThreadStatic] pools, so concurrent Encode calls are safe.
        DatumEncodedPage[] pages = new DatumEncodedPage[columnCount];
        Parallel.For(0, columnCount, columnIndex =>
        {
            DatumColumnDescriptor descriptor = _descriptors[columnIndex];
            DatumColumnEncoder encoder = DatumEncoderFactory.GetEncoder(descriptor);
            pages[columnIndex] = encoder.Encode(_columnBuffers[columnIndex], descriptor, context);
        });

        // Write encoded pages sequentially — stream offsets must be ordered.
        DatumColumnChunkDescriptor[] chunks = new DatumColumnChunkDescriptor[columnCount];
        for (int columnIndex = 0; columnIndex < columnCount; columnIndex++)
        {
            DatumEncodedPage page = pages[columnIndex];
            long pageOffset = _stream.Position;
            _stream.Write(page.Payload);

            chunks[columnIndex] = new DatumColumnChunkDescriptor(
                pageOffset,
                (uint)page.Payload.Length,
                (uint)page.UncompressedByteLength,
                page.Encoding,
                page.Compression,
                page.ZoneMap);
        }

        DatumRowGroupDescriptor rowGroupDescriptor = new((uint)rowCount, chunks);
        _rowGroupDescriptors.Add(rowGroupDescriptor);
        _totalRowsWritten += rowCount;

        foreach (List<DataValue> buffer in _columnBuffers)
        {
            buffer.Clear();
        }

        _pages.Clear();

        // Release the row group's page data. Zone maps are already materialized as
        // managed primitives on each row group descriptor, so nothing in the arena
        // needs to survive the flush.
        _writerArena.Reset();

        CheckAutoTune(chunks);
    }

    /// <summary>
    /// Infers and freezes fixed shapes for Vector/Matrix/Tensor columns on the first row group.
    /// After the first flush the shape is encoded in the descriptor and used for all subsequent pages.
    /// </summary>
    private void FreezeFixedShapes()
    {
        // Only needed on the first flush — after that descriptors are already frozen.
        if (_rowGroupDescriptors.Count > 0) return;

        for (int columnIndex = 0; columnIndex < _descriptors!.Length; columnIndex++)
        {
            DatumColumnDescriptor descriptor = _descriptors[columnIndex];

            if (descriptor.HasFixedShape) continue;

            bool isFloatKind = descriptor.Kind is DataKind.Vector or DataKind.Matrix or DataKind.Tensor;
            if (!isFloatKind) continue;

            List<DataValue> buffer = _columnBuffers![columnIndex];

            // Walk pages in order and use each page's store to resolve the first non-null shape.
            foreach (PageSpan page in _pages)
            {
                IValueStore pageStore = page.ArenaLength > 0
                    ? _writerArena.Slice(page.ArenaBase, page.ArenaLength)
                    : _writerArena;

                int endRow = page.RowStart + page.RowCount;
                int[]? shape = null;

                for (int rowIndex = page.RowStart; rowIndex < endRow; rowIndex++)
                {
                    DataValue value = buffer[rowIndex];
                    if (value.IsNull) continue;

                    shape = ExtractShape(value, pageStore);
                    break;
                }

                if (shape is not null)
                {
                    DatumColumnFlags updatedFlags = descriptor.Flags | DatumColumnFlags.FixedShape;
                    _descriptors[columnIndex] = descriptor with { Flags = updatedFlags, FixedShape = shape };
                    break;
                }
            }
        }
    }

    private static int[]? ExtractShape(DataValue value, IValueStore store)
    {
        return value.Kind switch
        {
            DataKind.Vector => [value.AsVector(store).Length],
            DataKind.Matrix => ExtractMatrixShape(value, store),
            DataKind.Tensor => ExtractTensorShape(value, store),
            _ => null
        };
    }

    private static int[] ExtractMatrixShape(DataValue value, IValueStore store)
    {
        value.AsMatrix(store, out int rows, out int columns);
        return [rows, columns];
    }

    private static int[] ExtractTensorShape(DataValue value, IValueStore store)
    {
        value.AsTensor(store, out int[] shape);
        return shape;
    }

    private void CheckAutoTune(DatumColumnChunkDescriptor[] chunks)
    {
        for (int columnIndex = 0; columnIndex < chunks.Length; columnIndex++)
        {
            if (chunks[columnIndex].Encoding == DatumEncoding.FixedFloat &&
                chunks[columnIndex].UncompressedByteLength > DatumFileConstants.LargePageAutoTuneThresholdBytes)
            {
                _rowGroupSize = Math.Max(DatumFileConstants.MinimumRowGroupSize, _rowGroupSize / 2);

                foreach (List<DataValue> buffer in _columnBuffers!)
                {
                    buffer.Capacity = _rowGroupSize;
                }

                return;
            }
        }
    }

    // ──────────────────── Header / footer writing ────────────────────

    private void WriteHeader()
    {
        // 28-byte header with zero placeholders for the three fields patched in PatchHeader.
        // Layout: magic(4) | version(2) | flags(2) | rowGroupCount(4) | totalRowCount(8) | footerOffset(8)
        byte[] header = new byte[DatumFileConstants.HeaderSize];
        DatumFileConstants.Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), DatumFileConstants.FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)DatumFileFlags.None);
        // Positions 8–27 are patched by PatchHeader after writing is complete.
        _stream.Write(header);
    }

    private void WriteFooter()
    {
        _footerOffset = _stream.Position;

        // Rebuild the schema from the finalized (shape-frozen) descriptor array.
        DatumFileSchema finalSchema = new(_descriptors!);

        using BinaryWriter writer = new(_stream, System.Text.Encoding.UTF8, leaveOpen: true);
        finalSchema.Serialize(writer);
        writer.Write((uint)_rowGroupDescriptors.Count);

        foreach (DatumRowGroupDescriptor rowGroupDescriptor in _rowGroupDescriptors)
        {
            rowGroupDescriptor.Serialize(writer);
        }

        writer.Flush();
        long footerEndOffset = _stream.Position;
        uint footerByteLength = (uint)(footerEndOffset - _footerOffset);

        // Tail: footerByteLength(4) | tailMagic(4)
        writer.Write(footerByteLength);
        writer.Write(DatumFileConstants.TailMagic.ToArray());
        writer.Flush();
    }

    private void PatchHeader()
    {
        long restorePosition = _stream.Position;

        // Seek to offset 8: skip magic(4) + version(2) + flags(2).
        _stream.Seek(8, SeekOrigin.Begin);

        byte[] patch = new byte[20]; // rowGroupCount(4) + totalRowCount(8) + footerOffset(8)
        BinaryPrimitives.WriteUInt32LittleEndian(patch.AsSpan(0), (uint)_rowGroupDescriptors.Count);
        BinaryPrimitives.WriteInt64LittleEndian(patch.AsSpan(4), _totalRowsWritten);
        BinaryPrimitives.WriteInt64LittleEndian(patch.AsSpan(12), _footerOffset);
        _stream.Write(patch);

        _stream.Seek(restorePosition, SeekOrigin.Begin);
    }

    // ──────────────────── Helpers ────────────────────

    private void ThrowIfNotReady()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("Call Initialize() before writing.");
        }

        if (_finalized)
        {
            throw new InvalidOperationException("DatumFileWriter has already been finalized.");
        }
    }
}
