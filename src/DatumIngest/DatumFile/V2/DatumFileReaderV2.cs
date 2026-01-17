using System.Buffers.Binary;
using DatumIngest.DatumFile.V2.Decoding;

namespace DatumIngest.DatumFile.V2;

/// <summary>
/// V2 <c>.datum</c> reader. Opens a v2 file, validates the header/tail
/// magic, parses the footer, and exposes random-access page reads.
/// Higher-level scan and pruning logic (zone-map walk, batch
/// materialization) layers on top via the table provider.
/// </summary>
/// <remarks>
/// First-cut implementation reads pages via FileStream random I/O. Mmap
/// optimization (true zero-copy) is a follow-up; the on-disk format is
/// already mmap-friendly, the reader just doesn't take advantage yet.
/// </remarks>
public sealed class DatumFileReaderV2 : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    /// <summary>
    /// Pack readers for external <c>.datum-pack</c> files referenced by
    /// the footer prologue's file table, indexed by
    /// <see cref="FileTableEntryV4.FileId"/>. <see langword="null"/>
    /// when the primary <c>.datum</c> doesn't reference any external
    /// packs (<see cref="DatumFileFlagsV2.HasExternalPages"/> clear) —
    /// the common case in PR7 where compaction hasn't shipped yet.
    /// </summary>
    private readonly Dictionary<ushort, DatumPackReader>? _packReaders;
    private bool _disposed;

    private DatumFileReaderV2(
        Stream stream, bool ownsStream, HeaderV2 header, FooterV2 footer,
        Dictionary<ushort, DatumPackReader>? packReaders)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        Header = header;
        Footer = footer;
        _packReaders = packReaders;
    }

    /// <summary>The parsed file header (flags, column count, page size, totals).</summary>
    public HeaderV2 Header { get; }

    /// <summary>The parsed footer (column footers + zone-map hierarchies).</summary>
    public FooterV2 Footer { get; }

    /// <summary>Total rows captured in the file (taken from the header).</summary>
    public long TotalRowCount => Header.TotalRowCount;

    /// <summary>
    /// Schema-order column descriptors, with tombstoned (soft-dropped)
    /// columns filtered out. Higher-level consumers (table provider,
    /// query planner) should treat this as the live schema. Use
    /// <see cref="Footer"/>.<see cref="FooterV2.Columns"/> directly to
    /// see every column block including tombstoned ones (e.g. for
    /// compaction).
    /// </summary>
    public IReadOnlyList<ColumnDescriptorV2> Columns =>
        Footer.Columns
            .Where(c => !c.Descriptor.IsTombstoned)
            .Select(c => c.Descriptor)
            .ToArray();

    /// <summary>
    /// Opens a v2 file at the given path. Throws
    /// <see cref="InvalidDataException"/> when magic bytes, version, or
    /// tail sentinel mismatch.
    /// </summary>
    public static DatumFileReaderV2 Open(string filePath)
    {
        // FileShare.ReadWrite lets a concurrent writer hold the file
        // open in append mode without blocking this reader. The reader
        // captures a snapshot of the footer at open and is unaffected
        // by subsequent writes — appending always lands past EOF and
        // commits via tail flip, so existing bytes the reader is
        // pointing at don't move.
        FileStream stream = new(
            filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 65_536, FileOptions.RandomAccess);
        try
        {
            // The base directory for resolving file-table relative
            // paths is the primary .datum file's directory. We pass
            // it through so external .datum-pack files can be opened
            // alongside the primary.
            string? baseDirectory = Path.GetDirectoryName(filePath);
            return OpenInternal(stream, ownsStream: true, baseDirectory);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens a v2 file over an existing seekable stream. Pass
    /// <paramref name="ownsStream"/> = <see langword="true"/> to have the
    /// reader dispose the stream on <see cref="Dispose"/>. Throws when
    /// the file declares <see cref="DatumFileFlagsV2.HasExternalPages"/>
    /// — the stream-based Open has no base directory to resolve
    /// external <c>.datum-pack</c> paths; use the path-based
    /// <see cref="Open(string)"/> for cross-file scenarios.
    /// </summary>
    public static DatumFileReaderV2 Open(Stream stream, bool ownsStream)
        => OpenInternal(stream, ownsStream, baseDirectory: null);

    private static DatumFileReaderV2 OpenInternal(Stream stream, bool ownsStream, string? baseDirectory)
    {
        if (!stream.CanSeek)
        {
            throw new ArgumentException("Stream must be seekable.", nameof(stream));
        }

        if (stream.Length < DatumFormatV2.HeaderSize + DatumFormatV2.TailSize)
        {
            throw new InvalidDataException(
                $"File is too small ({stream.Length} bytes) to be a v2 .datum file.");
        }

        // Read header.
        Span<byte> headerBytes = stackalloc byte[DatumFormatV2.HeaderSize];
        stream.Position = 0;
        stream.ReadExactly(headerBytes);
        HeaderV2 header = HeaderV2.ReadFrom(headerBytes);

        // Read tail. If the trailing bytes aren't a valid FMTD tail —
        // typically a crashed writer that streamed pages past the
        // previous committed tail without ever writing the new one —
        // scan backward for the last good tail and treat that as the
        // logical EOF. The on-disk file isn't modified (readers never
        // truncate); the next writer reopen runs RecoverIfTorn to do
        // the actual cleanup.
        long logicalEof = stream.Length;
        Span<byte> tail = stackalloc byte[DatumFormatV2.TailSize];
        stream.Position = logicalEof - DatumFormatV2.TailSize;
        stream.ReadExactly(tail);

        uint footerByteLength;
        long footerStart;
        if (tail[4..].SequenceEqual(DatumFormatV2.TailMagic))
        {
            footerByteLength = BinaryPrimitives.ReadUInt32LittleEndian(tail[..4]);
            footerStart = logicalEof - DatumFormatV2.TailSize - footerByteLength;
        }
        else
        {
            long recoveredTailEof = TornTailScanner.FindLastCleanTailEof(stream);
            if (recoveredTailEof < 0)
            {
                throw new InvalidDataException(
                    "File tail sentinel does not match v2 'FMTD' magic and no recoverable " +
                    "prior tail was found; the file may be corrupt or never finalized.");
            }
            logicalEof = recoveredTailEof;
            stream.Position = logicalEof - DatumFormatV2.TailSize;
            stream.ReadExactly(tail);
            footerByteLength = BinaryPrimitives.ReadUInt32LittleEndian(tail[..4]);
            footerStart = logicalEof - DatumFormatV2.TailSize - footerByteLength;
        }

        if (footerStart != header.FooterOffset)
        {
            // After recovery the header still points at the *old*
            // footer offset, which matches the recovered tail's footer
            // start — so this check passes for the common torn-tail
            // case. Mismatch here means actual corruption.
            throw new InvalidDataException(
                $"Footer offset mismatch: header says {header.FooterOffset}, tail says {footerStart}.");
        }

        // Read footer body.
        byte[] footerBuffer = new byte[footerByteLength];
        stream.Position = footerStart;
        stream.ReadExactly(footerBuffer);

        bool hasVolumeZoneMaps = (header.Flags & DatumFileFlagsV2.HasVolumeZoneMaps) != 0;
        bool hasTypeTable = (header.Flags & DatumFileFlagsV2.HasTypeTable) != 0;
        FooterV2 footer;
        using (MemoryStream ms = new(footerBuffer, writable: false))
        using (BinaryReader reader = new(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            footer = FooterV2.Deserialize(reader, hasVolumeZoneMaps, hasTypeTable);
        }

        // Header's ColumnCount is informational in v4 — the prologue's
        // ColumnCount is authoritative (per the v4 design, the prologue
        // wins on mismatch so column-add commits don't require a
        // perfectly-atomic header patch). Mismatch is non-fatal but
        // worth surfacing to callers as a sanity-check signal.
        if (header.ColumnCount != footer.Prologue.ColumnCount)
        {
            // Non-fatal: prologue wins. Future PRs may surface this via
            // a logger; for now it's silent because v4 PR1 always writes
            // matching values and a mismatch only ever appears mid-PR4
            // column-add commits which haven't shipped yet.
        }

        // Open external packs referenced by the file table. Reject
        // HasExternalPages without a base directory (stream-based Open
        // can't resolve relative paths).
        Dictionary<ushort, DatumPackReader>? packReaders = null;
        bool hasExternalPages = (header.Flags & DatumFileFlagsV2.HasExternalPages) != 0;
        if (hasExternalPages)
        {
            if (baseDirectory is null)
            {
                throw new InvalidDataException(
                    "File declares HasExternalPages but the reader was opened from a stream " +
                    "with no base directory. Use DatumFileReaderV2.Open(string) for files " +
                    "with cross-file page references.");
            }
            packReaders = OpenPackReaders(footer.Prologue.FileTableEntries, baseDirectory);
        }

        return new DatumFileReaderV2(stream, ownsStream, header, footer, packReaders);
    }

    /// <summary>
    /// Opens one <see cref="DatumPackReader"/> per file-table entry
    /// and returns a dictionary keyed by file id for fast routing in
    /// <see cref="ReadPageBytes"/>. Each pack's fingerprint is
    /// validated against the file-table entry's recorded value;
    /// mismatch raises <see cref="InvalidDataException"/>.
    /// </summary>
    /// <remarks>
    /// Relative paths are resolved against
    /// <paramref name="baseDirectory"/>; absolute paths are used
    /// as-is. The reader takes ownership of the opened pack readers
    /// and disposes them in <see cref="Dispose"/>.
    /// </remarks>
    private static Dictionary<ushort, DatumPackReader> OpenPackReaders(
        IReadOnlyList<FileTableEntryV4> fileTable, string baseDirectory)
    {
        Dictionary<ushort, DatumPackReader> result = new(fileTable.Count);
        try
        {
            foreach (FileTableEntryV4 entry in fileTable)
            {
                if (entry.FileId == DatumFormatV2.LocalFileId)
                {
                    throw new InvalidDataException(
                        $"File-table entry has reserved FileId 0; that id is reserved for the " +
                        $"primary .datum file and must not appear in the table.");
                }
                string resolvedPath = Path.IsPathRooted(entry.RelativePath)
                    ? entry.RelativePath
                    : Path.Combine(baseDirectory, entry.RelativePath);
                result[entry.FileId] = new DatumPackReader(resolvedPath, entry.Fingerprint);
            }
            return result;
        }
        catch
        {
            // On any failure, dispose the packs we managed to open.
            foreach (DatumPackReader pack in result.Values)
            {
                pack.Dispose();
            }
            throw;
        }
    }

    /// <summary>
    /// Reads <paramref name="destination"/>.Length bytes starting at
    /// the given absolute file offset. Used by callers that need
    /// fixed-position reads of file-level structures (e.g. chapter
    /// tombstone bitmaps referenced from the footer prologue) without
    /// going through the column-page indirection.
    /// </summary>
    public void ReadBytesAt(long offset, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _stream.Position = offset;
        _stream.ReadExactly(destination);
    }

    /// <summary>
    /// Loads every chapter's row-tombstone bitmap into memory so
    /// per-page filtering loops can skip soft-deleted rows without an
    /// I/O per page. Returns <see langword="null"/> when the file
    /// declares no tombstones (<see cref="DatumFileFlagsV2.HasTombstones"/>
    /// clear) or the prologue's offset list is empty — fast-path skip
    /// for the common case.
    /// </summary>
    /// <remarks>
    /// Total memory cost = 8 KiB × number of chapters with non-empty
    /// tombstone blocks. For a 1 M-row file (16 chapters) at most
    /// 128 KiB. Loading eagerly at provider/session construction lets
    /// the inner scan loop check a row's tombstone status without
    /// further I/O.
    /// </remarks>
    public byte[]?[]? LoadChapterTombstoneBitmaps()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((Header.Flags & DatumFileFlagsV2.HasTombstones) == 0) return null;

        IReadOnlyList<long> offsets = Footer.Prologue.ChapterTombstoneOffsets;
        if (offsets.Count == 0) return null;

        byte[]?[] result = new byte[]?[offsets.Count];
        for (int c = 0; c < offsets.Count; c++)
        {
            long offset = offsets[c];
            if (offset == DatumFormatV2.NoTombstoneBlock) continue;

            byte[] bytes = new byte[DatumFormatV2.ChapterTombstoneBlockBytes];
            ReadBytesAt(offset, bytes);
            result[c] = bytes;
        }
        return result;
    }

    /// <summary>
    /// Reads the bytes for the page at <c>(columnIndex, pageIndex)</c>
    /// into a fresh byte array. Dispatches by
    /// <see cref="PageDescriptorV2.FileId"/>: <c>0</c> reads from the
    /// primary <c>.datum</c>, non-zero reads from the corresponding
    /// external <c>.datum-pack</c> via the file-table entry. The
    /// returned memory is independent of the reader and remains
    /// valid after the reader is disposed.
    /// </summary>
    public ReadOnlyMemory<byte> ReadPageBytes(int columnIndex, int pageIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ColumnFooterV2 column = Footer.Columns[columnIndex];
        if ((uint)pageIndex >= (uint)column.Pages.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageIndex), pageIndex, $"Column '{column.Descriptor.Name}' has {column.Pages.Count} pages.");
        }

        PageDescriptorV2 descriptor = column.Pages[pageIndex];
        byte[] buffer = new byte[descriptor.PageByteLength];

        if (descriptor.FileId == DatumFormatV2.LocalFileId)
        {
            _stream.Position = descriptor.PageOffset;
            _stream.ReadExactly(buffer);
        }
        else
        {
            if (_packReaders is null || !_packReaders.TryGetValue(descriptor.FileId, out DatumPackReader? pack))
            {
                throw new InvalidDataException(
                    $"Page descriptor for column '{column.Descriptor.Name}' page {pageIndex} " +
                    $"references FileId {descriptor.FileId}, but no matching pack reader was " +
                    "opened. The footer's file table is missing an entry for this id, or the " +
                    "reader was opened without a base directory.");
            }
            pack.ReadBytesAt(descriptor.PageOffset, buffer);
        }
        return buffer;
    }

    /// <summary>
    /// Convenience: builds a decoder for the page at
    /// <c>(columnIndex, pageIndex)</c>. The decoder owns a fresh copy of
    /// the page bytes (loaded synchronously here) and can satisfy random
    /// row reads independently.
    /// </summary>
    internal IPageDecoderV2 OpenPageDecoder(
        int columnIndex,
        int pageIndex,
        byte sidecarStoreId = 0,
        DatumIngest.DatumFile.Sidecar.IBlobSource? sidecarSource = null,
        DatumIngest.Model.IValueStore? eagerStore = null,
        ushort columnRuntimeStructTypeId = 0)
    {
        ColumnFooterV2 column = Footer.Columns[columnIndex];
        PageDescriptorV2 descriptor = column.Pages[pageIndex];
        ReadOnlyMemory<byte> bytes = ReadPageBytes(columnIndex, pageIndex);
        return PageDecoderFactoryV2.Create(
            column.Descriptor, bytes, descriptor.RowCount, sidecarStoreId, sidecarSource, eagerStore,
            columnRuntimeStructTypeId);
    }

    /// <summary>
    /// Closes the underlying stream (when the reader owns it) and
    /// every pack reader opened for the file table.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsStream)
        {
            _stream.Dispose();
        }
        if (_packReaders is not null)
        {
            foreach (DatumPackReader pack in _packReaders.Values)
            {
                pack.Dispose();
            }
        }
    }
}
