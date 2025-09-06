using System.Buffers.Binary;
using DatumIngest.DatumFile.Encoding;

namespace DatumIngest.DatumFile;

/// <summary>
/// Pre-encoded column pages for a single new row group to be appended
/// via <see cref="DatumFileEditor.AppendRowGroups"/>.
/// </summary>
/// <param name="RowCount">Number of rows in this row group.</param>
/// <param name="ColumnPages">
/// Encoded column pages in schema column order.
/// Length must equal the file schema column count.
/// </param>
public sealed record RowGroupPayload(uint RowCount, IReadOnlyList<DatumEncodedPage> ColumnPages);

/// <summary>
/// A replacement column page targeting a specific column in a specific row group,
/// used by <see cref="DatumFileEditor.ReplaceColumns"/>.
/// </summary>
/// <param name="ColumnIndex">Zero-based schema column index of the column to replace.</param>
/// <param name="RowGroupIndex">Zero-based index of the row group whose column page is replaced.</param>
/// <param name="Page">The new encoded page that replaces the existing one.</param>
public sealed record ColumnPageReplacement(int ColumnIndex, int RowGroupIndex, DatumEncodedPage Page);

/// <summary>
/// Performs in-place mutations on existing <c>.datum</c> files by appending new column
/// pages and rewriting the footer. Used by DDL/DML operations (INSERT, UPDATE, ALTER ADD)
/// on temporary tables.
/// </summary>
/// <remarks>
/// All edit operations follow the same pattern: read the existing footer via the tail sentinel,
/// seek to the footer offset (overwriting the old footer and tail), append new page data, write
/// a new footer encompassing both old and new row groups, and patch the file header. Old pages
/// referenced by replaced columns become dead space — acceptable for short-lived temp tables.
/// The stream must be readable, writable, and seekable.
/// </remarks>
public static class DatumFileEditor
{
    /// <summary>
    /// Appends one or more row groups to an existing <c>.datum</c> file. The caller provides
    /// pre-encoded column pages for each new row group. Pages are written sequentially after
    /// the existing data, and the footer is rewritten with the combined row group directory.
    /// </summary>
    /// <param name="stream">A readable, writable, seekable stream positioned at any offset.</param>
    /// <param name="newRowGroups">
    /// One or more row group payloads. Each must contain exactly as many column pages as
    /// the file schema has columns.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when any row group payload has a column page count that does not match the schema.
    /// </exception>
    public static void AppendRowGroups(
        Stream stream,
        IReadOnlyList<RowGroupPayload> newRowGroups)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(newRowGroups);
        if (newRowGroups.Count == 0) return;

        (DatumFileSchema schema, DatumRowGroupDescriptor[] existingRowGroups, long totalRowCount, DatumFileFlags flags) =
            DatumFileReader.ReadFooterAndHeader(stream);

        long footerOffset = ReadFooterOffset(stream);
        stream.Seek(footerOffset, SeekOrigin.Begin);

        List<DatumRowGroupDescriptor> allRowGroups = new(existingRowGroups.Length + newRowGroups.Count);
        allRowGroups.AddRange(existingRowGroups);
        long additionalRows = 0;

        foreach (RowGroupPayload payload in newRowGroups)
        {
            DatumColumnChunkDescriptor[] chunks = WriteColumnPages(stream, payload.ColumnPages, schema.ColumnCount);
            allRowGroups.Add(new DatumRowGroupDescriptor(payload.RowCount, chunks));
            additionalRows += payload.RowCount;
        }

        long newFooterOffset = stream.Position;
        WriteFooterAndTail(stream, schema, allRowGroups, flags);
        long endPosition = stream.Position;

        PatchHeader(stream, allRowGroups.Count, totalRowCount + additionalRows, newFooterOffset, flags);
        stream.SetLength(endPosition);
    }

    /// <summary>
    /// Replaces one or more column pages in specified row groups. New pages are appended to
    /// the end of the data area; old pages become dead space. The footer is rewritten with
    /// updated chunk descriptors pointing to the new page offsets.
    /// </summary>
    /// <param name="stream">A readable, writable, seekable stream positioned at any offset.</param>
    /// <param name="replacements">
    /// Column page replacements. Each identifies a column index, row group index, and the new
    /// encoded page. Multiple columns and row groups may be replaced in a single call.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when any replacement references a column or row group index outside the valid range.
    /// </exception>
    public static void ReplaceColumns(
        Stream stream,
        IReadOnlyList<ColumnPageReplacement> replacements)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(replacements);
        if (replacements.Count == 0) return;

        (DatumFileSchema schema, DatumRowGroupDescriptor[] existingRowGroups, long totalRowCount, DatumFileFlags flags) =
            DatumFileReader.ReadFooterAndHeader(stream);

        // Deep-copy row group descriptors so chunk arrays can be mutated.
        List<DatumRowGroupDescriptor> allRowGroups = new(existingRowGroups.Length);
        foreach (DatumRowGroupDescriptor rowGroup in existingRowGroups)
        {
            DatumColumnChunkDescriptor[] clonedChunks = (DatumColumnChunkDescriptor[])rowGroup.ColumnChunks.Clone();
            allRowGroups.Add(new DatumRowGroupDescriptor(rowGroup.RowCount, clonedChunks));
        }

        long footerOffset = ReadFooterOffset(stream);
        stream.Seek(footerOffset, SeekOrigin.Begin);

        foreach (ColumnPageReplacement replacement in replacements)
        {
            if (replacement.ColumnIndex < 0 || replacement.ColumnIndex >= schema.ColumnCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(replacements),
                    $"Column index {replacement.ColumnIndex} is outside the range [0, {schema.ColumnCount}).");
            }

            if (replacement.RowGroupIndex < 0 || replacement.RowGroupIndex >= allRowGroups.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(replacements),
                    $"Row group index {replacement.RowGroupIndex} is outside the range [0, {allRowGroups.Count}).");
            }

            DatumEncodedPage page = replacement.Page;
            long pageOffset = stream.Position;
            stream.Write(page.Payload);

            allRowGroups[replacement.RowGroupIndex].ColumnChunks[replacement.ColumnIndex] =
                new DatumColumnChunkDescriptor(
                    pageOffset,
                    (uint)page.Payload.Length,
                    (uint)page.UncompressedByteLength,
                    page.Encoding,
                    page.Compression,
                    page.ZoneMap);
        }

        long newFooterOffset = stream.Position;
        WriteFooterAndTail(stream, schema, allRowGroups, flags);
        long endPosition = stream.Position;

        PatchHeader(stream, allRowGroups.Count, totalRowCount, newFooterOffset, flags);
        stream.SetLength(endPosition);
    }

    /// <summary>
    /// Adds a new column to an existing <c>.datum</c> file. One encoded page per existing
    /// row group is appended to the data area, and the footer is rewritten with the wider schema.
    /// </summary>
    /// <param name="stream">A readable, writable, seekable stream positioned at any offset.</param>
    /// <param name="newColumn">Descriptor for the column to add.</param>
    /// <param name="pagesPerRowGroup">
    /// Encoded pages for the new column, one per existing row group. Each page should contain
    /// values (null or default) matching that row group's row count.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when the number of pages does not match the existing row group count.
    /// </exception>
    public static void AddColumn(
        Stream stream,
        DatumColumnDescriptor newColumn,
        IReadOnlyList<DatumEncodedPage> pagesPerRowGroup)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(newColumn);
        ArgumentNullException.ThrowIfNull(pagesPerRowGroup);

        (DatumFileSchema schema, DatumRowGroupDescriptor[] existingRowGroups, long totalRowCount, DatumFileFlags flags) =
            DatumFileReader.ReadFooterAndHeader(stream);

        if (pagesPerRowGroup.Count != existingRowGroups.Length)
        {
            throw new ArgumentException(
                $"Expected {existingRowGroups.Length} pages (one per existing row group) but received {pagesPerRowGroup.Count}.",
                nameof(pagesPerRowGroup));
        }

        List<DatumColumnDescriptor> widerColumns = new(schema.Columns);
        widerColumns.Add(newColumn);
        DatumFileSchema widerSchema = new(widerColumns);

        long footerOffset = ReadFooterOffset(stream);
        stream.Seek(footerOffset, SeekOrigin.Begin);

        List<DatumRowGroupDescriptor> allRowGroups = new(existingRowGroups.Length);
        for (int rowGroupIndex = 0; rowGroupIndex < existingRowGroups.Length; rowGroupIndex++)
        {
            DatumRowGroupDescriptor oldRowGroup = existingRowGroups[rowGroupIndex];
            DatumEncodedPage page = pagesPerRowGroup[rowGroupIndex];

            long pageOffset = stream.Position;
            stream.Write(page.Payload);

            DatumColumnChunkDescriptor[] widerChunks = new DatumColumnChunkDescriptor[schema.ColumnCount + 1];
            Array.Copy(oldRowGroup.ColumnChunks, widerChunks, schema.ColumnCount);
            widerChunks[schema.ColumnCount] = new DatumColumnChunkDescriptor(
                pageOffset,
                (uint)page.Payload.Length,
                (uint)page.UncompressedByteLength,
                page.Encoding,
                page.Compression,
                page.ZoneMap);

            allRowGroups.Add(new DatumRowGroupDescriptor(oldRowGroup.RowCount, widerChunks));
        }

        long newFooterOffset = stream.Position;
        WriteFooterAndTail(stream, widerSchema, allRowGroups, flags);
        long endPosition = stream.Position;

        PatchHeader(stream, allRowGroups.Count, totalRowCount, newFooterOffset, flags);
        stream.SetLength(endPosition);
    }

    /// <summary>
    /// Marks rows as deleted in one or more row groups by writing tombstone bitmaps
    /// into the footer. No column page I/O is performed — only the footer and header
    /// are rewritten. The file's <see cref="DatumFileFlags.HasTombstones"/> flag is
    /// set automatically if not already present.
    /// </summary>
    /// <param name="stream">A readable, writable, seekable stream positioned at any offset.</param>
    /// <param name="tombstoneUpdates">
    /// Per row group: the zero-based row group index and the new tombstone bitmap.
    /// Each bitmap must be <c>ceil(rowCount / 8)</c> bytes with set bits for deleted rows.
    /// When merging with existing tombstones, the caller is responsible for OR-ing the
    /// old and new bitmaps before passing them.
    /// </param>
    /// <returns>The total number of rows newly marked as deleted across all affected row groups.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when a row group index is outside the valid range.
    /// </exception>
    public static long MarkDeleted(
        Stream stream,
        IReadOnlyList<(int RowGroupIndex, byte[] TombstoneBitmap)> tombstoneUpdates)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(tombstoneUpdates);
        if (tombstoneUpdates.Count == 0) return 0;

        (DatumFileSchema schema, DatumRowGroupDescriptor[] existingRowGroups, long totalRowCount, DatumFileFlags flags) =
            DatumFileReader.ReadFooterAndHeader(stream);

        // Enable the tombstone flag if this is the first deletion.
        flags |= DatumFileFlags.HasTombstones;

        List<DatumRowGroupDescriptor> allRowGroups = new(existingRowGroups.Length);
        allRowGroups.AddRange(existingRowGroups);

        long newlyDeleted = 0;

        foreach ((int rowGroupIndex, byte[] bitmap) in tombstoneUpdates)
        {
            if (rowGroupIndex < 0 || rowGroupIndex >= allRowGroups.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(tombstoneUpdates),
                    $"Row group index {rowGroupIndex} is outside the range [0, {allRowGroups.Count}).");
            }

            DatumRowGroupDescriptor existing = allRowGroups[rowGroupIndex];
            uint activeCount = CountActiveBits(existing.RowCount, bitmap);
            long previousActive = existing.ActiveRowCount;
            newlyDeleted += previousActive - activeCount;

            allRowGroups[rowGroupIndex] = new DatumRowGroupDescriptor(
                existing.RowCount,
                activeCount,
                bitmap,
                existing.ColumnChunks);
        }

        // Footer-only rewrite: seek to the old footer position, write the new footer there.
        long footerOffset = ReadFooterOffset(stream);
        stream.Seek(footerOffset, SeekOrigin.Begin);

        long newFooterOffset = stream.Position;
        WriteFooterAndTail(stream, schema, allRowGroups, flags);
        long endPosition = stream.Position;

        PatchHeader(stream, allRowGroups.Count, totalRowCount, newFooterOffset, flags);
        stream.SetLength(endPosition);

        return newlyDeleted;
    }

    /// <summary>
    /// Counts the number of non-deleted (active) rows by counting clear bits in the bitmap.
    /// </summary>
    private static uint CountActiveBits(uint totalRows, byte[] bitmap)
    {
        uint deletedCount = 0;
        for (int byteIndex = 0; byteIndex < bitmap.Length; byteIndex++)
        {
            deletedCount += (uint)BitCount(bitmap[byteIndex]);
        }

        // The last byte may have padding bits beyond totalRows; subtract them.
        int paddingBits = bitmap.Length * 8 - (int)totalRows;
        if (paddingBits > 0)
        {
            byte lastByte = bitmap[^1];
            // Count only the padding bits that are set.
            for (int bit = (int)totalRows & 7; bit < 8; bit++)
            {
                if ((lastByte & (1 << bit)) != 0) deletedCount--;
            }
        }

        return totalRows - deletedCount;
    }

    /// <summary>Returns the number of set bits (popcount) in a single byte.</summary>
    private static int BitCount(byte value)
    {
        // Kernighan's bit counting.
        int count = 0;
        while (value != 0)
        {
            value &= (byte)(value - 1);
            count++;
        }

        return count;
    }

    // ──────────────────── Private helpers ────────────────────

    /// <summary>Reads the footerOffset field from the 28-byte file header.</summary>
    private static long ReadFooterOffset(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[8];
        stream.Seek(DatumFileConstants.FooterOffsetPosition, SeekOrigin.Begin);
        stream.ReadExactly(buffer);
        return BinaryPrimitives.ReadInt64LittleEndian(buffer);
    }

    /// <summary>
    /// Writes encoded column pages sequentially and returns the chunk descriptors
    /// capturing the page offsets and metadata.
    /// </summary>
    private static DatumColumnChunkDescriptor[] WriteColumnPages(
        Stream stream,
        IReadOnlyList<DatumEncodedPage> pages,
        int expectedColumnCount)
    {
        if (pages.Count != expectedColumnCount)
        {
            throw new ArgumentException(
                $"Row group has {pages.Count} column pages but schema requires {expectedColumnCount}.");
        }

        DatumColumnChunkDescriptor[] chunks = new DatumColumnChunkDescriptor[expectedColumnCount];

        for (int columnIndex = 0; columnIndex < expectedColumnCount; columnIndex++)
        {
            DatumEncodedPage page = pages[columnIndex];
            long pageOffset = stream.Position;
            stream.Write(page.Payload);

            chunks[columnIndex] = new DatumColumnChunkDescriptor(
                pageOffset,
                (uint)page.Payload.Length,
                (uint)page.UncompressedByteLength,
                page.Encoding,
                page.Compression,
                page.ZoneMap);
        }

        return chunks;
    }

    /// <summary>
    /// Writes the schema block, row group directory, footer byte length, and tail magic.
    /// </summary>
    private static void WriteFooterAndTail(
        Stream stream,
        DatumFileSchema schema,
        IReadOnlyList<DatumRowGroupDescriptor> rowGroups,
        DatumFileFlags flags = DatumFileFlags.None)
    {
        long footerStart = stream.Position;
        bool hasTombstones = flags.HasFlag(DatumFileFlags.HasTombstones);

        using BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        schema.Serialize(writer);
        writer.Write((uint)rowGroups.Count);

        foreach (DatumRowGroupDescriptor rowGroup in rowGroups)
        {
            rowGroup.Serialize(writer, hasTombstones);
        }

        writer.Flush();
        uint footerByteLength = (uint)(stream.Position - footerStart);

        writer.Write(footerByteLength);
        writer.Write(DatumFileConstants.TailMagic.ToArray());
        writer.Flush();
    }

    /// <summary>
    /// Overwrites the mutable header fields: flags at byte offset 6, then
    /// rowGroupCount, totalRowCount, and footerOffset at byte offset 8.
    /// </summary>
    private static void PatchHeader(
        Stream stream,
        int rowGroupCount,
        long totalRowCount,
        long footerOffset,
        DatumFileFlags flags = DatumFileFlags.None)
    {
        stream.Seek(6, SeekOrigin.Begin);
        Span<byte> patch = stackalloc byte[22];
        BinaryPrimitives.WriteUInt16LittleEndian(patch, (ushort)flags);
        BinaryPrimitives.WriteUInt32LittleEndian(patch[2..], (uint)rowGroupCount);
        BinaryPrimitives.WriteInt64LittleEndian(patch[6..], totalRowCount);
        BinaryPrimitives.WriteInt64LittleEndian(patch[14..], footerOffset);
        stream.Write(patch);
    }
}
