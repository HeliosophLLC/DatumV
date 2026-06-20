using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.DatumFile.V2.Encoding;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.DatumFile.V2;

public sealed partial class DatumFileWriterV2
{
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

        // Compose column footers + zone-map hierarchies. ResolveColumn
        // StructTypeIdForFooter taps the per-column homogeneous-shape
        // capture from WriteRowBatch and runs each runtime TypeId through
        // the on-disk allocator — so the column footer stores stable ids
        // that match the entries we'll flush in EmitTypeTable below.
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
                volumes,
                StructTypeId: ResolveColumnStructTypeIdForFooter(colIndex));
        }

        // Emit the per-file TypeTable. Order matters: this must run after
        // ResolveColumnStructTypeIdForFooter (which can register late
        // column-level TypeIds with the allocator) but before footer
        // serialization (so the entries are part of the same on-disk
        // commit). EmitTypeTable also performs the sidecar appends for
        // the descriptor blobs themselves.
        IReadOnlyList<TypeTableEntryV5> typeTable = EmitTypeTable();

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
        // HasTypeTable signals readers to parse the trailing type-table
        // block. Set only when EmitTypeTable produced entries — files
        // that never saw a struct stay v4-shaped and skip the read path.
        if (typeTable.Count > 0)
        {
            flags |= DatumFileFlagsV2.HasTypeTable;
        }
        // HasColumnComputeds: set when at least one column carries a
        // GENERATED ALWAYS AS expression. Tells the reader to parse the
        // trailing computed-columns block.
        bool hasColumnComputeds = _columnComputeds is { Count: > 0 };
        if (hasColumnComputeds)
        {
            flags |= DatumFileFlagsV2.HasColumnComputeds;
        }
        // HasPrologueExtensions: set only when the prologue carries v7
        // extension entries. The writer doesn't emit any today, so this
        // stays clear in all currently-produced files; the reader still
        // honors the flag so a future writer can populate the block
        // without invalidating today's readers.
        bool hasPrologueExtensions = false;
        if (hasPrologueExtensions)
        {
            flags |= DatumFileFlagsV2.HasPrologueExtensions;
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
            ChapterTombstoneOffsets: chapterTombstoneOffsets,
            ColumnDefaults: _columnDefaults is { Count: > 0 }
                ? _columnDefaults.ToArray()
                : Array.Empty<ColumnDefaultV4>(),
            IdentityColumnIndex: _identityColumnIndex,
            IdentitySeed: _identitySeed,
            IdentityStep: _identityStep,
            IdentityNextValue: _identityNextValue,
            IdentityAcceptUserValues: _identityAcceptUserValues,
            PrimaryKeyColumnIndices: _primaryKeyColumnIndices ?? Array.Empty<ushort>(),
            Extensions: Array.Empty<PrologueExtensionV7>());

        IReadOnlyList<ColumnComputedV4> computedsForFooter = _columnComputeds is { Count: > 0 }
            ? _columnComputeds.ToArray()
            : Array.Empty<ColumnComputedV4>();
        FooterV2 footer = new(prologue, columnFooters, emitVolumes, typeTable, computedsForFooter);

        // Write the footer body, capture offset and length.
        long footerOffset = _stream.Position;
        using (MemoryStream footerScratch = new())
        using (BinaryWriter footerWriter = new(footerScratch, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            footer.Serialize(footerWriter,
                hasTypeTable: (flags & DatumFileFlagsV2.HasTypeTable) != 0,
                hasColumnComputeds: hasColumnComputeds,
                hasPrologueExtensions: hasPrologueExtensions);
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
}
