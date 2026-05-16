using System.Buffers.Binary;
using DatumIngest.DatumFile.V2;
using DatumIngest.DatumFile.V2.Decoding;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.DatumFile.V2;

/// <summary>
/// PR7 tests for the cross-file read path: primary <c>.datum</c> with
/// page descriptors that route to external <c>.datum-pack</c> files
/// via the footer prologue's file table. PR7 doesn't ship compaction
/// (the writer-side that produces redirected primaries), so tests
/// hand-construct the multi-file scenario via
/// <see cref="RedirectPagesToPack"/> — read the primary, copy chosen
/// pages out to a pack, rewrite the footer with the redirections,
/// commit. The reader must transparently dispatch
/// <see cref="PageDescriptorV2.FileId"/> = 0 reads to the primary and
/// non-zero ids to the corresponding pack reader.
/// </summary>
public sealed class DatumFileV2MultiFileReadTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_v4_packs_{Guid.NewGuid():N}");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        return Task.CompletedTask;
    }

    [Fact]
    public void PrimaryWithoutExternalPages_OpensWithNullPackTable()
    {
        // The common case (no compaction yet) — file's HasExternalPages
        // bit stays clear and the file table is empty. Reader opens
        // cleanly without trying to resolve any pack.
        string path = WriteSequentialRowsFile("noredirect.datum", rowCount: 100);

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(path);
        Assert.Equal(DatumFileFlagsV2.None, reader.Header.Flags & DatumFileFlagsV2.HasExternalPages);
        Assert.Empty(reader.Footer.Prologue.FileTableEntries);

        // Reads work normally (no pack resolution needed).
        IPageDecoderV2 dec = reader.OpenPageDecoder(columnIndex: 0, pageIndex: 0);
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(i, dec.ReadValue(i).AsInt32());
        }
    }

    [Fact]
    public void RedirectedPage_ReadsFromExternalPack()
    {
        // Write a file with multiple pages, redirect one page's bytes
        // out to a pack, verify the reader returns the same values for
        // that page as a non-redirected baseline.
        const int pageSize = 32;
        const int rowCount = 100;  // 4 pages: 32+32+32+4
        string baselinePath = Path.Combine(_tempDir, "baseline.datum");
        string redirectedPath = Path.Combine(_tempDir, "redirected.datum");
        string packPath = Path.Combine(_tempDir, "pack_001.datum-pack");

        WriteSequentialRowsFileTo(baselinePath, rowCount, pageSize);
        File.Copy(baselinePath, redirectedPath);

        // Redirect page 1 (rows 32..63) to the pack.
        RedirectPagesToPack(redirectedPath, [(0, 1)], packPath,
            packFileId: 7, packRelativePath: "pack_001.datum-pack");

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(redirectedPath);

        // Sanity: HasExternalPages flipped, file table populated.
        Assert.True((reader.Header.Flags & DatumFileFlagsV2.HasExternalPages) != 0);
        Assert.Single(reader.Footer.Prologue.FileTableEntries);
        Assert.Equal(7, reader.Footer.Prologue.FileTableEntries[0].FileId);

        // Page 1's descriptor now has FileId=7.
        var pages = reader.Footer.Columns[0].Pages;
        Assert.Equal(0, pages[0].FileId);
        Assert.Equal(7, pages[1].FileId);
        Assert.Equal(0, pages[2].FileId);
        Assert.Equal(0, pages[3].FileId);

        // Reads against the redirected page produce the original values.
        for (int p = 0; p < pages.Count; p++)
        {
            IPageDecoderV2 dec = reader.OpenPageDecoder(columnIndex: 0, pageIndex: p);
            int firstRow = p * pageSize;
            for (int i = 0; i < dec.RowCount; i++)
            {
                Assert.Equal(firstRow + i, dec.ReadValue(i).AsInt32());
            }
        }
    }

    [Fact]
    public void MultipleRedirectsToSamePack_ReadCorrectly()
    {
        // Move several non-contiguous pages into the same pack file.
        // Reader should resolve each independently.
        const int pageSize = 32;
        const int rowCount = 100;
        string path = Path.Combine(_tempDir, "multi_redirect.datum");
        string packPath = Path.Combine(_tempDir, "multi_pack.datum-pack");

        WriteSequentialRowsFileTo(path, rowCount, pageSize);
        RedirectPagesToPack(path, [(0, 0), (0, 2)], packPath,
            packFileId: 3, packRelativePath: "multi_pack.datum-pack");

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(path);
        var pages = reader.Footer.Columns[0].Pages;
        Assert.Equal(3, pages[0].FileId);
        Assert.Equal(0, pages[1].FileId);
        Assert.Equal(3, pages[2].FileId);
        Assert.Equal(0, pages[3].FileId);

        // Verify all rows still round-trip.
        int rowIndex = 0;
        for (int p = 0; p < pages.Count; p++)
        {
            IPageDecoderV2 dec = reader.OpenPageDecoder(0, p);
            for (int i = 0; i < dec.RowCount; i++)
            {
                Assert.Equal(rowIndex, dec.ReadValue(i).AsInt32());
                rowIndex++;
            }
        }
        Assert.Equal(rowCount, rowIndex);
    }

    [Fact]
    public void RedirectedPagesInDifferentPacks_BothResolved()
    {
        // Two pack files referenced by different file ids — reader
        // opens both and dispatches by id.
        const int pageSize = 32;
        const int rowCount = 100;
        string path = Path.Combine(_tempDir, "two_packs.datum");
        string packAPath = Path.Combine(_tempDir, "pack_a.datum-pack");
        string packBPath = Path.Combine(_tempDir, "pack_b.datum-pack");

        WriteSequentialRowsFileTo(path, rowCount, pageSize);
        // Move page 0 to pack A (id=5).
        RedirectPagesToPack(path, [(0, 0)], packAPath, 5, "pack_a.datum-pack");
        // Move page 2 to pack B (id=8).
        RedirectPagesToPack(path, [(0, 2)], packBPath, 8, "pack_b.datum-pack");

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(path);
        Assert.Equal(2, reader.Footer.Prologue.FileTableEntries.Count);
        var pages = reader.Footer.Columns[0].Pages;
        Assert.Equal(5, pages[0].FileId);
        Assert.Equal(8, pages[2].FileId);

        int rowIndex = 0;
        for (int p = 0; p < pages.Count; p++)
        {
            IPageDecoderV2 dec = reader.OpenPageDecoder(0, p);
            for (int i = 0; i < dec.RowCount; i++)
            {
                Assert.Equal(rowIndex++, dec.ReadValue(i).AsInt32());
            }
        }
    }

    [Fact]
    public void FingerprintMismatch_RaisesClearError()
    {
        // Tamper with a pack's fingerprint after the primary's
        // file-table entry was written. Reader should refuse to open
        // with a clear error pointing at staleness.
        string path = Path.Combine(_tempDir, "fingerprint_mismatch.datum");
        string packPath = Path.Combine(_tempDir, "stale_pack.datum-pack");

        WriteSequentialRowsFileTo(path, rowCount: 100, pageSize: 32);
        RedirectPagesToPack(path, [(0, 1)], packPath, packFileId: 1, packRelativePath: "stale_pack.datum-pack");

        // Corrupt the pack's fingerprint bytes (offset 16, 16 bytes).
        using (FileStream fs = new(packPath, FileMode.Open, FileAccess.Write))
        {
            fs.Position = DatumPackConstants.FingerprintOffset;
            byte[] tampered = new byte[DatumPackConstants.FingerprintBytes];
            new Random(1).NextBytes(tampered);
            fs.Write(tampered);
        }

        InvalidDataException ex = Assert.Throws<InvalidDataException>(
            () => DatumFileReaderV2.Open(path));
        Assert.Contains("fingerprint mismatch", ex.Message);
    }

    [Fact]
    public void MissingPackFile_RaisesFileNotFound()
    {
        // Primary references a pack that has been deleted. Reader
        // open should fail with FileNotFoundException naming the
        // missing pack.
        string path = Path.Combine(_tempDir, "missing_pack.datum");
        string packPath = Path.Combine(_tempDir, "removed.datum-pack");

        WriteSequentialRowsFileTo(path, rowCount: 100, pageSize: 32);
        RedirectPagesToPack(path, [(0, 1)], packPath, packFileId: 1, packRelativePath: "removed.datum-pack");

        File.Delete(packPath);

        FileNotFoundException ex = Assert.Throws<FileNotFoundException>(
            () => DatumFileReaderV2.Open(path));
        Assert.Contains("removed.datum-pack", ex.Message);
    }

    [Fact]
    public void StreamBasedOpen_RejectsExternalPages()
    {
        // The stream-based Open has no base directory to resolve
        // relative pack paths against. When the file declares
        // HasExternalPages, that overload must throw.
        string path = Path.Combine(_tempDir, "stream_open_with_packs.datum");
        string packPath = Path.Combine(_tempDir, "p.datum-pack");

        WriteSequentialRowsFileTo(path, rowCount: 100, pageSize: 32);
        RedirectPagesToPack(path, [(0, 1)], packPath, packFileId: 1, packRelativePath: "p.datum-pack");

        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        InvalidDataException ex = Assert.Throws<InvalidDataException>(
            () => DatumFileReaderV2.Open(fs, ownsStream: false));
        Assert.Contains("HasExternalPages", ex.Message);
    }

    [Fact]
    public void DatumPackWriter_FingerprintIsRandom()
    {
        // Two consecutive writers produce different fingerprints —
        // confirms the random-per-write generator (rather than e.g.
        // a deterministic hash of the file).
        string p1 = Path.Combine(_tempDir, "rng1.datum-pack");
        string p2 = Path.Combine(_tempDir, "rng2.datum-pack");

        byte[] f1, f2;
        using (DatumPackWriter w1 = new(p1)) { f1 = w1.Fingerprint; }
        using (DatumPackWriter w2 = new(p2)) { f2 = w2.Fingerprint; }

        Assert.NotEqual(f1, f2);
        Assert.Equal(DatumPackConstants.FingerprintBytes, f1.Length);
    }

    [Fact]
    public void DatumPackReader_RejectsBadMagic()
    {
        // Hand-write a file whose first 8 bytes aren't the pack magic.
        string path = Path.Combine(_tempDir, "bad_magic.datum-pack");
        byte[] bytes = new byte[DatumPackConstants.HeaderSize];
        // Leave magic bytes as zeros (invalid).
        File.WriteAllBytes(path, bytes);

        byte[] anyFingerprint = new byte[DatumPackConstants.FingerprintBytes];
        InvalidDataException ex = Assert.Throws<InvalidDataException>(
            () => new DatumPackReader(path, anyFingerprint));
        Assert.Contains("magic mismatch", ex.Message);
    }

    // ──────────────────── Helpers ────────────────────

    private string WriteSequentialRowsFile(string fileName, int rowCount)
    {
        string path = Path.Combine(_tempDir, fileName);
        WriteSequentialRowsFileTo(path, rowCount, pageSize: DatumFormatV2.DefaultPageSize);
        return path;
    }

    private void WriteSequentialRowsFileTo(string path, int rowCount, int pageSize)
    {
        ColumnDescriptorV2 col = new("v", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);
        Pool pool = new(new PoolBacking());
        ColumnLookup lookup = new(["v"]);
        Arena arena = CreateArena();
        RowBatch batch = pool.RentRowBatch(lookup, capacity: rowCount, arena: arena);
        for (int i = 0; i < rowCount; i++)
        {
            DataValue[] row = pool.RentDataValues(1);
            row[0] = DataValue.FromInt32(i);
            batch.Add(row);
        }
        using DatumFileWriterV2 writer = new(path, sidecarPath: null);
        if (pageSize != DatumFormatV2.DefaultPageSize) writer.SetPageSize(pageSize);
        writer.Initialize([col]);
        writer.WriteRowBatch(batch);
        writer.FinalizeWriter();
    }

    /// <summary>
    /// Builds a multi-file scenario by redirecting the named
    /// (column, page) descriptors of <paramref name="primaryPath"/>
    /// out to a fresh pack file at <paramref name="packPath"/>. The
    /// primary's footer is rewritten with: a new file-table entry,
    /// the redirected pages' descriptors updated to point at the pack,
    /// and the <see cref="DatumFileFlagsV2.HasExternalPages"/> flag
    /// set in the header. Existing tail bytes become orphan past the
    /// new tail.
    /// </summary>
    private static void RedirectPagesToPack(
        string primaryPath,
        (int ColumnIndex, int PageIndex)[] redirections,
        string packPath,
        ushort packFileId,
        string packRelativePath)
    {
        // Step 1: read pages from primary, write them to the new pack.
        Dictionary<(int, int), long> packOffsets = new();
        byte[] packFingerprint;

        using DatumPackWriter packWriter = new(packPath);
        packFingerprint = packWriter.Fingerprint;

        FooterV2 originalFooter;
        HeaderV2 originalHeader;
        using (DatumFileReaderV2 reader = DatumFileReaderV2.Open(primaryPath))
        {
            originalFooter = reader.Footer;
            originalHeader = reader.Header;
            foreach ((int colIdx, int pageIdx) in redirections)
            {
                ReadOnlyMemory<byte> pageBytes = reader.ReadPageBytes(colIdx, pageIdx);
                long packOffset = packWriter.AppendPage(pageBytes.Span);
                packOffsets[(colIdx, pageIdx)] = packOffset;
            }
        }
        packWriter.Dispose();

        // Step 2: build a new footer with the redirected page
        // descriptors and an updated file-table entry. We also append
        // any existing file-table entries so a primary with multiple
        // packs can be built up incrementally by repeated calls.
        ColumnFooterV2[] newColumns = new ColumnFooterV2[originalFooter.Columns.Count];
        for (int colIdx = 0; colIdx < originalFooter.Columns.Count; colIdx++)
        {
            ColumnFooterV2 colFooter = originalFooter.Columns[colIdx];
            PageDescriptorV2[] newPages = new PageDescriptorV2[colFooter.Pages.Count];
            for (int pageIdx = 0; pageIdx < colFooter.Pages.Count; pageIdx++)
            {
                PageDescriptorV2 p = colFooter.Pages[pageIdx];
                if (packOffsets.TryGetValue((colIdx, pageIdx), out long newOffset))
                {
                    newPages[pageIdx] = p with { FileId = packFileId, PageOffset = newOffset };
                }
                else
                {
                    newPages[pageIdx] = p;
                }
            }
            newColumns[colIdx] = colFooter with { Pages = newPages };
        }

        List<FileTableEntryV4> newFileTable = new(originalFooter.Prologue.FileTableEntries);
        newFileTable.Add(new FileTableEntryV4(packFileId, packRelativePath, packFingerprint));

        FooterPrologueV4 newPrologue = originalFooter.Prologue with
        {
            Generation = originalFooter.Prologue.Generation + 1,
            BaseGeneration = originalFooter.Prologue.Generation,
            FileTableEntries = newFileTable,
        };

        FooterV2 newFooter = new(newPrologue, newColumns, originalFooter.HasVolumeZoneMaps);

        // Step 3: append new footer + tail past the existing data; patch header.
        using FileStream fs = new(primaryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        fs.Position = fs.Length;
        long newFooterOffset = fs.Position;

        using (MemoryStream footerScratch = new())
        {
            using (BinaryWriter footerWriter = new(footerScratch, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                newFooter.Serialize(footerWriter, hasTypeTable: false);
            }
            footerScratch.Position = 0;
            footerScratch.CopyTo(fs);
            uint footerLen = checked((uint)footerScratch.Length);

            Span<byte> tail = stackalloc byte[DatumFormatV2.TailSize];
            BinaryPrimitives.WriteUInt32LittleEndian(tail[..4], footerLen);
            DatumFormatV2.TailMagic.CopyTo(tail[4..]);
            fs.Write(tail);
        }

        // Patch header: flip HasExternalPages, update FooterOffset.
        DatumFileFlagsV2 newFlags = originalHeader.Flags | DatumFileFlagsV2.HasExternalPages;
        fs.Position = 0;
        Span<byte> newHeaderBytes = stackalloc byte[DatumFormatV2.HeaderSize];
        new HeaderV2(
            newFlags,
            ColumnCount: originalHeader.ColumnCount,
            PageSize: originalHeader.PageSize,
            TotalRowCount: originalHeader.TotalRowCount,
            FooterOffset: newFooterOffset).WriteTo(newHeaderBytes);
        fs.Write(newHeaderBytes);
    }
}
