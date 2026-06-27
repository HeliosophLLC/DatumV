using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.DatumFile.V2.Encoding;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.DatumFile.V2;

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
public sealed partial class DatumFileWriterV2 : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    // Writer coordination lock — non-null when this writer owns the
    // path-based open path (initial create or OpenForAppend). The
    // stream-based ctor (caller-owned Stream) leaves this null; lock
    // management is the caller's concern in that case. Released by
    // Dispose. See WriterLockFile for the cross-platform rationale.
    private WriterLockFile? _writerLock;
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

    // Column defaults carry forward verbatim across appends. They live
    // in the footer prologue (PR10b) so a freshly-opened catalog and
    // standalone .datum tools see the same DEFAULT literals without
    // consulting .datum-catalog.json. Initialize seeds them; AddColumn
    // and MarkColumnTombstoned leave them alone (dropped columns keep
    // their index, so the stored ColumnIndex stays valid even after a
    // tombstone). FinalizeWriter emits this list verbatim.
    private List<ColumnDefaultV4>? _columnDefaults;

    // Per-column GENERATED ALWAYS AS expressions (v6+). Mirrors
    // _columnDefaults but for computed columns: the SQL fragment is
    // persisted in the footer's optional computed-columns block (gated
    // by DatumFileFlagsV2.HasColumnComputeds) and re-parsed by the
    // catalog at open. Initialize / AddColumn append entries; the
    // FinalizeWriter path emits the block when the list is non-empty.
    private List<ColumnComputedV4>? _columnComputeds;

    // IDENTITY state (PR10e). Seeded by Initialize for fresh writes
    // and by RehydrateFromFooter for appends; the running counter
    // (_identityNextValue) bumps via UpdateIdentityNextValue so each
    // commit's prologue stamps the live value. -1 means no IDENTITY
    // column on this table.
    private short _identityColumnIndex = -1;
    private long _identitySeed;
    private long _identityStep;
    private long _identityNextValue;
    private bool _identityAcceptUserValues;

    // PRIMARY KEY column indices (PR10f). Empty when the table has
    // no PK. Carries forward unchanged across appends — neither
    // AddColumn nor MarkColumnTombstoned shifts indices, so stored
    // values stay valid. Initialize seeds it for fresh writes;
    // RehydrateFromFooter copies it from the existing prologue.
    private ushort[]? _primaryKeyColumnIndices;

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

        // Acquire the writer lock before opening the data file. Throws
        // IOException if another writer holds the path. See WriterLockFile
        // for why this is needed even though _stream is opened with
        // FileShare.Read below: file-share rules are mandatory on Windows
        // but only advisory on Linux, so the share-based exclusion alone
        // is not portable.
        _writerLock = WriterLockFile.AcquireFor(datumPath);

        try
        {
            // FileShare.Read lets concurrent readers open the file while
            // a writer holds it — they get a snapshot of whatever footer
            // was last committed. Writer-vs-writer exclusion is enforced
            // by _writerLock above, not by file-share semantics, so the
            // engine behaves identically on Windows and Linux.
            _stream = new FileStream(
                datumPath,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: 65_536,
                FileOptions.SequentialScan);
            _ownsStream = true;
        }
        catch
        {
            _writerLock.Dispose();
            _writerLock = null;
            throw;
        }

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
    /// Override the page size before <see cref="Initialize(IReadOnlyList{ColumnDescriptorV2})"/>. Test-only;
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
    public void Initialize(IReadOnlyList<ColumnDescriptorV2> columns) =>
        Initialize(columns, columnDefaults: null, identity: null, primaryKeyColumnIndices: null);

    /// <summary>
    /// Initializes the writer with the column schema and an optional
    /// per-column <c>DEFAULT</c> literal table that the finalize step
    /// stamps into the footer prologue. The defaults are SQL fragments
    /// (round-tripped through <c>QueryExplainer.FormatExpression</c> at
    /// the catalog layer); the writer treats them as opaque payload.
    /// </summary>
    public void Initialize(
        IReadOnlyList<ColumnDescriptorV2> columns,
        IReadOnlyList<ColumnDefaultV4>? columnDefaults) =>
        Initialize(columns, columnDefaults, identity: null, primaryKeyColumnIndices: null);

    /// <summary>
    /// Initializes the writer with the column schema, optional per-column
    /// <c>DEFAULT</c> literal table, and optional <c>IDENTITY</c> spec
    /// for one of the columns. The IDENTITY counter starts at the
    /// supplied seed; <see cref="UpdateIdentityNextValue"/> advances it
    /// before <see cref="FinalizeWriter"/> stamps the prologue.
    /// </summary>
    public void Initialize(
        IReadOnlyList<ColumnDescriptorV2> columns,
        IReadOnlyList<ColumnDefaultV4>? columnDefaults,
        IdentityWriterSpec? identity) =>
        Initialize(columns, columnDefaults, identity, primaryKeyColumnIndices: null);

    /// <summary>
    /// As <see cref="Initialize(IReadOnlyList{ColumnDescriptorV2}, IReadOnlyList{ColumnDefaultV4}?, IdentityWriterSpec?)"/>,
    /// but additionally stamps a PRIMARY KEY column-index list into
    /// the footer prologue. The catalog validates the keys (at most
    /// one PK, columns exist, total key size ≤ 16 bytes) before calling
    /// in.
    /// </summary>
    public void Initialize(
        IReadOnlyList<ColumnDescriptorV2> columns,
        IReadOnlyList<ColumnDefaultV4>? columnDefaults,
        IdentityWriterSpec? identity,
        IReadOnlyList<ushort>? primaryKeyColumnIndices) =>
        Initialize(columns, columnDefaults, identity, primaryKeyColumnIndices, columnComputeds: null);

    /// <summary>
    /// Full-fidelity Initialize that additionally accepts the per-column
    /// <c>GENERATED ALWAYS AS</c> table. Computed columns are persisted as
    /// SQL fragments in the footer's optional computed-columns block
    /// (gated by <see cref="DatumFileFlagsV2.HasColumnComputeds"/>); the
    /// catalog re-parses each fragment on open and the INSERT path evaluates
    /// the expression per row.
    /// </summary>
    public void Initialize(
        IReadOnlyList<ColumnDescriptorV2> columns,
        IReadOnlyList<ColumnDefaultV4>? columnDefaults,
        IdentityWriterSpec? identity,
        IReadOnlyList<ushort>? primaryKeyColumnIndices,
        IReadOnlyList<ColumnComputedV4>? columnComputeds)
    {
        if (_initialized) throw new InvalidOperationException("Writer already initialized.");
        if (columns.Count == 0) throw new ArgumentException("At least one column required.", nameof(columns));

        _columns = columns.ToArray();
        _encoders = new IPageEncoderV2[_columns.Length];
        _pageDirectory = new List<PageDescriptorV2>[_columns.Length];
        _hierarchies = new ZoneMapHierarchyBuilderV2[_columns.Length];

        for (int i = 0; i < _columns.Length; i++)
        {
            _encoders[i] = PageEncoderFactoryV2.Create(_columns[i], _pageSize, _allocator);
            _pageDirectory[i] = new List<PageDescriptorV2>();
            _hierarchies[i] = new ZoneMapHierarchyBuilderV2();
        }

        if (columnDefaults is { Count: > 0 })
        {
            _columnDefaults = new List<ColumnDefaultV4>(columnDefaults);
        }

        if (columnComputeds is { Count: > 0 })
        {
            _columnComputeds = new List<ColumnComputedV4>(columnComputeds);
        }

        if (identity is { } id)
        {
            _identityColumnIndex = checked((short)id.ColumnIndex);
            _identitySeed = id.Seed;
            _identityStep = id.Step;
            _identityNextValue = id.Seed;
            _identityAcceptUserValues = id.AcceptUserValues;
        }

        if (primaryKeyColumnIndices is { Count: > 0 })
        {
            _primaryKeyColumnIndices = primaryKeyColumnIndices.ToArray();
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

        // The batch arrives with one column per LIVE schema column —
        // callers (InsertExecutor, V2-F2 backfill, etc.) all build
        // batches from `provider.GetSchema()` which filters tombstones.
        // The writer's `_columns` array, on the other hand, includes
        // tombstoned entries (kept on disk for compaction-time
        // reclamation, hidden from readers). Validate batch arity
        // against the LIVE count, and pump NULL into the tombstoned
        // encoders so every column's row count stays in lockstep.
        int liveCount = 0;
        for (int i = 0; i < _columns!.Length; i++)
        {
            if (!_columns[i].IsTombstoned) liveCount++;
        }
        if (batch.ColumnLookup.Count != liveCount)
        {
            throw new InvalidOperationException(
                $"Row batch column count ({batch.ColumnLookup.Count}) does not match writer's " +
                $"live schema ({liveCount} live, {_columns.Length - liveCount} tombstoned).");
        }

        IValueStore store = batch.Arena;

        for (int rowIndex = 0; rowIndex < batch.Count; rowIndex++)
        {
            Row row = batch[rowIndex];
            int batchColIndex = 0;
            for (int colIndex = 0; colIndex < _columns.Length; colIndex++)
            {
                // Tombstoned columns: pad with NULL to keep the encoder's
                // row count in lockstep with the live columns. The bytes
                // are never read (readers filter tombstones), so any
                // value works; NULL is the cheapest.
                DataValue value = _columns[colIndex].IsTombstoned
                    ? DataValue.Null(_columns[colIndex].Kind)
                    : row[batchColIndex++];

                // Capture the homogeneous shape for non-array Struct columns.
                // Array<Struct> columns carry per-element TypeIds in slot
                // bytes — the encoder's allocator path picks those up; no
                // column-level capture needed. Skip nulls and the
                // legacy-no-registry path so writes that don't carry types
                // stay byte-identical to v4.
                if (_typeRegistry is not null
                    && !value.IsNull
                    && _columns[colIndex].Kind == DataKind.Struct
                    && !_columns[colIndex].IsArray)
                {
                    CaptureStructColumnTypeId(colIndex, value.TypeId);
                }

                IPageEncoderV2 encoder = _encoders![colIndex];
                encoder.Append(value, store, _sidecar);
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
    /// Streams a flushed page to disk and records its descriptor with the
    /// absolute file offset where it was written. Called once per
    /// (column, page) pair as encoders fill up.
    /// </summary>
    private void FlushPage(int columnIndex)
    {
        EncodedPageV2 page = _encoders![columnIndex].Flush();
        long offset = _stream.Position;
        _stream.Write(page.Bytes);
        // Source HasNullBitmap from the encoder's actual output — NOT from
        // the current column descriptor. The encoder was constructed when
        // the descriptor was still in its previous shape (e.g. IsNullable
        // false before ALTER … DROP NOT NULL), and produces bytes matching
        // that shape regardless of any descriptor mutation made before
        // flush. The per-page flag must agree with the bytes that were
        // actually written.
        _pageDirectory![columnIndex].Add(new PageDescriptorV2(
            offset,
            (uint)page.Bytes.Length,
            (ushort)page.RowCount,
            page.ZoneMap,
            hasNullBitmap: page.HasNullBitmap));
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

/// <summary>
/// Writer-facing IDENTITY spec passed to <see cref="DatumFileWriterV2.Initialize(IReadOnlyList{ColumnDescriptorV2}, IReadOnlyList{ColumnDefaultV4}?, IdentityWriterSpec?)"/>.
/// Carries the column index, seed, and step; the writer derives the
/// initial next-value from the seed and persists the live counter
/// through subsequent commits.
/// </summary>
public sealed record IdentityWriterSpec(int ColumnIndex, long Seed, long Step, bool AcceptUserValues = false);
