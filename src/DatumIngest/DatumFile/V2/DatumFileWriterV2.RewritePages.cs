using System.Buffers.Binary;
using DatumIngest.DatumFile.Sidecar;
using DatumIngest.DatumFile.V2.Decoding;
using DatumIngest.DatumFile.V2.Encoding;
using DatumIngest.Model;

namespace DatumIngest.DatumFile.V2;

/// <summary>
/// Page-COW rewrite primitive (PR11b). Lets the catalog's UPDATE path
/// replace specific row values inside specific pages of an existing
/// <c>.datum</c> file without renumbering rows or rewriting unaffected
/// pages.
/// </summary>
/// <remarks>
/// <para>
/// For each <c>(pageIndex, columnIndex)</c> pair touched by an update,
/// the rewriter reads the existing page's bytes, decodes every row,
/// substitutes the new value at each updated row, and re-encodes a fresh
/// page. The new bytes are appended past the current end of file; a new
/// footer (with the affected <see cref="PageDescriptorV2"/> entries
/// pointing at the new offsets, and refreshed chapter/volume zone maps
/// for every column whose page directory changed) and tail are written
/// after that. Tail-flip-as-commit makes the rewrite atomic: a crash
/// mid-rewrite leaves the old footer/tail addressable from EOF, and
/// <see cref="DatumFileWriterV2.OpenForAppend"/>'s torn-tail recovery
/// truncates the partial work the next time the file is opened for
/// write.
/// </para>
/// <para>
/// Old page bytes leak (the page directory points at the new offsets,
/// the old bytes are unreachable). A future compaction pass reclaims
/// them; until then the leak is bounded by the affected-column-page
/// width × number of rewrites.
/// </para>
/// </remarks>
public sealed partial class DatumFileWriterV2
{
    /// <summary>
    /// Replaces specific row values inside specific pages of an existing
    /// <c>.datum</c> file. See class remarks for the page-COW semantics.
    /// </summary>
    /// <param name="datumPath">Path to the <c>.datum</c> file to modify.</param>
    /// <param name="sidecarPath">
    /// Path to the companion <c>.datum-blob</c> sidecar. Required when any
    /// rewritten column uses <see cref="EncoderKind.VariableSlot"/> and a
    /// new value cannot fit inline; the rewriter opens it in append mode
    /// and hands it to the encoder. Pass <see langword="null"/> when
    /// updating only fixed-width / boolean columns.
    /// </param>
    /// <param name="updatesByPage">
    /// Map from page index → list of row updates to apply within that
    /// page. Each <see cref="RowUpdate"/> names a row by its
    /// <see cref="RowUpdate.RowInPage"/> position (0-based, inside the
    /// page) and supplies a sparse map of column index → new value. A
    /// page is "affected" iff at least one row update touches it; a
    /// column-page is "affected" iff at least one row update inside that
    /// page references the column. Unaffected column-pages keep their
    /// existing on-disk bytes and descriptor verbatim.
    /// </param>
    /// <param name="sourceStore">
    /// Backing store for any non-inline <see cref="DataValue"/> in
    /// <paramref name="updatesByPage"/>. Pass <see langword="null"/> when
    /// every supplied value is inline (numerics, short strings,
    /// fixed-width arrays that fit in 16 bytes, …).
    /// </param>
    /// <exception cref="ArgumentException">
    /// A page index, row-in-page, or column index is out of range for
    /// the file's existing page directory.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A rewritten column has an encoder that this primitive does not
    /// yet support (e.g. eager-Struct decoding requires a sidecar source
    /// the rewriter does not currently open).
    /// </exception>
    public static void RewritePages(
        string datumPath,
        string? sidecarPath,
        IReadOnlyDictionary<int, IReadOnlyList<RowUpdate>> updatesByPage,
        IValueStore? sourceStore = null)
    {
        ArgumentNullException.ThrowIfNull(datumPath);
        ArgumentNullException.ThrowIfNull(updatesByPage);

        if (updatesByPage.Count == 0)
        {
            // Nothing to do.
            return;
        }

        using FileStream stream = new(
            datumPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 65_536,
            FileOptions.RandomAccess);

        // Recover any prior torn tail so we operate on a known-good
        // baseline. After this returns, the file ends with a valid
        // tail/footer pair.
        RecoverIfTorn(stream);

        (HeaderV2 header, FooterV2 footer) = LoadHeaderAndFooter(stream);

        ValidateUpdates(footer, updatesByPage);

        // Determine whether any rewritten column will use the
        // VariableSlot encoder; only then do we need the sidecar.
        bool needsSidecar = NeedsSidecarForRewrites(footer, updatesByPage);
        SidecarWriteStore? sidecarStore = null;
        CountingBlobSink? countingSidecar = null;
        if (needsSidecar)
        {
            if (sidecarPath is null)
            {
                throw new InvalidOperationException(
                    "RewritePages: at least one rewritten column uses the VariableSlot " +
                    "encoder, which requires a sidecarPath in case a new value spills " +
                    "to the .datum-blob sidecar. Pass the companion sidecar path or " +
                    "restrict updates to fixed-width / boolean columns.");
            }
            sidecarStore = SidecarWriteStore.OpenForAppend(sidecarPath);
            countingSidecar = new CountingBlobSink(sidecarStore);
        }

        try
        {
            // Walk past the existing footer/tail. New page bytes append at
            // the end; the old footer/tail bytes survive between the last
            // old page and the first new page, but become unreachable once
            // the new tail is written.
            stream.Position = stream.Length;

            // Per-(column, page) replacement descriptors.
            Dictionary<(int Column, int Page), PageDescriptorV2> replacements = new();

            // Process pages in deterministic order so disk layout is
            // reproducible across identical inputs (helps test diffs).
            List<int> orderedPages = new(updatesByPage.Keys);
            orderedPages.Sort();

            byte[] readBuffer = Array.Empty<byte>();

            foreach (int pageIndex in orderedPages)
            {
                IReadOnlyList<RowUpdate> rowUpdates = updatesByPage[pageIndex];

                // Find the set of columns touched by any row update on
                // this page. Only those column-pages get rewritten.
                HashSet<int> affectedColumns = new();
                foreach (RowUpdate row in rowUpdates)
                {
                    foreach (int columnIndex in row.ColumnValues.Keys)
                    {
                        affectedColumns.Add(columnIndex);
                    }
                }

                foreach (int columnIndex in affectedColumns)
                {
                    ColumnDescriptorV2 columnDescriptor = footer.Columns[columnIndex].Descriptor;
                    PageDescriptorV2 oldPage = footer.Columns[columnIndex].Pages[pageIndex];
                    int rowCount = oldPage.RowCount;

                    // Build a row-in-page → new-value lookup for THIS
                    // column-page. Rows without an entry keep their
                    // decoded existing value.
                    Dictionary<int, DataValue> rowOverrides = new();
                    foreach (RowUpdate row in rowUpdates)
                    {
                        if (row.ColumnValues.TryGetValue(columnIndex, out DataValue newValue))
                        {
                            rowOverrides[row.RowInPage] = newValue;
                        }
                    }

                    // Read the existing page bytes. The seek + read here
                    // doesn't disturb the appended-pages cursor — we
                    // re-seek to EOF before writing the new page.
                    if (readBuffer.Length < oldPage.PageByteLength)
                    {
                        readBuffer = new byte[oldPage.PageByteLength];
                    }
                    stream.Position = oldPage.PageOffset;
                    stream.ReadExactly(readBuffer, 0, (int)oldPage.PageByteLength);

                    ReadOnlyMemory<byte> pageMemory = new(readBuffer, 0, (int)oldPage.PageByteLength);
                    IPageDecoderV2 decoder = PageDecoderFactoryV2.Create(
                        columnDescriptor,
                        pageMemory,
                        rowCount,
                        sidecarStoreId: 0,
                        sidecarSource: null,
                        eagerStore: null);

                    // Per-page encoder sized exactly to this page's row
                    // count. Construct fresh — encoders aren't designed
                    // for restart across pages of different sizes.
                    IPageEncoderV2 encoder = PageEncoderFactoryV2.Create(columnDescriptor, rowCount);

                    for (int row = 0; row < rowCount; row++)
                    {
                        DataValue value = rowOverrides.TryGetValue(row, out DataValue overrideValue)
                            ? overrideValue
                            : decoder.ReadValue(row);
                        encoder.Append(value, sourceStore, countingSidecar);
                    }

                    EncodedPageV2 encoded = encoder.Flush();

                    // Append the new page bytes at EOF. Read above seeked
                    // back to oldPage.PageOffset; reset to current EOF so
                    // the new page lands past every prior write.
                    stream.Position = stream.Length;
                    long newPageOffset = stream.Position;
                    stream.Write(encoded.Bytes);

                    PageDescriptorV2 newDescriptor = new(
                        oldPage.FileId,
                        newPageOffset,
                        (uint)encoded.Bytes.Length,
                        (ushort)encoded.RowCount,
                        encoded.ZoneMap);

                    replacements[(columnIndex, pageIndex)] = newDescriptor;
                }
            }

            // Build patched column footers. For each column whose page
            // directory had any replacement, rebuild the chapter (and
            // volume, when applicable) zone-map hierarchy from the new
            // page list. Untouched columns keep their existing footer
            // verbatim.
            ColumnFooterV2[] newColumnFooters = BuildNewColumnFooters(footer, replacements);

            // Determine whether any new sidecar payloads landed during
            // this rewrite, AND whether the file already had sidecar
            // references. The flag is sticky across commits — once any
            // commit set it, it stays on (existing pointer slots still
            // reference sidecar bytes regardless of what this rewrite
            // wrote).
            bool hadSidecarRefs = (header.Flags & DatumFileFlagsV2.HasSidecarReferences) != 0;
            bool addedSidecarRefs = countingSidecar is { AppendCount: > 0 };
            bool hasSidecarRefs = hadSidecarRefs || addedSidecarRefs;

            // Bump generation; preserve every other prologue field
            // verbatim. Row count stays the same — UPDATE doesn't add or
            // remove rows.
            FooterPrologueV4 oldProlog = footer.Prologue;
            FooterPrologueV4 newProlog = new(
                Generation: oldProlog.Generation + 1,
                WriterId: WriterIdentity.Default,
                BaseGeneration: oldProlog.Generation,
                TombstoneGranularity: oldProlog.TombstoneGranularity,
                ColumnCount: oldProlog.ColumnCount,
                FileTableEntries: oldProlog.FileTableEntries,
                ChapterTombstoneOffsets: oldProlog.ChapterTombstoneOffsets,
                ColumnDefaults: oldProlog.ColumnDefaults,
                IdentityColumnIndex: oldProlog.IdentityColumnIndex,
                IdentitySeed: oldProlog.IdentitySeed,
                IdentityStep: oldProlog.IdentityStep,
                IdentityNextValue: oldProlog.IdentityNextValue,
                PrimaryKeyColumnIndices: oldProlog.PrimaryKeyColumnIndices);

            FooterV2 newFooter = new(newProlog, newColumnFooters, footer.HasVolumeZoneMaps);

            // Serialize footer body, append, then write the tail.
            stream.Position = stream.Length;
            long newFooterOffset = stream.Position;
            using (MemoryStream footerScratch = new())
            using (BinaryWriter footerWriter = new(footerScratch, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                newFooter.Serialize(footerWriter);
                footerWriter.Flush();
                footerScratch.Position = 0;
                footerScratch.CopyTo(stream);
                uint footerLength = checked((uint)footerScratch.Length);

                Span<byte> tail = stackalloc byte[DatumFormatV2.TailSize];
                BinaryPrimitives.WriteUInt32LittleEndian(tail[..4], footerLength);
                DatumFormatV2.TailMagic.CopyTo(tail[4..]);
                stream.Write(tail);
            }

            // Patch the header in place. Flags get the (possibly updated)
            // sidecar flag; everything else is preserved.
            DatumFileFlagsV2 newFlags = (header.Flags & ~DatumFileFlagsV2.HasSidecarReferences)
                | (hasSidecarRefs ? DatumFileFlagsV2.HasSidecarReferences : 0);
            HeaderV2 newHeader = new(
                newFlags,
                ColumnCount: header.ColumnCount,
                PageSize: header.PageSize,
                TotalRowCount: header.TotalRowCount,
                FooterOffset: newFooterOffset);

            stream.Position = 0;
            Span<byte> headerScratch = stackalloc byte[DatumFormatV2.HeaderSize];
            newHeader.WriteTo(headerScratch);
            stream.Write(headerScratch);
            stream.Flush();
        }
        finally
        {
            sidecarStore?.Dispose();
        }
    }

    private static void ValidateUpdates(
        FooterV2 footer,
        IReadOnlyDictionary<int, IReadOnlyList<RowUpdate>> updatesByPage)
    {
        int columnCount = footer.Columns.Count;

        // Every column has the same number of pages with the same row
        // counts (column-major page directory aligned by row position).
        // Validate against column 0's page directory; any divergence
        // would already have failed file-load.
        IReadOnlyList<PageDescriptorV2> referencePages = footer.Columns[0].Pages;
        int pageCount = referencePages.Count;

        foreach (KeyValuePair<int, IReadOnlyList<RowUpdate>> kvp in updatesByPage)
        {
            int pageIndex = kvp.Key;
            if ((uint)pageIndex >= (uint)pageCount)
            {
                throw new ArgumentException(
                    $"RewritePages: page index {pageIndex} is out of range " +
                    $"(file has {pageCount} page(s)).",
                    nameof(updatesByPage));
            }

            int rowsInThisPage = referencePages[pageIndex].RowCount;
            IReadOnlyList<RowUpdate> rows = kvp.Value;
            for (int i = 0; i < rows.Count; i++)
            {
                RowUpdate row = rows[i];
                if ((uint)row.RowInPage >= (uint)rowsInThisPage)
                {
                    throw new ArgumentException(
                        $"RewritePages: page {pageIndex} row-in-page {row.RowInPage} is " +
                        $"out of range (page has {rowsInThisPage} row(s)).",
                        nameof(updatesByPage));
                }
                foreach (int columnIndex in row.ColumnValues.Keys)
                {
                    if ((uint)columnIndex >= (uint)columnCount)
                    {
                        throw new ArgumentException(
                            $"RewritePages: page {pageIndex} row {row.RowInPage} references " +
                            $"column index {columnIndex} (file has {columnCount} column(s)).",
                            nameof(updatesByPage));
                    }
                }
            }
        }
    }

    private static bool NeedsSidecarForRewrites(
        FooterV2 footer,
        IReadOnlyDictionary<int, IReadOnlyList<RowUpdate>> updatesByPage)
    {
        foreach (IReadOnlyList<RowUpdate> rows in updatesByPage.Values)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                foreach (int columnIndex in rows[i].ColumnValues.Keys)
                {
                    if (footer.Columns[columnIndex].Descriptor.Encoder == EncoderKind.VariableSlot)
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    private static ColumnFooterV2[] BuildNewColumnFooters(
        FooterV2 footer,
        IReadOnlyDictionary<(int Column, int Page), PageDescriptorV2> replacements)
    {
        // Pre-compute per-column "did anything change" so untouched
        // columns avoid the zone-map rebuild work.
        bool[] columnTouched = new bool[footer.Columns.Count];
        foreach ((int column, _) in replacements.Keys)
        {
            columnTouched[column] = true;
        }

        ColumnFooterV2[] result = new ColumnFooterV2[footer.Columns.Count];
        for (int columnIndex = 0; columnIndex < footer.Columns.Count; columnIndex++)
        {
            ColumnFooterV2 oldFooter = footer.Columns[columnIndex];

            if (!columnTouched[columnIndex])
            {
                result[columnIndex] = oldFooter;
                continue;
            }

            PageDescriptorV2[] newPages = new PageDescriptorV2[oldFooter.Pages.Count];
            for (int pageIndex = 0; pageIndex < newPages.Length; pageIndex++)
            {
                newPages[pageIndex] = replacements.TryGetValue((columnIndex, pageIndex), out PageDescriptorV2? replaced)
                    ? replaced
                    : oldFooter.Pages[pageIndex];
            }

            ZoneMapHierarchyBuilderV2 hb = new();
            for (int pageIndex = 0; pageIndex < newPages.Length; pageIndex++)
            {
                DatumZoneMap pageMap = newPages[pageIndex].ZoneMap
                    ?? new DatumZoneMap(nullCount: 0u);
                hb.AddPage(pageMap);
            }
            (IReadOnlyList<DatumZoneMap> chapters, IReadOnlyList<DatumZoneMap>? volumes) =
                hb.Finalize(footer.HasVolumeZoneMaps);

            result[columnIndex] = new ColumnFooterV2(
                oldFooter.Descriptor,
                newPages,
                chapters,
                volumes);
        }
        return result;
    }
}

/// <summary>
/// One row's worth of updates inside a single page. Used by
/// <see cref="DatumFileWriterV2.RewritePages"/> to convey "at row
/// <see cref="RowInPage"/>, set columns named in <see cref="ColumnValues"/>
/// to the supplied values; leave other columns at their decoded values."
/// </summary>
/// <param name="RowInPage">
/// 0-based position of the row inside the page. Must be in
/// <c>[0, page.RowCount)</c> for the page identified by the enclosing
/// dictionary entry.
/// </param>
/// <param name="ColumnValues">
/// Sparse column-index → new-value map. Columns absent from the map keep
/// their existing values.
/// </param>
public sealed record RowUpdate(
    int RowInPage,
    IReadOnlyDictionary<int, DataValue> ColumnValues);
