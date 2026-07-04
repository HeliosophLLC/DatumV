using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.DatumFile.V2.Encoding;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.DatumFile.V2;

public sealed partial class DatumFileWriterV2
{
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

        // Acquire writer lock before opening the data file. Throws
        // IOException if another writer holds the path. Must precede the
        // FileStream open so a second concurrent OpenForAppend on the
        // same path fails fast at the lock acquire, not at some later
        // point where the file is partially modified. See WriterLockFile
        // for the cross-platform rationale (Windows FileShare is
        // mandatory; Linux's is advisory, so share-based exclusion alone
        // does not portably serialize writers).
        WriterLockFile writerLock = WriterLockFile.AcquireFor(datumPath);

        FileStream stream;
        try
        {
            stream = new FileStream(
                datumPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: 65_536,
                FileOptions.RandomAccess);
        }
        catch
        {
            writerLock.Dispose();
            throw;
        }

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
            writer._writerLock = writerLock;

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

            // Replay decoded partial-page rows into the fresh encoders.
            // Deferred until after the sidecar attach because eagerly-
            // materialized values (Struct scalars) re-encode through the
            // arena path, which needs the write-side blob sink — during
            // rehydrate only the read-side mmap existed.
            writer.ReplayPendingPartialPageRows();

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
            writerLock.Dispose();
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

        // Sidecar convention: foo.datum → foo.datum-blob (extension is
        // REPLACED, not appended — matches the provider's OpenAppendWriter
        // and the read-side reader wiring).
        return Path.ChangeExtension(datumPath, DatumFile.Sidecar.SidecarConstants.FileExtension);
    }

    /// <summary>
    /// Internal constructor used by <see cref="OpenForAppend"/> — does
    /// not write a header (the existing one stays in place) and does
    /// not run <see cref="Initialize(IReadOnlyList{ColumnDescriptorV2})"/> (rehydration drives the schema
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

        // Carry forward column defaults verbatim. AddColumn/MarkColumnTombstoned
        // don't shift indices, so the stored ColumnIndex on each entry stays
        // valid across the append.
        if (footer.Prologue.ColumnDefaults.Count > 0)
        {
            _columnDefaults = new List<ColumnDefaultV4>(footer.Prologue.ColumnDefaults);
        }

        // Carry forward column-computed expressions (v6+). Same ColumnIndex
        // stability as defaults.
        if (footer.ColumnComputeds.Count > 0)
        {
            _columnComputeds = new List<ColumnComputedV4>(footer.ColumnComputeds);
        }

        // Carry forward IDENTITY state. The next-value carries the live
        // counter; AddColumn/MarkColumnTombstoned don't shift indices,
        // so the stored ColumnIndex stays valid across appends.
        _identityColumnIndex = footer.Prologue.IdentityColumnIndex;
        _identitySeed = footer.Prologue.IdentitySeed;
        _identityStep = footer.Prologue.IdentityStep;
        _identityNextValue = footer.Prologue.IdentityNextValue;
        _identityAcceptUserValues = footer.Prologue.IdentityAcceptUserValues;

        // Carry forward PRIMARY KEY column indices verbatim.
        if (footer.Prologue.PrimaryKeyColumnIndices.Count > 0)
        {
            _primaryKeyColumnIndices = footer.Prologue.PrimaryKeyColumnIndices.ToArray();
        }

        int columnCount = footer.Columns.Count;

        // Carry forward the struct type table. Entries re-emit verbatim at
        // finalize (their descriptor blobs are already in the sidecar), and
        // the captured blob bytes let SetTypeRegistry seed the allocator so
        // same-shape values reuse existing on-disk ids instead of colliding.
        // Per-column StructTypeIds carry forward alongside so a session that
        // never writes struct values doesn't strip them from the footer.
        _existingColumnOnDiskStructTypeIds = new ushort?[columnCount];
        for (int colIndex = 0; colIndex < columnCount; colIndex++)
        {
            _existingColumnOnDiskStructTypeIds[colIndex] = footer.Columns[colIndex].StructTypeId;
        }
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
            if (footer.TypeTable.Count > 0)
            {
                sidecarReadStore ??= TryOpenSidecarForRehydrate(_stream, header);
                if (sidecarReadStore is null)
                {
                    throw new InvalidDataException(
                        $"File declares a type table with {footer.TypeTable.Count} entries but its " +
                        "companion .datum-blob sidecar is missing; the descriptor blobs cannot be read.");
                }
                List<(TypeTableEntryV5 Entry, byte[] Blob)> existing = new(footer.TypeTable.Count);
                ushort maxOnDiskId = 0;
                foreach (TypeTableEntryV5 entry in footer.TypeTable)
                {
                    byte[] blob = sidecarReadStore.Read(entry.SidecarOffset, entry.DescriptorLength).ToArray();
                    existing.Add((entry, blob));
                    if (entry.OnDiskTypeId > maxOnDiskId) maxOnDiskId = entry.OnDiskTypeId;
                }
                _existingTypeTableEntries = existing;
                _allocator.EnsureNextOnDiskIdAtLeast((ushort)(maxOnDiskId + 1));
                // SetTypeRegistry seeds the allocator from these blobs; if a
                // registry was attached before rehydrate, seed now instead.
                SeedAllocatorFromExistingTypeTable();
            }

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

    // Rows decoded out of trailing partial pages during rehydrate, waiting
    // for ReplayPendingPartialPageRows once the write-side sidecar is
    // attached. The shared arena holds eagerly-materialized payloads
    // (Struct field blocks) until the replay re-encodes them.
    private List<(int ColumnIndex, Model.DataValue[] Values)>? _pendingPartialPageRows;
    private Model.Arena? _rehydrateArena;

    /// <summary>
    /// Decodes the rows of <paramref name="partialPage"/> and queues them
    /// for <see cref="ReplayPendingPartialPageRows"/> so the encoder's
    /// accumulated row count matches the partial page's row count. New
    /// <see cref="WriteRowBatch"/> calls fill the rest of the page; on
    /// flush the encoder produces fresh page bytes (with a fresh zone map)
    /// at a new file offset. Decode and re-encode are split because the
    /// decode needs the read-side sidecar mmap while re-encode of
    /// eagerly-materialized values needs the write-side sink — on Windows
    /// the two can't be open simultaneously, so OpenForAppend sequences
    /// decode → attach sink → replay.
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

        // Shared read arena absorbing eagerly-materialized children
        // (Struct field arrays). Values that come back as sidecar-pointer
        // DataValues (IsInSidecar = true) flow through the encoder's
        // IsInSidecar fast path and never need reconstitution.
        _rehydrateArena ??= new Model.Arena();
        Decoding.IPageDecoderV2 decoder = Decoding.PageDecoderFactoryV2.Create(
            column,
            new ReadOnlyMemory<byte>(pageBytes),
            partialPage.RowCount,
            sidecarStoreId: 0,
            hasNullBitmap: partialPage.HasNullBitmap,
            sidecarSource: sidecarReadStore,
            eagerStore: _rehydrateArena);

        Model.DataValue[] values = new Model.DataValue[partialPage.RowCount];
        for (int row = 0; row < partialPage.RowCount; row++)
        {
            values[row] = decoder.ReadValue(row);
        }
        _pendingPartialPageRows ??= new List<(int, Model.DataValue[])>();
        _pendingPartialPageRows.Add((columnIndex, values));
    }

    /// <summary>
    /// Re-encodes the partial-page rows queued by
    /// <see cref="ExtendPartialPage"/> into their column encoders. Called
    /// by <see cref="OpenForAppend"/> after the write-side sidecar is
    /// attached so arena-backed payloads (eagerly-decoded Struct scalars)
    /// have a blob sink to spill into.
    /// </summary>
    private void ReplayPendingPartialPageRows()
    {
        if (_pendingPartialPageRows is null) return;

        foreach ((int columnIndex, Model.DataValue[] values) in _pendingPartialPageRows)
        {
            IPageEncoderV2 encoder = _encoders![columnIndex];
            foreach (Model.DataValue value in values)
            {
                encoder.Append(value, _rehydrateArena, _sidecar);
            }
        }
        _pendingPartialPageRows = null;
        _rehydrateArena = null;
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

        string sidecarPath = Path.ChangeExtension(fs.Name, DatumFile.Sidecar.SidecarConstants.FileExtension);
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
        bool hasTypeTable = (header.Flags & DatumFileFlagsV2.HasTypeTable) != 0;
        bool hasColumnComputeds = (header.Flags & DatumFileFlagsV2.HasColumnComputeds) != 0;
        bool hasPrologueExtensions = (header.Flags & DatumFileFlagsV2.HasPrologueExtensions) != 0;
        FooterV2 footer;
        using (MemoryStream ms = new(footerBuffer, writable: false))
        using (BinaryReader reader = new(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            footer = FooterV2.Deserialize(reader, hasVolumeZoneMaps, hasTypeTable, hasColumnComputeds, hasPrologueExtensions);
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
}
