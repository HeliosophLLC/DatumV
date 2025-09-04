using System.IO.MemoryMappedFiles;
using System.Text;
using DatumIngest.Model;

namespace DatumIngest.Indexing;

/// <summary>
/// Reads a v4 mapped sorted index file, opening column data as memory-mapped
/// <see cref="MappedSortedIndex"/> instances. The file remains mapped for the
/// lifetime of the returned <see cref="MappedSortedIndexSet"/>.
/// </summary>
internal static class MappedSortedIndexReader
{
    /// <summary>
    /// Opens a v4 mapped sorted index file and returns the column directory and
    /// per-column <see cref="MappedSortedIndex"/> instances.
    /// </summary>
    /// <param name="filePath">Path to the <c>.datum-index-v4</c> file.</param>
    /// <returns>
    /// A <see cref="MappedSortedIndexSet"/> that owns the memory-mapped file and
    /// exposes per-column <see cref="IColumnIndex"/> instances.
    /// </returns>
    /// <exception cref="InvalidDataException">The file has bad magic bytes or unsupported version.</exception>
    public static MappedSortedIndexSet Open(string filePath)
    {
        MemoryMappedFile memoryMappedFile = MemoryMappedFile.CreateFromFile(
            filePath, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);

        try
        {
            MemoryMappedViewAccessor headerAccessor = memoryMappedFile.CreateViewAccessor(
                0, 0, MemoryMappedFileAccess.Read);

            // Read and validate header.
            Span<byte> magic = stackalloc byte[4];
            headerAccessor.ReadArray(0, magic);

            if (!magic.SequenceEqual(MappedSortedIndexWriter.MagicBytes))
            {
                headerAccessor.Dispose();
                throw new InvalidDataException("Not a v4 mapped sorted index file: bad magic bytes.");
            }

            int version = headerAccessor.ReadInt32(4);

            if (version != MappedSortedIndexWriter.FormatVersion)
            {
                headerAccessor.Dispose();
                throw new InvalidDataException(
                    $"Unsupported mapped sorted index version {version} (expected {MappedSortedIndexWriter.FormatVersion}).");
            }

            int columnCount = headerAccessor.ReadInt32(8);

            // Parse column directory using a BinaryReader on a stream over the mapped view.
            // The directory starts at offset 12 and has variable-length column names.
            using MemoryMappedViewStream directoryStream = memoryMappedFile.CreateViewStream(
                0, 0, MemoryMappedFileAccess.Read);
            directoryStream.Position = 12; // Skip header.

            using BinaryReader reader = new(directoryStream, Encoding.UTF8, leaveOpen: true);

            ColumnDirectory[] columns = new ColumnDirectory[columnCount];

            for (int index = 0; index < columnCount; index++)
            {
                columns[index] = new ColumnDirectory
                {
                    ColumnName = reader.ReadString(),
                    Kind = (DataKind)reader.ReadByte(),
                    EntryCount = reader.ReadInt64(),
                    KeysOffset = reader.ReadInt64(),
                    LocatorsOffset = reader.ReadInt64(),
                    StringTableOffset = reader.ReadInt64(),
                    StringTableLength = reader.ReadInt64(),
                };
            }

            // Create per-column MappedSortedIndex instances sharing the same accessor.
            Dictionary<string, MappedSortedIndex> indexes = new(StringComparer.OrdinalIgnoreCase);

            for (int index = 0; index < columnCount; index++)
            {
                ColumnDirectory column = columns[index];
                indexes[column.ColumnName] = new MappedSortedIndex(
                    headerAccessor,
                    column.Kind,
                    column.EntryCount,
                    column.KeysOffset,
                    column.LocatorsOffset,
                    column.StringTableOffset,
                    column.StringTableLength);
            }

            return new MappedSortedIndexSet(memoryMappedFile, headerAccessor, indexes);
        }
        catch
        {
            memoryMappedFile.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Column directory entry read from the file header.
    /// </summary>
    private struct ColumnDirectory
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
