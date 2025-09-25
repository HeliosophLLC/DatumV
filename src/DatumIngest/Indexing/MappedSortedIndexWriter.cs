using System.Buffers.Binary;
using System.Text;
using DatumIngest.Model;

namespace DatumIngest.Indexing;

/// <summary>
/// Writes sorted index data in the v4 fixed-width memory-mappable format.
/// The layout for each column consists of three contiguous regions:
/// keys array, locators array, and (for string columns) a string table.
/// </summary>
/// <remarks>
/// <para>
/// The file-level layout is:
/// <list type="number">
///   <item>Magic bytes: <c>DXIX</c> (4 bytes)</item>
///   <item>Format version: <see cref="FormatVersion"/> (int32, 4 bytes)</item>
///   <item>Column count (int32, 4 bytes)</item>
///   <item>Column directory: per column (column name UTF-8 with length prefix, DataKind byte,
///         entry count int64, keys offset int64, locators offset int64,
///         string table offset int64, string table length int64)</item>
///   <item>Column data: keys array + locators array + string table per column, contiguous</item>
/// </list>
/// </para>
/// </remarks>
internal static class MappedSortedIndexWriter
{
    /// <summary>Magic bytes identifying a v4 mapped sorted index file.</summary>
    internal static ReadOnlySpan<byte> MagicBytes => "DXIX"u8;

    /// <summary>Format version for the v5 fixed-width layout.</summary>
    internal const int FormatVersion = 5;

    /// <summary>
    /// Writes the v4 mapped sorted index file for the specified columns.
    /// </summary>
    /// <param name="stream">The output stream. Must be seekable for backpatching the directory.</param>
    /// <param name="columns">Column names and their sorted index entries (must already be sorted).</param>
    public static void Write(Stream stream, IReadOnlyList<(string ColumnName, DataKind Kind, ReadOnlyMemory<ValueIndexEntry> Entries)> columns)
    {
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);

        // Header.
        writer.Write(MagicBytes);
        writer.Write(FormatVersion);
        writer.Write(columns.Count);

        // Reserve space for the column directory. Each directory entry has a variable-length
        // column name, so we write a placeholder directory first, then backpatch offsets after
        // writing column data.
        long directoryStart = stream.Position;
        ColumnDirectoryEntry[] directory = new ColumnDirectoryEntry[columns.Count];

        for (int columnIndex = 0; columnIndex < columns.Count; columnIndex++)
        {
            (string columnName, DataKind kind, ReadOnlyMemory<ValueIndexEntry> entries) = columns[columnIndex];
            directory[columnIndex] = new ColumnDirectoryEntry
            {
                ColumnName = columnName,
                Kind = kind,
                EntryCount = entries.Length,
            };

            // Write placeholder directory entry (will backpatch offsets).
            WriteDirectoryEntryPlaceholder(writer, columnName, kind, entries.Length);
        }

        // Write column data and record offsets.
        // Maximum key width is 16 (Uuid), so a fixed 16-byte buffer avoids stackalloc in a loop.
        Span<byte> keyBuffer = stackalloc byte[16];

        for (int columnIndex = 0; columnIndex < columns.Count; columnIndex++)
        {
            (string columnName, DataKind kind, ReadOnlyMemory<ValueIndexEntry> entries) = columns[columnIndex];
            int keyWidth = SortedIndexKeyEncoder.GetKeyWidth(kind);
            ReadOnlySpan<ValueIndexEntry> entrySpan = entries.Span;

            // Keys array.
            directory[columnIndex].KeysOffset = stream.Position;

            if (kind == DataKind.String)
            {
                WriteStringKeysAndTable(writer, stream, entrySpan, ref directory[columnIndex]);
            }
            else
            {
                Span<byte> keySlice = keyBuffer[..keyWidth];

                for (int entryIndex = 0; entryIndex < entrySpan.Length; entryIndex++)
                {
                    SortedIndexKeyEncoder.Encode(entrySpan[entryIndex].Key, keySlice);
                    writer.Write(keySlice);
                }

                directory[columnIndex].StringTableOffset = 0;
                directory[columnIndex].StringTableLength = 0;
            }

            // Locators array.
            directory[columnIndex].LocatorsOffset = stream.Position;

            for (int entryIndex = 0; entryIndex < entrySpan.Length; entryIndex++)
            {
                writer.Write(entrySpan[entryIndex].ChunkIndex);
                writer.Write(entrySpan[entryIndex].RowOffsetInChunk);
            }
        }

        // Backpatch directory.
        long endPosition = stream.Position;
        stream.Position = directoryStart;

        for (int columnIndex = 0; columnIndex < columns.Count; columnIndex++)
        {
            WriteDirectoryEntry(writer, directory[columnIndex]);
        }

        stream.Position = endPosition;
    }

    /// <summary>
    /// Writes a string column's keys array and string table. Keys are 8-byte references
    /// (offset + length) into the string table. The string table is written immediately
    /// after the keys array.
    /// </summary>
    private static void WriteStringKeysAndTable(
        BinaryWriter writer,
        Stream stream,
        ReadOnlySpan<ValueIndexEntry> entries,
        ref ColumnDirectoryEntry directory)
    {
        // First pass: build the string table content and record offsets.
        // We write entries in index order, deduplicating identical strings by offset.
        MemoryStream stringTableBuffer = new();
        Dictionary<string, (int Offset, int Length)> stringPositions = new(StringComparer.Ordinal);
        (int Offset, int Length)[] entryReferences = new (int, int)[entries.Length];

        for (int index = 0; index < entries.Length; index++)
        {
            string value = entries[index].Key.AsString();

            if (!stringPositions.TryGetValue(value, out (int Offset, int Length) existing))
            {
                int offset = (int)stringTableBuffer.Position;
                byte[] utf8 = Encoding.UTF8.GetBytes(value);
                stringTableBuffer.Write(utf8);
                existing = (offset, utf8.Length);
                stringPositions[value] = existing;
            }

            entryReferences[index] = existing;
        }

        // Write keys array (8 bytes per entry: offset + length).
        Span<byte> referenceBuffer = stackalloc byte[8];

        for (int index = 0; index < entries.Length; index++)
        {
            SortedIndexKeyEncoder.EncodeStringReference(
                entryReferences[index].Offset,
                entryReferences[index].Length,
                referenceBuffer);
            writer.Write(referenceBuffer);
        }

        // Write string table.
        directory.StringTableOffset = stream.Position;
        directory.StringTableLength = stringTableBuffer.Length;
        stringTableBuffer.Position = 0;
        stringTableBuffer.CopyTo(stream);
    }

    /// <summary>
    /// Writes a placeholder directory entry (offsets set to zero, will be backpatched).
    /// </summary>
    private static void WriteDirectoryEntryPlaceholder(
        BinaryWriter writer, string columnName, DataKind kind, long entryCount)
    {
        writer.Write(columnName);       // Length-prefixed UTF-8.
        writer.Write((byte)kind);
        writer.Write(entryCount);       // int64
        writer.Write(0L);              // keysOffset placeholder
        writer.Write(0L);              // locatorsOffset placeholder
        writer.Write(0L);              // stringTableOffset placeholder
        writer.Write(0L);              // stringTableLength placeholder
    }

    /// <summary>
    /// Writes a directory entry with the final offsets.
    /// </summary>
    private static void WriteDirectoryEntry(BinaryWriter writer, ColumnDirectoryEntry entry)
    {
        writer.Write(entry.ColumnName);
        writer.Write((byte)entry.Kind);
        writer.Write(entry.EntryCount);
        writer.Write(entry.KeysOffset);
        writer.Write(entry.LocatorsOffset);
        writer.Write(entry.StringTableOffset);
        writer.Write(entry.StringTableLength);
    }

    /// <summary>
    /// Mutable directory entry used during writing to record byte offsets before backpatching.
    /// </summary>
    private struct ColumnDirectoryEntry
    {
        public string ColumnName;
        public DataKind Kind;
        public long EntryCount;
        public long KeysOffset;
        public long LocatorsOffset;
        public long StringTableOffset;
        public long StringTableLength;
    }
}
