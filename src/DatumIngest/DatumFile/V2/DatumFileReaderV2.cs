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
    private bool _disposed;

    private DatumFileReaderV2(Stream stream, bool ownsStream, HeaderV2 header, FooterV2 footer)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        Header = header;
        Footer = footer;
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
            return Open(stream, ownsStream: true);
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
    /// reader dispose the stream on <see cref="Dispose"/>.
    /// </summary>
    public static DatumFileReaderV2 Open(Stream stream, bool ownsStream)
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

        // Read tail.
        Span<byte> tail = stackalloc byte[DatumFormatV2.TailSize];
        stream.Position = stream.Length - DatumFormatV2.TailSize;
        stream.ReadExactly(tail);
        if (!tail[4..].SequenceEqual(DatumFormatV2.TailMagic))
        {
            throw new InvalidDataException(
                "File tail sentinel does not match v2 'FMTD' magic; the file may be truncated or corrupt.");
        }
        uint footerByteLength = BinaryPrimitives.ReadUInt32LittleEndian(tail[..4]);

        long expectedFooterEnd = stream.Length - DatumFormatV2.TailSize;
        long footerStart = expectedFooterEnd - footerByteLength;
        if (footerStart != header.FooterOffset)
        {
            throw new InvalidDataException(
                $"Footer offset mismatch: header says {header.FooterOffset}, tail says {footerStart}.");
        }

        // Read footer body.
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

        return new DatumFileReaderV2(stream, ownsStream, header, footer);
    }

    /// <summary>
    /// Reads the bytes for the page at <c>(columnIndex, pageIndex)</c>
    /// into a fresh byte array. The returned memory is independent of the
    /// reader and remains valid after the reader is disposed.
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
        _stream.Position = descriptor.PageOffset;
        _stream.ReadExactly(buffer);
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
        DatumIngest.Model.IValueStore? eagerStore = null)
    {
        ColumnFooterV2 column = Footer.Columns[columnIndex];
        PageDescriptorV2 descriptor = column.Pages[pageIndex];
        ReadOnlyMemory<byte> bytes = ReadPageBytes(columnIndex, pageIndex);
        return PageDecoderFactoryV2.Create(
            column.Descriptor, bytes, descriptor.RowCount, sidecarStoreId, sidecarSource, eagerStore);
    }

    /// <summary>Closes the underlying stream when the reader owns it.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsStream)
        {
            _stream.Dispose();
        }
    }
}
