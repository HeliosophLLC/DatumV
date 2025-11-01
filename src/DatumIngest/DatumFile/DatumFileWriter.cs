using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using DatumIngest.DatumFile.Encoding;
using DatumIngest.DatumFile.Sidecar;
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
    private int _rowGroupByteThreshold = DatumFileConstants.RowGroupArenaByteThreshold;
    private bool _serialColumnEncoding;
    private long _totalRowsWritten;
    private readonly List<DatumRowGroupDescriptor> _rowGroupDescriptors = new();
    private long _footerOffset;
    private bool _initialized;
    private bool _finalized;

    /// <summary>
    /// Optional companion sidecar store. When attached before <see cref="Initialize"/>,
    /// image-column descriptors are rewritten with <see cref="DatumColumnFlags.SidecarBlobs"/>;
    /// if the sidecar gets materialised during ingest, the file header's
    /// <see cref="DatumFileFlags.HasSidecarBlobs"/> flag is set and the footer carries
    /// the sidecar fingerprint for read-time verification.
    /// </summary>
    private SidecarWriteStore? _sidecar;

    /// <summary>
    /// Writer-owned arena holding verbatim byte copies of every incoming batch's arena.
    /// Each <see cref="WriteRowBatch"/> appends a page; <see cref="_pages"/> tracks the
    /// per-page layout so encoders can resolve page-relative DataValue offsets via
    /// <see cref="Arena.Slice(int, int)"/>. Reset between row groups; disposed with the writer.
    /// </summary>
    private Arena? _writerArena;

    /// <summary>
    /// Read access to the writer's arena for consumers that need to resolve
    /// offsets copied into it (e.g. statistics accumulators whose local sketches
    /// retained DataValues from earlier batches in the current row group).
    /// Only valid between row-group resets — do not retain references past a
    /// <see cref="FlushRowGroup"/> call.
    /// </summary>
    public Arena WriterArena
    {
        get
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            return _writerArena;
        }
    }

    /// <summary>
    /// Per-page layout of the current row group's column buffers. Cleared on
    /// row group flush. One entry per appended <see cref="RowBatch"/>.
    /// </summary>
    private readonly List<PageSpan> _pages = new();

    /// <summary>
    /// Set when <see cref="WriteRowBatch"/> has driven the column buffers to the
    /// row group target size. The next <see cref="WriteRowBatch"/> call throws
    /// until <see cref="FlushRowGroup"/> resets this flag.
    /// </summary>
    private bool _pendingFlush;

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
        _writerArena = new Arena();
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
        _writerArena = new Arena();
    }

    /// <summary>
    /// Gets a value indicating whether the writer has been disposed. Disposed writers should
    /// not be written to or finalized; they have already released their resources.
    /// </summary>
    [MemberNotNullWhen(false, nameof(_writerArena))]
    public bool Disposed { get; private set; }

    /// <summary>
    /// Overrides the row group size used for flushing.
    /// Must be called before <see cref="Initialize"/>. Intended for testing only.
    /// </summary>
    /// <param name="rowGroupSize">The maximum number of rows per row group.</param>
    internal void SetRowGroupSize(int rowGroupSize) => _rowGroupSize = rowGroupSize;

    /// <summary>
    /// Configures the writer's memory tradeoffs. Lower <paramref name="rowGroupByteThreshold"/>
    /// and <paramref name="serialColumnEncoding"/> = <c>true</c> reduce peak working set
    /// at the cost of more row groups and slower encode wall time. Must be called before
    /// <see cref="Initialize"/>.
    /// </summary>
    public void SetMemoryBudget(int rowGroupByteThreshold, bool serialColumnEncoding)
    {
        if (_initialized)
        {
            throw new InvalidOperationException("Memory budget must be configured before Initialize.");
        }

        _rowGroupByteThreshold = rowGroupByteThreshold;
        _serialColumnEncoding = serialColumnEncoding;
    }

    /// <summary>
    /// Attaches the companion sidecar store. Must be called before <see cref="Initialize"/>.
    /// On <see cref="Initialize"/>, every image-column descriptor in the schema gets the
    /// <see cref="DatumColumnFlags.SidecarBlobs"/> flag, instructing the encoder to emit
    /// pointer-only pages whose <c>(offset, length)</c> values point into the sidecar.
    /// On <see cref="Finalize"/>, if the sidecar was actually written to, the file header's
    /// <see cref="DatumFileFlags.HasSidecarBlobs"/> flag is set and the footer records
    /// the sidecar's 64-bit fingerprint so the reader can verify the pair.
    /// </summary>
    public void AttachSidecar(SidecarWriteStore sidecar)
    {
        if (_initialized)
        {
            throw new InvalidOperationException("Sidecar must be attached before Initialize.");
        }

        _sidecar = sidecar;
    }

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
        // When a sidecar is attached, image columns get rewritten with the SidecarBlobs flag
        // so the encoder routes them through the pointer-only page path. Schema is the
        // single source of truth — see BinaryColumnEncoder.EncodeSidecar.
        _descriptors = new DatumColumnDescriptor[schema.ColumnCount];
        for (int index = 0; index < schema.ColumnCount; index++)
        {
            DatumColumnDescriptor src = schema.Columns[index];
            _descriptors[index] = (_sidecar is not null && src.Kind == DataKind.Image && !src.UsesSidecar)
                ? src with { Flags = src.Flags | DatumColumnFlags.SidecarBlobs }
                : src;
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
    /// <returns>
    /// A <see cref="WriteHandle"/> exposing a read-only <see cref="ArenaSlice"/>
    /// over the just-copied page (for post-write consumers like statistics collectors)
    /// and a <see cref="WriteHandle.RequiresFlush"/> flag. When the flag is <c>true</c>
    /// the caller must call <see cref="FlushRowGroup"/> before the next
    /// <see cref="WriteRowBatch"/> — otherwise that call throws.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a previous call returned <see cref="WriteHandle.RequiresFlush"/>
    /// = <c>true</c> and <see cref="FlushRowGroup"/> has not yet been called.
    /// </exception>
    public WriteHandle WriteRowBatch(RowBatch batch)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        ThrowIfNotReady();

        if (_pendingFlush)
        {
            throw new InvalidOperationException(
                "The previous WriteRowBatch filled the row group. Call FlushRowGroup() before writing again.");
        }

        if (batch.Count == 0) return new WriteHandle(null, requiresFlush: false);

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

        ArenaSlice? pageStore = pageLength > 0 ? _writerArena.Slice(pageBase, pageLength) : null;

        // Flush when either (a) the row count reaches the target row-group size, or
        // (b) the writer's arena bytes exceed a byte-based soft cap. The byte cap
        // protects image and large-blob ingestion, where 64k rows of ~150 KB payload
        // each would hold ~9 GB in memory before a row-count-only trigger fires.
        _pendingFlush = _columnBuffers[0].Count >= _rowGroupSize
            || _writerArena.BytesWritten >= _rowGroupByteThreshold;
        return new WriteHandle(pageStore, _pendingFlush);
    }

    /// <summary>
    /// Flushes any remaining buffered rows, writes the footer and tail, and patches the header.
    /// Returns the total number of bytes written to the stream (including header, footer, and tail).
    /// </summary>
    /// <returns>Total bytes written.</returns>
    /// <exception cref="InvalidOperationException">Thrown when not initialized or already finalized.</exception>
    public long Finalize()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        ThrowIfNotReady();

        if (_columnBuffers![0].Count > 0)
        {
            FlushRowGroup();
        }

        _finalized = true;

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
        if (Disposed) return;

        if (_ownsStream)
        {
            _stream.Dispose();
            _ownsStream = false;
        }

        _writerArena.Dispose();
        _writerArena = null;

        Disposed = true;
    }

    // ──────────────────── Row group flush ────────────────────

    /// <summary>
    /// Encodes and writes the currently buffered rows as a row group, then resets
    /// the column buffers and writer arena so subsequent batches can be appended.
    /// Safe to call with a non-full buffer; callers typically invoke it in response
    /// to <see cref="WriteHandle.RequiresFlush"/>.
    /// </summary>
    public void FlushRowGroup()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        ThrowIfNotReady();

        int rowCount = _columnBuffers![0].Count;
        if (rowCount == 0)
        {
            _pendingFlush = false;
            return;
        }

        int columnCount = _descriptors!.Length;

        FreezeFixedShapes();

        DatumEncoderContext context = new()
        {
            Store = _writerArena,
            Pages = _pages,
        };

        // Encode all columns in parallel — encoders are stateless singletons and
        // compression uses [ThreadStatic] pools, so concurrent Encode calls are safe.
        // Serial mode is used by memory-constrained callers to cap peak at one
        // column's encode buffer instead of N in flight concurrently.
        DatumEncodedPage[] pages = new DatumEncodedPage[columnCount];
        if (_serialColumnEncoding)
        {
            for (int columnIndex = 0; columnIndex < columnCount; columnIndex++)
            {
                DatumColumnDescriptor descriptor = _descriptors[columnIndex];
                DatumColumnEncoder encoder = DatumEncoderFactory.GetEncoder(descriptor);
                pages[columnIndex] = encoder.Encode(_columnBuffers[columnIndex], descriptor, context);
            }
        }
        else
        {
            Parallel.For(0, columnCount, columnIndex =>
            {
                DatumColumnDescriptor descriptor = _descriptors[columnIndex];
                DatumColumnEncoder encoder = DatumEncoderFactory.GetEncoder(descriptor);
                pages[columnIndex] = encoder.Encode(_columnBuffers[columnIndex], descriptor, context);
            });
        }

        // Write encoded pages sequentially — stream offsets must be ordered.
        DatumColumnChunkDescriptor[] chunks = new DatumColumnChunkDescriptor[columnCount];
        for (int columnIndex = 0; columnIndex < columnCount; columnIndex++)
        {
            DatumEncodedPage page = pages[columnIndex];
            long pageOffset = _stream.Position;
            int payloadLength = page.PayloadLength;
            _stream.Write(page.Payload);

            chunks[columnIndex] = new DatumColumnChunkDescriptor(
                pageOffset,
                (uint)payloadLength,
                (uint)page.UncompressedByteLength,
                page.Encoding,
                page.Compression,
                page.ZoneMap);

            // Return the pooled payload buffer now that the bytes are safely on the stream.
            page.ReturnBuffer();
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
        _pendingFlush = false;

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
                    ? WriterArena.Slice(page.ArenaBase, page.ArenaLength)
                    : WriterArena;

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

                // Leave column-buffer capacity untouched — shrinking via
                // List<T>.Capacity = smaller forces a reallocation of the backing array
                // per column, which dominates allocations when auto-tune fires. The
                // over-sized buffers retain their initial allocation and will be reused
                // at the new (smaller) row-group size without further allocation.

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

        // Sidecar fingerprint follows the row group directory when (and only when) the
        // attached sidecar was actually written to. Reader pairs this against the
        // .datum-blob header to detect stale or swapped sidecar files. The header flag
        // DatumFileFlags.HasSidecarBlobs is patched in PatchHeader.
        if (_sidecar is { WasMaterialized: true })
        {
            writer.Write(_sidecar.Fingerprint);
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

        // Patch the file flags (offset 6) so HasSidecarBlobs reflects whether the
        // attached sidecar was actually materialised during this run.
        DatumFileFlags fileFlags = DatumFileFlags.None;
        if (_sidecar is { WasMaterialized: true })
        {
            fileFlags |= DatumFileFlags.HasSidecarBlobs;
        }

        _stream.Seek(6, SeekOrigin.Begin);
        Span<byte> flagsPatch = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(flagsPatch, (ushort)fileFlags);
        _stream.Write(flagsPatch);

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
