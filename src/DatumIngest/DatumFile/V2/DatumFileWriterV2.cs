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
    // Sidecar fields are not readonly because OpenForAppend attaches
    // them after construction (sequenced after rehydrate to avoid a
    // file-share conflict between the read-side mmap used during
    // partial-page rehydrate and the write-side RW handle).
    private IBlobSink? _sidecar;
    private CountingBlobSink? _countingSidecar;
    private bool _ownsSidecar;

    private ColumnDescriptorV2[]? _columns;
    private IPageEncoderV2[]? _encoders;
    private List<PageDescriptorV2>[]? _pageDirectory;
    private ZoneMapHierarchyBuilderV2[]? _hierarchies;
    private int _pageSize = DatumFormatV2.DefaultPageSize;
    private long _totalRowsWritten;
    private bool _initialized;
    private bool _finalized;
    private bool _disposed;

    // Append-mode state. _isAppend is true when the writer was opened
    // via OpenForAppend; _existingGeneration carries the generation we
    // read from the existing footer so finalize can stamp gen+1 with
    // baseGeneration = oldGeneration. _existingSidecarReferences tracks
    // whether any prior commit already set HasSidecarReferences — once
    // set, the flag stays sticky even if no new sidecar appends happen
    // in this session.
    private bool _isAppend;
    private ulong _existingGeneration;
    private bool _existingSidecarReferences;

    // Tail CAS state. _baseTailBytes is the 8 bytes that occupied the
    // tail position when the writer was opened; _baseTailPosition is
    // the file offset where those bytes lived (the old EOF − 8 at open
    // time). At commit, we re-read those bytes and verify they're
    // still intact — a sanity check that no other writer slipped in
    // between open and finalize. Single-writer-with-FileShare.Read
    // guarantees this passes; multi-writer adds a meaningful retry
    // path. Empty for fresh writes (Initialize path).
    private byte[]? _baseTailBytes;
    private long _baseTailPosition = -1;

    /// <summary>
    /// Identifier stamped into <see cref="FooterPrologueV4.WriterId"/>
    /// on every commit this writer makes. Defaults to
    /// <see cref="WriterIdentity.Default"/> — a process-stable mixed
    /// pid/random value — so different runs of the same code don't
    /// collide on the same id. Multi-writer workloads can override per
    /// instance to attribute commits.
    /// </summary>
    public ulong WriterId { get; set; } = WriterIdentity.Default;

    // Soft-delete state. Pending tombstone bits accumulate per chapter
    // in memory (lazy-loaded from disk on first edit) and flush to a
    // fresh COW block per affected chapter on FinalizeWriter.
    // _existingTombstoneOffsets carries forward unchanged chapters'
    // offsets — those blocks stay live on disk and remain referenced by
    // the new prologue.
    private Dictionary<int, ChapterTombstoneBlock>? _pendingTombstoneEdits;
    private long[]? _existingTombstoneOffsets;
    private long _existingChapterCount;

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

        // FileShare.Read lets concurrent readers open the file while
        // a writer holds it — they get a snapshot of whatever footer
        // was last committed. Concurrent writers (in this or other
        // processes) are excluded by the absence of FileShare.Write,
        // so the OS-level file-share rules implement the writer-lock
        // convention without a separate lock primitive.
        _stream = new FileStream(
            datumPath,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 65_536,
            FileOptions.SequentialScan);
        _ownsStream = true;

        if (sidecarPath is not null)
        {
            SidecarWriteStore underlying = new(sidecarPath);
            _countingSidecar = new CountingBlobSink(underlying);
            _sidecar = _countingSidecar;
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
        if (sidecar is not null)
        {
            _countingSidecar = new CountingBlobSink(sidecar);
            _sidecar = _countingSidecar;
        }
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

        // Tail-CAS sanity check (append mode only). Verifies the bytes
        // at the tail position captured at OpenForAppend are still what
        // we read — i.e., no other writer slipped in. Throws on
        // mismatch so the caller can rebase. No-op on initial-write
        // path because there's no base tail to compare against.
        VerifyBaseTailUnchanged();

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
        // HasSidecarReferences is sticky across appends — once a prior
        // commit set it, it stays set, even if this session's encoders
        // happened not to spill (rehydrated old sidecar refs are still
        // referenced). New sidecar appends in this session also set it.
        if (_existingSidecarReferences || HasAnySidecarReferences())
        {
            flags |= DatumFileFlagsV2.HasSidecarReferences;
        }
        // HasExternalPages: clear in PR4 — cross-file pages ship in PR7.

        // Build the per-chapter tombstone offsets array. Three states
        // per chapter: edited this session (write a fresh COW block at
        // a new offset), unchanged-but-existed (carry forward the old
        // offset), or never-tombstoned (use NoTombstoneBlock = -1).
        IReadOnlyList<long> chapterTombstoneOffsets = BuildTombstoneOffsetsAndWriteBlocks(columnFooters);
        bool anyTombstones = false;
        foreach (long offset in chapterTombstoneOffsets)
        {
            if (offset != DatumFormatV2.NoTombstoneBlock) { anyTombstones = true; break; }
        }
        if (anyTombstones) flags |= DatumFileFlagsV2.HasTombstones;

        // Build the prologue: initial write starts at generation 1;
        // append bumps the existing generation and records the prior
        // value as baseGeneration so future MVCC layers can trace
        // commit lineage. WriterId is stamped on every commit (default
        // process-stable, configurable per instance).
        FooterPrologueV4 prologue = new(
            Generation: _isAppend ? _existingGeneration + 1 : 1,
            WriterId: WriterId,
            BaseGeneration: _isAppend ? _existingGeneration : 0,
            TombstoneGranularity: DatumFormatV2.TombstoneGranularityChapter,
            ColumnCount: _columns.Length,
            FileTableEntries: Array.Empty<FileTableEntryV4>(),
            ChapterTombstoneOffsets: chapterTombstoneOffsets);

        FooterV2 footer = new(prologue, columnFooters, emitVolumes);

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

        if (_ownsSidecar && _countingSidecar?.Inner is IDisposable disposableSidecar)
        {
            disposableSidecar.Dispose();
        }
    }

    /// <summary>
    /// Opens an existing finalized <c>.datum</c> file for append. Reads
    /// the header / tail / footer, rebuilds per-column encoder + page
    /// directory + zone-map hierarchy state, extends any trailing
    /// partial page back into its encoder, and positions the stream
    /// past the old data so new pages can stream out without disturbing
    /// the on-disk old state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Crash safety.</strong> Until <see cref="FinalizeWriter"/>
    /// completes its tail flip, the file's old tail at EOF still
    /// references the old footer, so a crash mid-append leaves the
    /// file readable in its pre-append state (the new bytes are
    /// orphaned past the old tail). On the next
    /// <see cref="OpenForAppend"/> call, the post-tail garbage is
    /// detected and truncated back to the last clean tail.
    /// </para>
    /// <para>
    /// <strong>Trailing partial pages.</strong> If any column's last
    /// page has fewer than <see cref="DatumFormatV2.DefaultPageSize"/>
    /// rows, that page is decoded back into a fresh encoder so the
    /// partial values are re-flushed at the next page boundary. The
    /// old partial-page bytes remain on disk as orphans (reachable from
    /// the old footer only). The format's seek math
    /// (<c>pageIndex = startRow / pageSize</c>) requires every page
    /// except the last to be exactly <c>pageSize</c> rows, so partial
    /// extension is mandatory — we can't simply seal-and-fresh.
    /// </para>
    /// <para>
    /// <strong>Sidecar.</strong> Pass <paramref name="sidecarPath"/>
    /// matching the source file's companion <c>.datum-blob</c>. The
    /// blob sink opens append-only, preserving existing offsets; new
    /// non-inline payloads land at the sidecar's existing EOF. Pass
    /// <see langword="null"/> to forbid new sidecar writes (the
    /// resulting writer rejects rows that would spill).
    /// </para>
    /// </remarks>
    public static DatumFileWriterV2 OpenForAppend(string datumPath, string? sidecarPath)
    {
        ArgumentNullException.ThrowIfNull(datumPath);

        // FileShare.Read mirrors the initial-write path: concurrent
        // readers see the last-committed footer; concurrent writers
        // (this or other processes) are excluded because we omit
        // FileShare.Write. This is the writer-lock convention from the
        // v4 design — OS file-share rules serialize writers, no
        // separate lock primitive needed for single-process or
        // multi-process exclusion.
        FileStream stream = new(
            datumPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 65_536,
            FileOptions.RandomAccess);

        try
        {
            // Recover from any prior torn append: scan backward for the
            // last clean tail and truncate. After this returns, the file
            // ends with a valid tail/footer combination.
            RecoverIfTorn(stream);

            // Read header + tail + footer using the same logic as the
            // reader. We don't reuse DatumFileReaderV2 directly because
            // it dispose-owns the stream and we need persistent
            // ReadWrite access for append.
            HeaderV2 header;
            FooterV2 footer;
            (header, footer) = LoadHeaderAndFooter(stream);

            // Allocate writer instance (without sidecar yet) so we can
            // populate rehydrated state. The write-side sidecar opens
            // AFTER rehydrate completes to avoid a Windows file-share
            // conflict — RehydrateFromFooter may open a read-side mmap
            // on the .datum-blob, and SidecarWriteStore.OpenForAppend
            // holds it RW with FileShare.Read which blocks the mmap
            // create on Windows. Sequencing read-then-write resolves
            // the conflict cleanly.
            DatumFileWriterV2 writer = new(stream, sink: null, ownsStream: true, ownsSidecar: false);

            // Snapshot the base tail bytes so FinalizeWriter can verify
            // nobody else committed during this session. The check is
            // passive under single-writer (FileShare.Read excludes
            // other writers) but in place so multi-writer can later add
            // a CAS retry path without changing the protocol.
            writer.CaptureBaseTail(stream);

            // Rehydrate per-column writer state from the existing
            // footer. This sets _columns / _encoders / _pageDirectory /
            // _hierarchies and extends any trailing partial pages,
            // opening a temporary SidecarReadStore for the partial-page
            // decode if needed.
            writer.RehydrateFromFooter(header, footer);

            // Now open the write-side sidecar in append mode. Existing
            // offsets stay valid; new payloads land at current sidecar
            // EOF.
            if (sidecarPath is not null)
            {
                SidecarWriteStore sidecarStore = SidecarWriteStore.OpenForAppend(sidecarPath);
                writer.AttachSidecar(sidecarStore);
            }

            // Position stream past the old tail so new pages stream out
            // append-only. Old pages, footer, and tail bytes remain
            // intact; the FinalizeWriter tail-flip is what supersedes
            // them.
            stream.Position = stream.Length;

            return writer;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Attaches a sidecar to a writer that was constructed without one
    /// — used by <see cref="OpenForAppend"/> to defer sidecar open
    /// until after rehydrate completes.
    /// </summary>
    private void AttachSidecar(IBlobSink sink)
    {
        _countingSidecar = sink as CountingBlobSink ?? new CountingBlobSink(sink);
        _sidecar = _countingSidecar;
        _ownsSidecar = true;
    }

    /// <summary>
    /// Marks a column as soft-dropped. The column's data stays on
    /// disk (page directory, zone maps, sidecar references all
    /// preserved) but readers skip it at schema enumeration. Idempotent
    /// — calling twice on the same name is a no-op.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only valid in append mode (the writer was opened via
    /// <see cref="OpenForAppend"/>). Initial-write callers that don't
    /// want the column should simply not include it in the schema
    /// passed to <see cref="Initialize"/>.
    /// </para>
    /// <para>
    /// Must be called before <see cref="FinalizeWriter"/>. Calls
    /// after finalize throw <see cref="InvalidOperationException"/>.
    /// </para>
    /// </remarks>
    /// <param name="columnName">
    /// Name of the column to drop. Match is case-sensitive and exact;
    /// throws <see cref="ArgumentException"/> if the name doesn't
    /// resolve to a column in the file's footer.
    /// </param>
    public void MarkColumnTombstoned(string columnName)
    {
        ArgumentNullException.ThrowIfNull(columnName);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized) throw new InvalidOperationException(
            "MarkColumnTombstoned requires an initialized writer; open the file with OpenForAppend.");
        if (_finalized) throw new InvalidOperationException("Writer is finalized.");

        for (int i = 0; i < _columns!.Length; i++)
        {
            if (_columns[i].Name == columnName)
            {
                if (_columns[i].IsTombstoned) return; // idempotent
                _columns[i] = _columns[i] with { IsTombstoned = true };
                return;
            }
        }

        throw new ArgumentException(
            $"Column '{columnName}' not found in the file's schema. Existing columns: " +
            string.Join(", ", _columns.Select(c => c.Name)),
            nameof(columnName));
    }

    /// <summary>
    /// Adds a new column to the schema with all-null backfill for
    /// every existing row. After this call, the writer's effective
    /// column count is <c>previous + 1</c>; subsequent
    /// <see cref="WriteRowBatch"/> calls must supply values for the
    /// new column too.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only valid in append mode (writer opened via
    /// <see cref="OpenForAppend"/>) and only callable while the
    /// writer's encoders have no pending unflushed rows for the
    /// existing columns — i.e., <see cref="AddColumn(ColumnDescriptorV2)"/> must come
    /// before any <see cref="WriteRowBatch"/> call in the session, OR
    /// after a <see cref="WriteRowBatch"/> whose row count made every
    /// column's encoder flush at the same boundary. The simplest rule:
    /// call <c>AddColumn</c> immediately after
    /// <see cref="OpenForAppend"/> and before any
    /// <c>WriteRowBatch</c>.
    /// </para>
    /// <para>
    /// PR6 requires the new column to be nullable
    /// (<see cref="ColumnDescriptorV2.IsNullable"/> = <c>true</c>) —
    /// the backfill is all-null and a non-nullable column with no
    /// values is undefined. Computed-default backfill is a future
    /// enhancement.
    /// </para>
    /// <para>
    /// The new column's pages stream out past EOF as the encoder
    /// fills, exactly mirroring the existing append-pages flow.
    /// They're column-major (contiguous) for the new column, while
    /// older columns' pages remain interleaved by their original
    /// flush order — invisible to readers because page directories
    /// record absolute offsets.
    /// </para>
    /// </remarks>
    public void AddColumn(ColumnDescriptorV2 column)
    {
        ArgumentNullException.ThrowIfNull(column);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized) throw new InvalidOperationException(
            "AddColumn requires an initialized writer; open the file with OpenForAppend.");
        if (_finalized) throw new InvalidOperationException("Writer is finalized.");
        if (!_isAppend) throw new InvalidOperationException(
            "AddColumn is only supported in append mode (OpenForAppend). " +
            "For initial writes, include the column in Initialize's schema list.");

        if (!column.IsNullable)
        {
            throw new ArgumentException(
                $"AddColumn requires a nullable column; '{column.Name}' is non-nullable. " +
                "All-null backfill needs IsNullable = true.",
                nameof(column));
        }
        if (column.IsTombstoned)
        {
            throw new ArgumentException(
                "AddColumn cannot accept a column with IsTombstoned = true; " +
                "tombstone state is set via MarkColumnTombstoned, not AddColumn.",
                nameof(column));
        }

        // Reject collisions against any column block — even tombstoned
        // ones — so a future undrop can recover the dropped column
        // unambiguously.
        foreach (ColumnDescriptorV2 existing in _columns!)
        {
            if (existing.Name == column.Name)
            {
                throw new ArgumentException(
                    $"Column '{column.Name}' already exists in the file's schema " +
                    $"(IsTombstoned = {existing.IsTombstoned}). Drop or compact first if you " +
                    "need to re-add a column with the same name.",
                    nameof(column));
            }
        }

        // Sanity: every existing column's encoder must hold the same
        // pending row count. This is the lockstep invariant the writer
        // maintains across rehydration and WriteRowBatch (all columns
        // advance row-by-row in step). A divergence here would mean
        // some prior op corrupted writer state — fail loudly. The
        // backfill below pumps _totalRowsWritten nulls into the new
        // column, which aligns it to whatever lockstep state the
        // existing columns share regardless of value.
        if (_encoders!.Length > 0)
        {
            int referenceRowCount = _encoders[0].RowCount;
            for (int i = 1; i < _encoders.Length; i++)
            {
                if (_encoders[i].RowCount != referenceRowCount)
                {
                    throw new InvalidOperationException(
                        $"Writer state inconsistent at AddColumn: column '{_columns[0].Name}' " +
                        $"has {referenceRowCount} pending rows, column '{_columns[i].Name}' has " +
                        $"{_encoders[i].RowCount}. Existing columns must be in lockstep before " +
                        "adding a new column.");
                }
            }
        }

        // Grow per-column writer state arrays to accommodate the new
        // column at index N (where N was the previous count).
        int newIndex = _columns.Length;
        Array.Resize(ref _columns, newIndex + 1);
        Array.Resize(ref _encoders, newIndex + 1);
        Array.Resize(ref _pageDirectory, newIndex + 1);
        Array.Resize(ref _hierarchies, newIndex + 1);

        _columns[newIndex] = column;
        _encoders[newIndex] = PageEncoderFactoryV2.Create(column, _pageSize);
        _pageDirectory[newIndex] = new List<PageDescriptorV2>();
        _hierarchies[newIndex] = new ZoneMapHierarchyBuilderV2();

        // Pump _totalRowsWritten null values into the new column's
        // encoder. Pages flush automatically as the encoder fills,
        // streaming all-null bytes past EOF and recording offsets in
        // the new page directory. After this loop the encoder's
        // RowCount matches every other column's logical position
        // (zero, since we asserted that above), so subsequent
        // WriteRowBatch calls extend all columns in lockstep.
        DataValue nullValue = DataValue.Null(column.Kind);
        for (long row = 0; row < _totalRowsWritten; row++)
        {
            _encoders[newIndex].Append(nullValue, store: null, sidecar: null);
            if (_encoders[newIndex].IsFull)
            {
                FlushPage(newIndex);
            }
        }
    }

    /// <summary>
    /// One-shot helper that opens <paramref name="datumPath"/>, adds
    /// <paramref name="column"/> with all-null backfill, and commits
    /// via tail flip. See <see cref="AddColumn(ColumnDescriptorV2)"/> for the
    /// session-scoped equivalent and constraints.
    /// </summary>
    public static void AddColumn(string datumPath, ColumnDescriptorV2 column)
    {
        ArgumentNullException.ThrowIfNull(datumPath);
        ArgumentNullException.ThrowIfNull(column);

        string? sidecarPath = ResolveSidecarPathIfNeeded(datumPath);
        using DatumFileWriterV2 writer = OpenForAppend(datumPath, sidecarPath);
        writer.AddColumn(column);
        writer.FinalizeWriter();
    }

    /// <summary>
    /// One-shot helper for batched column additions in a single
    /// commit. Generation bumps by one regardless of how many columns
    /// are added.
    /// </summary>
    public static void AddColumns(string datumPath, IReadOnlyList<ColumnDescriptorV2> columns)
    {
        ArgumentNullException.ThrowIfNull(datumPath);
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count == 0) return;

        string? sidecarPath = ResolveSidecarPathIfNeeded(datumPath);
        using DatumFileWriterV2 writer = OpenForAppend(datumPath, sidecarPath);
        foreach (ColumnDescriptorV2 column in columns)
        {
            writer.AddColumn(column);
        }
        writer.FinalizeWriter();
    }

    /// <summary>
    /// Marks the row at logical index <paramref name="rowIndex"/> as
    /// soft-deleted. The data bytes stay on disk (page directory, zone
    /// maps, sidecar refs all unchanged) but readers skip the row at
    /// materialization time. Idempotent — marking an already-deleted
    /// row is a no-op.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mutation is buffered in memory; <see cref="FinalizeWriter"/>
    /// writes a fresh tombstone bitmap block per affected chapter at a
    /// new file offset (copy-on-write) and updates the footer's
    /// <see cref="FooterPrologueV4.ChapterTombstoneOffsets"/> to point
    /// at it. Old blocks become orphan bytes (reachable only from
    /// pre-commit footers — i.e., reader snapshots opened before this
    /// commit).
    /// </para>
    /// <para>
    /// Only valid in append mode (writer opened via
    /// <see cref="OpenForAppend"/>). Throws on out-of-range
    /// <paramref name="rowIndex"/>.
    /// </para>
    /// </remarks>
    public void MarkRowDeleted(long rowIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized) throw new InvalidOperationException(
            "MarkRowDeleted requires an initialized writer; open the file with OpenForAppend.");
        if (_finalized) throw new InvalidOperationException("Writer is finalized.");
        if (rowIndex < 0 || rowIndex >= _totalRowsWritten)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rowIndex), rowIndex,
                $"Row index must be in [0, {_totalRowsWritten}).");
        }

        int chapterRowSpan = ChapterTombstoneBlock.MaxRowsPerChapter;
        int chapterIndex = (int)(rowIndex / chapterRowSpan);
        int rowInChapter = (int)(rowIndex - (long)chapterIndex * chapterRowSpan);

        ChapterTombstoneBlock block = GetOrLoadPendingBlock(chapterIndex);
        block.MarkDeleted(rowInChapter);
    }

    /// <summary>
    /// Marks <paramref name="count"/> consecutive rows starting at
    /// <paramref name="startRow"/> as soft-deleted. Equivalent to
    /// looping <see cref="MarkRowDeleted"/> but skips repeated chapter
    /// resolution for ranges that span only a few chapters. Idempotent.
    /// </summary>
    public void MarkRowsDeleted(long startRow, long count)
    {
        if (count == 0) return;
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count), count, "Count must be non-negative.");
        long endExclusive = checked(startRow + count);
        if (startRow < 0 || endExclusive > _totalRowsWritten)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count),
                $"[{startRow}..{endExclusive}) out of range; file has {_totalRowsWritten} rows.");
        }

        int chapterRowSpan = ChapterTombstoneBlock.MaxRowsPerChapter;
        long current = startRow;
        while (current < endExclusive)
        {
            int chapterIndex = (int)(current / chapterRowSpan);
            long chapterStart = (long)chapterIndex * chapterRowSpan;
            long chapterEnd = Math.Min(chapterStart + chapterRowSpan, endExclusive);
            ChapterTombstoneBlock block = GetOrLoadPendingBlock(chapterIndex);
            for (long r = current; r < chapterEnd; r++)
            {
                block.MarkDeleted((int)(r - chapterStart));
            }
            current = chapterEnd;
        }
    }

    /// <summary>
    /// Lazy-loads the pending tombstone block for
    /// <paramref name="chapterIndex"/>. If the file already has a
    /// committed block for this chapter, the bytes are read in so
    /// further <see cref="MarkRowDeleted"/> calls accumulate on top.
    /// Otherwise a fresh all-zeros block is created.
    /// </summary>
    private ChapterTombstoneBlock GetOrLoadPendingBlock(int chapterIndex)
    {
        _pendingTombstoneEdits ??= new Dictionary<int, ChapterTombstoneBlock>();
        if (_pendingTombstoneEdits.TryGetValue(chapterIndex, out ChapterTombstoneBlock? cached))
        {
            return cached;
        }

        ChapterTombstoneBlock block;
        if (_existingTombstoneOffsets is not null
            && chapterIndex < _existingTombstoneOffsets.Length
            && _existingTombstoneOffsets[chapterIndex] != DatumFormatV2.NoTombstoneBlock)
        {
            // Read existing committed block; new edits OR'd on top.
            byte[] bytes = new byte[DatumFormatV2.ChapterTombstoneBlockBytes];
            long savedPosition = _stream.Position;
            try
            {
                _stream.Position = _existingTombstoneOffsets[chapterIndex];
                _stream.ReadExactly(bytes);
            }
            finally
            {
                _stream.Position = savedPosition;
            }
            block = new ChapterTombstoneBlock(bytes);
        }
        else
        {
            block = new ChapterTombstoneBlock();
        }

        _pendingTombstoneEdits[chapterIndex] = block;
        return block;
    }

    /// <summary>
    /// One-shot helper that opens <paramref name="datumPath"/>, marks
    /// <paramref name="columnName"/> as tombstoned, and commits via
    /// tail flip. Resolves the sidecar path from the source filename
    /// when the file declares <see cref="DatumFileFlagsV2.HasSidecarReferences"/>;
    /// callers wanting non-default sidecar placement should use
    /// <see cref="OpenForAppend"/> + <see cref="MarkColumnTombstoned"/> +
    /// <see cref="FinalizeWriter"/> directly.
    /// </summary>
    public static void DropColumn(string datumPath, string columnName)
    {
        ArgumentNullException.ThrowIfNull(datumPath);
        ArgumentNullException.ThrowIfNull(columnName);

        // Auto-resolve sidecar from convention if the file uses one.
        // We can determine that without opening for write by peeking
        // at the header flags through a read-only handle.
        string? sidecarPath = ResolveSidecarPathIfNeeded(datumPath);

        using DatumFileWriterV2 writer = OpenForAppend(datumPath, sidecarPath);
        writer.MarkColumnTombstoned(columnName);
        writer.FinalizeWriter();
    }

    /// <summary>
    /// One-shot helper that opens <paramref name="datumPath"/>, marks
    /// the given row indices as soft-deleted, and commits via tail
    /// flip. Generation bumps by one regardless of how many rows are
    /// affected.
    /// </summary>
    public static void SoftDeleteRows(string datumPath, IReadOnlyList<long> rowIndices)
    {
        ArgumentNullException.ThrowIfNull(datumPath);
        ArgumentNullException.ThrowIfNull(rowIndices);
        if (rowIndices.Count == 0) return;

        string? sidecarPath = ResolveSidecarPathIfNeeded(datumPath);

        using DatumFileWriterV2 writer = OpenForAppend(datumPath, sidecarPath);
        foreach (long rowIndex in rowIndices)
        {
            writer.MarkRowDeleted(rowIndex);
        }
        writer.FinalizeWriter();
    }

    /// <summary>
    /// One-shot helper for batched drops in a single commit.
    /// Equivalent to opening once, marking all named columns, and
    /// finalizing — generation increments by one regardless of how
    /// many columns are dropped in the call.
    /// </summary>
    public static void DropColumns(string datumPath, IReadOnlyList<string> columnNames)
    {
        ArgumentNullException.ThrowIfNull(datumPath);
        ArgumentNullException.ThrowIfNull(columnNames);
        if (columnNames.Count == 0) return;

        string? sidecarPath = ResolveSidecarPathIfNeeded(datumPath);

        using DatumFileWriterV2 writer = OpenForAppend(datumPath, sidecarPath);
        foreach (string name in columnNames)
        {
            writer.MarkColumnTombstoned(name);
        }
        writer.FinalizeWriter();
    }

    /// <summary>
    /// Returns the conventional <c>.datum-blob</c> path for
    /// <paramref name="datumPath"/> when the file's header declares
    /// <see cref="DatumFileFlagsV2.HasSidecarReferences"/>; otherwise
    /// <see langword="null"/> (no sidecar in the picture). Implemented
    /// without opening the writer-side handle so it doesn't conflict
    /// with the caller's subsequent <see cref="OpenForAppend"/>.
    /// </summary>
    private static string? ResolveSidecarPathIfNeeded(string datumPath)
    {
        if (!File.Exists(datumPath)) return null;

        // Read just the header flags to decide.
        using FileStream fs = new(
            datumPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 64, FileOptions.None);
        if (fs.Length < DatumFormatV2.HeaderSize) return null;

        Span<byte> headerBytes = stackalloc byte[DatumFormatV2.HeaderSize];
        fs.ReadExactly(headerBytes);
        DatumFileFlagsV2 flags =
            (DatumFileFlagsV2)System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(headerBytes[6..8]);

        if ((flags & DatumFileFlagsV2.HasSidecarReferences) == 0) return null;

        return datumPath + DatumFile.Sidecar.SidecarConstants.FileExtension;
    }

    /// <summary>
    /// Internal constructor used by <see cref="OpenForAppend"/> — does
    /// not write a header (the existing one stays in place) and does
    /// not run <see cref="Initialize"/> (rehydration drives the schema
    /// instead).
    /// </summary>
    private DatumFileWriterV2(Stream stream, IBlobSink? sink, bool ownsStream, bool ownsSidecar)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        if (sink is not null)
        {
            _countingSidecar = sink as CountingBlobSink ?? new CountingBlobSink(sink);
            _sidecar = _countingSidecar;
        }
        _ownsSidecar = ownsSidecar;
    }

    /// <summary>
    /// Rebuilds per-column writer state from a parsed footer. Replays
    /// every full page's zone map through a fresh hierarchy builder
    /// (so chapter / volume aggregation matches the original
    /// finalize), seeds the page directory with full pages, and
    /// extends trailing partial pages by decoding their values back
    /// into fresh encoders.
    /// </summary>
    private void RehydrateFromFooter(HeaderV2 header, FooterV2 footer)
    {
        _pageSize = header.PageSize;
        _existingGeneration = footer.Prologue.Generation;
        _existingSidecarReferences = (header.Flags & DatumFileFlagsV2.HasSidecarReferences) != 0;
        _totalRowsWritten = header.TotalRowCount;

        // Carry forward the existing tombstone state. The prologue's
        // offset array is either empty (no tombstones ever) or sized to
        // the file's chapter count; either way we copy it so commit can
        // either re-emit unchanged offsets verbatim or replace them
        // when MarkRow(s)Deleted produced edits.
        if (footer.Prologue.ChapterTombstoneOffsets.Count > 0)
        {
            _existingTombstoneOffsets = footer.Prologue.ChapterTombstoneOffsets.ToArray();
            _existingChapterCount = _existingTombstoneOffsets.Length;
        }
        else
        {
            _existingTombstoneOffsets = null;
            // Derive chapter count from any column's chapter zone-map
            // list — all live columns share the same count.
            _existingChapterCount = footer.Columns.Count > 0
                ? footer.Columns[0].ChapterZoneMaps.Count
                : 0;
        }

        int columnCount = footer.Columns.Count;
        _columns = new ColumnDescriptorV2[columnCount];
        _encoders = new IPageEncoderV2[columnCount];
        _pageDirectory = new List<PageDescriptorV2>[columnCount];
        _hierarchies = new ZoneMapHierarchyBuilderV2[columnCount];

        // We need to read partial-page bytes back to extend them. Use a
        // SidecarReadStore opened against the same file for any
        // sidecar-pointer values produced by the page decoder — the
        // values pass through to the new encoder unchanged because
        // VariableSlotPageEncoderV2's Append fast-paths IsInSidecar.
        // Constructed lazily; only opened if a partial-page extension
        // actually needs it.
        DatumFile.Sidecar.SidecarReadStore? sidecarReadStore = null;
        try
        {
            for (int colIndex = 0; colIndex < columnCount; colIndex++)
            {
                ColumnFooterV2 columnFooter = footer.Columns[colIndex];
                _columns[colIndex] = columnFooter.Descriptor;
                _encoders[colIndex] = PageEncoderFactoryV2.Create(columnFooter.Descriptor, _pageSize);
                _pageDirectory[colIndex] = new List<PageDescriptorV2>();
                _hierarchies[colIndex] = new ZoneMapHierarchyBuilderV2();

                int pageCount = columnFooter.Pages.Count;
                if (pageCount == 0)
                {
                    continue;
                }

                PageDescriptorV2 lastPage = columnFooter.Pages[pageCount - 1];
                bool lastIsPartial = lastPage.RowCount < _pageSize;
                int fullPageCount = lastIsPartial ? pageCount - 1 : pageCount;

                // Replay full pages: their bytes don't move, so their
                // descriptors carry forward verbatim and their zone maps
                // feed the fresh hierarchy builder to recreate the
                // chapter/volume aggregation state.
                for (int p = 0; p < fullPageCount; p++)
                {
                    PageDescriptorV2 page = columnFooter.Pages[p];
                    _pageDirectory[colIndex].Add(page);
                    _hierarchies[colIndex].AddPage(page.ZoneMap ?? new DatumZoneMap(0));
                }

                if (lastIsPartial)
                {
                    // Decode the partial page back into the fresh
                    // encoder so its rows are re-flushed at the next
                    // page boundary. The old partial-page bytes stay on
                    // disk as orphan (reachable only via the old footer
                    // we're about to supersede).
                    sidecarReadStore ??= TryOpenSidecarForRehydrate(_stream, header);
                    ExtendPartialPage(
                        colIndex, columnFooter.Descriptor, lastPage,
                        sidecarReadStore);
                }
            }

            _initialized = true;
            _isAppend = true;
        }
        finally
        {
            sidecarReadStore?.Dispose();
        }
    }

    /// <summary>
    /// Decodes the rows of <paramref name="partialPage"/> back into
    /// <see cref="_encoders"/>[<paramref name="columnIndex"/>] so the
    /// encoder's accumulated row count matches the partial page's row
    /// count. New <see cref="WriteRowBatch"/> calls fill the rest of
    /// the page; on flush the encoder produces fresh page bytes (with a
    /// fresh zone map) at a new file offset.
    /// </summary>
    private void ExtendPartialPage(
        int columnIndex,
        ColumnDescriptorV2 column,
        PageDescriptorV2 partialPage,
        DatumFile.Sidecar.SidecarReadStore? sidecarReadStore)
    {
        // Read page bytes directly from the stream.
        byte[] pageBytes = new byte[partialPage.PageByteLength];
        long savedPosition = _stream.Position;
        try
        {
            _stream.Position = partialPage.PageOffset;
            _stream.ReadExactly(pageBytes);
        }
        finally
        {
            _stream.Position = savedPosition;
        }

        // Build a temporary read arena to absorb eagerly-materialized
        // children (Struct field arrays). Values that come back as
        // sidecar-pointer DataValues (IsInSidecar = true) flow through
        // the encoder's IsInSidecar fast path and never need
        // reconstitution.
        Model.Arena rehydrateArena = new();
        Decoding.IPageDecoderV2 decoder = Decoding.PageDecoderFactoryV2.Create(
            column,
            new ReadOnlyMemory<byte>(pageBytes),
            partialPage.RowCount,
            sidecarStoreId: 0,
            sidecarSource: sidecarReadStore,
            eagerStore: rehydrateArena);

        IPageEncoderV2 encoder = _encoders![columnIndex];
        for (int row = 0; row < partialPage.RowCount; row++)
        {
            Model.DataValue value = decoder.ReadValue(row);
            encoder.Append(value, rehydrateArena, _sidecar);
        }
    }

    /// <summary>
    /// Opens a read-only sidecar handle for rehydrating partial pages
    /// whose VariableSlot rows reference the sidecar. Returns
    /// <see langword="null"/> when the file declares no sidecar
    /// references (so partial-page rows are guaranteed inline) or when
    /// the sidecar file is missing for any reason — the decoder will
    /// surface a clear error if it actually needs sidecar bytes.
    /// </summary>
    private static DatumFile.Sidecar.SidecarReadStore? TryOpenSidecarForRehydrate(
        Stream datumStream, HeaderV2 header)
    {
        if ((header.Flags & DatumFileFlagsV2.HasSidecarReferences) == 0)
        {
            return null;
        }
        if (datumStream is not FileStream fs)
        {
            return null;
        }

        string sidecarPath = fs.Name + DatumFile.Sidecar.SidecarConstants.FileExtension;
        if (!File.Exists(sidecarPath))
        {
            return null;
        }
        // Open without fingerprint validation — we trust the local file
        // since we just opened the .datum from the same directory.
        // Fingerprint mismatches would surface as decoder errors during
        // partial-page rehydration if the sidecar were truly mismatched.
        return DatumFile.Sidecar.SidecarReadStore.OpenWithoutFingerprintCheck(sidecarPath);
    }

    /// <summary>
    /// Captures the 8 bytes occupying the file's current tail position
    /// at open time. Stored on the writer so <see cref="FinalizeWriter"/>
    /// can verify nothing rewrote them between open and commit — a
    /// sanity check that becomes a meaningful CAS retry path under
    /// future multi-writer concurrency. Single-writer-with-FileShare.Read
    /// always passes this check.
    /// </summary>
    private void CaptureBaseTail(Stream stream)
    {
        long position = stream.Length - DatumFormatV2.TailSize;
        byte[] bytes = new byte[DatumFormatV2.TailSize];
        stream.Position = position;
        stream.ReadExactly(bytes);

        _baseTailPosition = position;
        _baseTailBytes = bytes;
    }

    /// <summary>
    /// Verifies the bytes at <see cref="_baseTailPosition"/> still
    /// match what we read at <see cref="OpenForAppend"/>. Throws
    /// <see cref="InvalidOperationException"/> on mismatch — meaning
    /// some other writer committed during this writer's session, the
    /// file is no longer the one we based our work on, and the caller
    /// must abort or rebase. In single-writer-with-FileShare.Read this
    /// check is a passive guard; under future multi-writer it becomes
    /// the CAS retry trigger.
    /// </summary>
    private void VerifyBaseTailUnchanged()
    {
        if (_baseTailBytes is null || _baseTailPosition < 0) return; // initial-write path

        Span<byte> actual = stackalloc byte[DatumFormatV2.TailSize];
        long savedPosition = _stream.Position;
        try
        {
            _stream.Position = _baseTailPosition;
            _stream.ReadExactly(actual);
        }
        finally
        {
            _stream.Position = savedPosition;
        }

        if (!actual.SequenceEqual(_baseTailBytes))
        {
            throw new InvalidOperationException(
                "Base tail mismatch on commit: the bytes at the tail position captured at " +
                "OpenForAppend have been rewritten since this writer opened. Another writer " +
                "committed during this session — the file is no longer the one this commit was " +
                "based on. Re-open the file and re-apply pending writes.");
        }
    }

    /// <summary>
    /// Reads the header + tail + footer from <paramref name="stream"/>
    /// without taking ownership. Mirrors
    /// <see cref="DatumFileReaderV2.Open(string)"/>'s parsing logic.
    /// </summary>
    private static (HeaderV2 Header, FooterV2 Footer) LoadHeaderAndFooter(Stream stream)
    {
        if (stream.Length < DatumFormatV2.HeaderSize + DatumFormatV2.TailSize)
        {
            throw new InvalidDataException(
                $"File is too small ({stream.Length} bytes) to be a valid .datum file.");
        }

        Span<byte> headerBytes = stackalloc byte[DatumFormatV2.HeaderSize];
        stream.Position = 0;
        stream.ReadExactly(headerBytes);
        HeaderV2 header = HeaderV2.ReadFrom(headerBytes);

        Span<byte> tail = stackalloc byte[DatumFormatV2.TailSize];
        stream.Position = stream.Length - DatumFormatV2.TailSize;
        stream.ReadExactly(tail);
        if (!tail[4..].SequenceEqual(DatumFormatV2.TailMagic))
        {
            throw new InvalidDataException(
                "File tail sentinel does not match 'FMTD' magic; the file may be truncated or corrupt.");
        }
        uint footerByteLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tail[..4]);

        long footerStart = stream.Length - DatumFormatV2.TailSize - footerByteLength;
        if (footerStart != header.FooterOffset)
        {
            throw new InvalidDataException(
                $"Footer offset mismatch: header says {header.FooterOffset}, tail says {footerStart}.");
        }

        byte[] footerBuffer = new byte[footerByteLength];
        stream.Position = footerStart;
        stream.ReadExactly(footerBuffer);

        bool hasVolumeZoneMaps = (header.Flags & DatumFileFlagsV2.HasVolumeZoneMaps) != 0;
        FooterV2 footer;
        using (MemoryStream ms = new(footerBuffer, writable: false))
        using (BinaryReader reader = new(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            footer = FooterV2.Deserialize(reader, hasVolumeZoneMaps);
        }
        return (header, footer);
    }

    /// <summary>
    /// Detects torn-append state — the trailing 8 bytes of the file
    /// don't match the <see cref="DatumFormatV2.TailMagic"/> sentinel —
    /// and truncates the file back to its last clean tail. A
    /// well-finalized file is left untouched.
    /// </summary>
    /// <remarks>
    /// Recovery scans backward in 4 KiB chunks from EOF looking for the
    /// last <c>FMTD</c> 4-byte sentinel preceded by a plausible
    /// <c>uint32</c> footer length. The bounds check (footer length
    /// must point inside the file past the header) catches false
    /// positives in user data. Throws
    /// <see cref="InvalidDataException"/> if no clean tail can be
    /// recovered — the file is unsalvageable.
    /// </remarks>
    private static void RecoverIfTorn(FileStream stream)
    {
        if (stream.Length < DatumFormatV2.HeaderSize + DatumFormatV2.TailSize)
        {
            return; // too small to bother — let the regular open path produce the error
        }

        Span<byte> tail = stackalloc byte[DatumFormatV2.TailSize];
        stream.Position = stream.Length - DatumFormatV2.TailSize;
        stream.ReadExactly(tail);
        if (tail[4..].SequenceEqual(DatumFormatV2.TailMagic))
        {
            return; // file ends cleanly
        }

        long lastCleanTailEof = TornTailScanner.FindLastCleanTailEof(stream);
        if (lastCleanTailEof < 0)
        {
            throw new InvalidDataException(
                "File ends without a valid tail and no recoverable prior tail was found. " +
                "The file may be corrupt or never finalized.");
        }

        // Truncate file to the last clean tail's EOF.
        stream.SetLength(lastCleanTailEof);
        stream.Position = lastCleanTailEof;
    }

    /// <summary>
    /// Writes copy-on-write tombstone blocks for every chapter that
    /// received <see cref="MarkRowDeleted"/> calls in this session and
    /// returns the per-chapter offset array to embed in the new
    /// prologue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One offset per chapter in the (post-append) file. Three cases per chapter:
    /// </para>
    /// <list type="bullet">
    ///   <item>Edited this session — a fresh 8 KiB block is written at current EOF and the
    ///         offset slot points at it.</item>
    ///   <item>Not edited but had an existing committed block — the slot carries forward
    ///         the old offset (the old block stays referenced).</item>
    ///   <item>Never tombstoned — slot = <see cref="DatumFormatV2.NoTombstoneBlock"/> (-1).</item>
    /// </list>
    /// <para>
    /// Returns an empty array (count = 0) when the file has no
    /// tombstones at all (no edits, no prior tombstones). The reader
    /// fast-paths past tombstone resolution when the array is empty.
    /// </para>
    /// </remarks>
    private IReadOnlyList<long> BuildTombstoneOffsetsAndWriteBlocks(IReadOnlyList<ColumnFooterV2> columnFooters)
    {
        // Determine post-commit chapter count from the per-column zone-map
        // hierarchy we just finalized. All live columns share this count.
        int postCommitChapterCount = 0;
        foreach (ColumnFooterV2 cf in columnFooters)
        {
            if (cf.Descriptor.IsTombstoned) continue;
            postCommitChapterCount = cf.ChapterZoneMaps.Count;
            break;
        }
        if (postCommitChapterCount == 0)
        {
            return Array.Empty<long>();
        }

        bool hasPendingEdits = _pendingTombstoneEdits is { Count: > 0 };
        bool hadExistingBlocks = _existingTombstoneOffsets is not null
            && Array.Exists(_existingTombstoneOffsets, o => o != DatumFormatV2.NoTombstoneBlock);

        if (!hasPendingEdits && !hadExistingBlocks)
        {
            // No tombstones in the file at all. Empty offset array
            // signals fast-path skip to readers.
            return Array.Empty<long>();
        }

        long[] offsets = new long[postCommitChapterCount];
        for (int c = 0; c < postCommitChapterCount; c++)
        {
            // Default to "no tombstones in this chapter" — overridden
            // below if either edited this session or carried forward.
            offsets[c] = DatumFormatV2.NoTombstoneBlock;

            if (_existingTombstoneOffsets is not null
                && c < _existingTombstoneOffsets.Length)
            {
                offsets[c] = _existingTombstoneOffsets[c];
            }
        }

        if (hasPendingEdits)
        {
            foreach ((int chapterIndex, ChapterTombstoneBlock block) in _pendingTombstoneEdits!)
            {
                if (chapterIndex >= postCommitChapterCount)
                {
                    // Defensive — shouldn't happen since MarkRowDeleted
                    // bounds-checks against _totalRowsWritten and a row
                    // can't live in a chapter past the file's count.
                    continue;
                }
                if (!block.HasAnyDeletes())
                {
                    // Block was created (lazy-load) but no bits were
                    // ultimately set. Skip emitting; carry forward the
                    // existing offset (which is what 'offsets[c]' already
                    // holds from the loop above).
                    continue;
                }

                long blockOffset = _stream.Position;
                _stream.Write(block.AsSpan());
                offsets[chapterIndex] = blockOffset;
            }
        }

        return offsets;
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
    /// True when at least one row was emitted as a sidecar pointer (i.e.
    /// the variable-slot encoder couldn't keep it inline). Drives the
    /// <see cref="DatumFileFlagsV2.HasSidecarReferences"/> file flag,
    /// which the reader uses to know whether the companion
    /// <c>.datum-blob</c> is required for opens.
    /// </summary>
    private bool HasAnySidecarReferences()
    {
        // Authoritative: the counting wrapper around the IBlobSink tracks
        // every Append call from inside the encoders. Zero appends means
        // every variable-slot row went inline, no sidecar bytes were
        // written, and the sidecar file may not even exist on disk.
        if (_countingSidecar is null) return false;
        return _countingSidecar.AppendCount > 0;
    }

    /// <summary>
    /// Pass-through <see cref="IBlobSink"/> wrapper that counts how many
    /// times <see cref="Append"/> was called. Used by the writer to detect
    /// whether any variable-slot row actually spilled to the sidecar so
    /// the <see cref="DatumFileFlagsV2.HasSidecarReferences"/> flag is
    /// only set when the sidecar contains real bytes.
    /// </summary>
    private sealed class CountingBlobSink : IBlobSink
    {
        public CountingBlobSink(IBlobSink inner) { Inner = inner; }
        public IBlobSink Inner { get; }
        public int AppendCount { get; private set; }
        public (long Offset, long Length) Append(ReadOnlySpan<byte> bytes)
        {
            AppendCount++;
            return Inner.Append(bytes);
        }
    }
}
