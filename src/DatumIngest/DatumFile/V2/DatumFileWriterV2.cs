using DatumIngest.DatumFile.Sidecar;
using DatumIngest.DatumFile.V2.Encoding;
using DatumIngest.Model;

namespace DatumIngest.DatumFile.V2;

/// <summary>
/// V2 <c>.datum</c> writer. Streams <see cref="RowBatch"/>es into per-column
/// 1024-row pages, writes each page to disk as it flushes, accumulates
/// the zone-map hierarchy in memory, and emits a footer + tail on
/// <see cref="FinalizeWriter"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>On-disk layout deviation from spec:</strong> the spec calls for
/// column-major page layout (all of column 0's pages, then column 1's,
/// …). To enable streaming writes (incremental file growth visible to
/// users, bounded memory, partial output preserved on cancel), pages are
/// instead emitted in the order they flush — which interleaves columns
/// (page 0 of col 0, page 0 of col 1, …, page 0 of col N-1, page 1 of
/// col 0, …). The reader uses absolute offsets from the page directory
/// in the footer, so layout is invisible to correctness; only sequential
/// per-column scan loses some locality. A follow-up could re-introduce
/// column-major layout via per-column temp files when measurements
/// justify it.
/// </para>
/// <para>
/// The writer requires a seekable stream because it patches
/// <c>totalRowCount</c> and <c>footerOffset</c> in the file header on
/// finalize.
/// </para>
/// </remarks>
public sealed class DatumFileWriterV2 : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly IBlobSink? _sidecar;
    private readonly bool _ownsSidecar;

    private ColumnDescriptorV2[]? _columns;
    private IPageEncoderV2[]? _encoders;
    private List<PageDescriptorV2>[]? _pageDirectory;
    private ZoneMapHierarchyBuilderV2[]? _hierarchies;
    private int _pageSize = DatumFormatV2.DefaultPageSize;
    private long _totalRowsWritten;
    private bool _initialized;
    private bool _finalized;
    private bool _disposed;

    /// <summary>
    /// Creates a writer that opens (and disposes) the given .datum file
    /// path. If <paramref name="sidecarPath"/> is non-null a
    /// <see cref="SidecarWriteStore"/> is created lazily; the file is
    /// only materialised on the first non-inline payload.
    /// </summary>
    public DatumFileWriterV2(string datumPath, string? sidecarPath)
    {
        ArgumentNullException.ThrowIfNull(datumPath);

        string? directory = Path.GetDirectoryName(datumPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _stream = new FileStream(
            datumPath,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 65_536,
            FileOptions.SequentialScan);
        _ownsStream = true;

        if (sidecarPath is not null)
        {
            _sidecar = new SidecarWriteStore(sidecarPath);
            _ownsSidecar = true;
        }
    }

    /// <summary>
    /// Creates a writer over an existing seekable stream and an optional
    /// blob sink. The caller retains ownership of both.
    /// </summary>
    public DatumFileWriterV2(Stream datumStream, IBlobSink? sidecar)
    {
        if (!datumStream.CanWrite)
        {
            throw new ArgumentException("Stream must be writable.", nameof(datumStream));
        }
        if (!datumStream.CanSeek)
        {
            throw new ArgumentException("Stream must be seekable for header patching.", nameof(datumStream));
        }
        _stream = datumStream;
        _ownsStream = false;
        _sidecar = sidecar;
        _ownsSidecar = false;
    }

    /// <summary>
    /// Override the page size before <see cref="Initialize"/>. Test-only;
    /// production writers should leave this at
    /// <see cref="DatumFormatV2.DefaultPageSize"/> so pages align with
    /// <c>ExecutionContext.BatchSize</c>.
    /// </summary>
    internal void SetPageSize(int pageSize)
    {
        if (_initialized)
        {
            throw new InvalidOperationException("Page size must be set before Initialize.");
        }
        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, "Page size must be positive.");
        }
        _pageSize = pageSize;
    }

    /// <summary>
    /// Initializes the writer with the column schema. Reserves header
    /// space at offset 0 (zeros, patched at finalize) and prepares one
    /// encoder + zone-map builder per column.
    /// </summary>
    public void Initialize(IReadOnlyList<ColumnDescriptorV2> columns)
    {
        if (_initialized) throw new InvalidOperationException("Writer already initialized.");
        if (columns.Count == 0) throw new ArgumentException("At least one column required.", nameof(columns));

        _columns = columns.ToArray();
        _encoders = new IPageEncoderV2[_columns.Length];
        _pageDirectory = new List<PageDescriptorV2>[_columns.Length];
        _hierarchies = new ZoneMapHierarchyBuilderV2[_columns.Length];

        for (int i = 0; i < _columns.Length; i++)
        {
            _encoders[i] = PageEncoderFactoryV2.Create(_columns[i], _pageSize);
            _pageDirectory[i] = new List<PageDescriptorV2>();
            _hierarchies[i] = new ZoneMapHierarchyBuilderV2();
        }

        // Reserve header bytes (placeholder zeros). Patched at finalize.
        Span<byte> headerScratch = stackalloc byte[DatumFormatV2.HeaderSize];
        new HeaderV2(
            DatumFileFlagsV2.None,
            ColumnCount: _columns.Length,
            PageSize: _pageSize,
            TotalRowCount: 0,
            FooterOffset: 0).WriteTo(headerScratch);
        _stream.Write(headerScratch);

        _initialized = true;
    }

    /// <summary>
    /// Appends a batch of rows. Each column encoder absorbs one value per
    /// row; pages flush automatically when their row count hits
    /// <see cref="DatumFormatV2.DefaultPageSize"/>.
    /// </summary>
    public void WriteRowBatch(RowBatch batch)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized) throw new InvalidOperationException("Initialize before WriteRowBatch.");
        if (_finalized) throw new InvalidOperationException("Writer is finalized.");

        if (batch.Count == 0) return;

        if (batch.ColumnLookup.Count != _columns!.Length)
        {
            throw new InvalidOperationException(
                $"Row batch column count ({batch.ColumnLookup.Count}) does not match writer schema ({_columns.Length}).");
        }

        IValueStore store = batch.Arena;

        for (int rowIndex = 0; rowIndex < batch.Count; rowIndex++)
        {
            Row row = batch[rowIndex];
            for (int colIndex = 0; colIndex < _columns.Length; colIndex++)
            {
                IPageEncoderV2 encoder = _encoders![colIndex];
                encoder.Append(row[colIndex], store, _sidecar);
                if (encoder.IsFull)
                {
                    FlushPage(colIndex);
                }
            }
        }

        _totalRowsWritten += batch.Count;

        // Flush the underlying stream so the bytes we just wrote land on
        // disk promptly. Without this, FileStream's internal 64 KiB buffer
        // can hold pages for arbitrary wall time even though our writer
        // already produced them — making `ls -l` and ingest progress
        // invisible to the user, and losing all in-flight data on cancel.
        _stream.Flush();
    }

    /// <summary>
    /// Flushes any partial trailing pages, then writes the footer + tail
    /// and patches the header. After this call the file is
    /// closed-ready (Dispose still flushes / closes the stream).
    /// </summary>
    public void FinalizeWriter()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized) throw new InvalidOperationException("Writer was never initialized.");
        if (_finalized) return;

        // Flush trailing partial pages.
        for (int colIndex = 0; colIndex < _columns!.Length; colIndex++)
        {
            if (_encoders![colIndex].RowCount > 0)
            {
                FlushPage(colIndex);
            }
        }

        // Pages have already been streamed to disk in the order they
        // flushed (page 0 col 0, page 0 col 1, …, page 1 col 0, …); the
        // page directory carries each page's absolute file offset, so the
        // reader doesn't depend on layout.

        // Compose column footers + zone-map hierarchies.
        bool emitVolumes = _totalRowsWritten > DatumFormatV2.VolumeEmitRowThreshold;
        var columnFooters = new ColumnFooterV2[_columns.Length];
        for (int colIndex = 0; colIndex < _columns.Length; colIndex++)
        {
            (IReadOnlyList<DatumZoneMap> chapters, IReadOnlyList<DatumZoneMap>? volumes) =
                _hierarchies![colIndex].Finalize(emitVolumes);

            columnFooters[colIndex] = new ColumnFooterV2(
                _columns[colIndex],
                _pageDirectory![colIndex],
                chapters,
                volumes);
        }

        DatumFileFlagsV2 flags = DatumFileFlagsV2.None;
        if (emitVolumes) flags |= DatumFileFlagsV2.HasVolumeZoneMaps;
        if (HasAnySidecarReferences()) flags |= DatumFileFlagsV2.HasSidecarReferences;

        FooterV2 footer = new(columnFooters, emitVolumes);

        // Write the footer body, capture offset and length.
        long footerOffset = _stream.Position;
        using (MemoryStream footerScratch = new())
        using (BinaryWriter footerWriter = new(footerScratch, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            footer.Serialize(footerWriter);
            footerWriter.Flush();
            footerScratch.Position = 0;
            footerScratch.CopyTo(_stream);
            uint footerLength = checked((uint)footerScratch.Length);

            // Tail: footerByteLength(4) + tailMagic(4) = 8.
            Span<byte> tail = stackalloc byte[DatumFormatV2.TailSize];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(tail[..4], footerLength);
            DatumFormatV2.TailMagic.CopyTo(tail[4..]);
            _stream.Write(tail);
        }

        // Patch header.
        _stream.Position = 0;
        Span<byte> headerScratch = stackalloc byte[DatumFormatV2.HeaderSize];
        new HeaderV2(
            flags,
            ColumnCount: _columns.Length,
            PageSize: _pageSize,
            TotalRowCount: _totalRowsWritten,
            FooterOffset: footerOffset).WriteTo(headerScratch);
        _stream.Write(headerScratch);
        _stream.Flush();

        _finalized = true;
    }

    /// <summary>
    /// Closes the underlying datum file and (if owned) the sidecar
    /// store. Does not finalize the file — call
    /// <see cref="FinalizeWriter"/> first to produce a readable file.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_ownsStream)
        {
            _stream.Dispose();
        }

        if (_ownsSidecar && _sidecar is IDisposable disposableSidecar)
        {
            disposableSidecar.Dispose();
        }
    }

    /// <summary>
    /// Streams a flushed page to disk and records its descriptor with the
    /// absolute file offset where it was written. Called once per
    /// (column, page) pair as encoders fill up.
    /// </summary>
    private void FlushPage(int columnIndex)
    {
        EncodedPageV2 page = _encoders![columnIndex].Flush();
        long offset = _stream.Position;
        _stream.Write(page.Bytes);
        _pageDirectory![columnIndex].Add(new PageDescriptorV2(
            offset,
            (uint)page.Bytes.Length,
            (ushort)page.RowCount,
            page.ZoneMap));
        _hierarchies![columnIndex].AddPage(page.ZoneMap);
    }

    /// <summary>
    /// Scans the page descriptors for any column whose pages flagged at
    /// least one row as a sidecar pointer (inline-bit clear). Drives the
    /// <see cref="DatumFileFlagsV2.HasSidecarReferences"/> flag. The
    /// cheap-but-coarse signal: any VariableSlot column whose pages
    /// contain at least one non-inline row.
    /// </summary>
    private bool HasAnySidecarReferences()
    {
        // The current encoder API doesn't surface "did this page emit a
        // sidecar pointer?". As a coarse signal we look at whether any
        // VariableSlot column has any pages whose non-null rows aren't all
        // inline — but that requires the inline bitmap, which we've
        // discarded by now. For v1, key off "is there a VariableSlot
        // column AND a sidecar attached AND a non-zero row count".
        if (_sidecar is null) return false;
        for (int colIndex = 0; colIndex < _columns!.Length; colIndex++)
        {
            if (_columns[colIndex].Encoder == EncoderKind.VariableSlot && _pageDirectory![colIndex].Count > 0)
            {
                return true;
            }
        }
        return false;
    }
}
