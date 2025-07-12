namespace DatumIngest.Indexing;

/// <summary>
/// A single entry from a cached ZIP central directory, describing one file
/// within a ZIP archive.
/// </summary>
/// <param name="FileName">Relative path of the entry within the archive.</param>
/// <param name="CompressedSize">Compressed size in bytes.</param>
/// <param name="UncompressedSize">Uncompressed size in bytes.</param>
/// <param name="LocalHeaderOffset">Byte offset of the local file header in the archive.</param>
/// <param name="Crc32">CRC-32 checksum of the uncompressed data.</param>
public readonly record struct ZipDirectoryEntry(
    string FileName,
    long CompressedSize,
    long UncompressedSize,
    long LocalHeaderOffset,
    uint Crc32);

/// <summary>
/// Cached snapshot of a ZIP archive's central directory. Avoids re-parsing
/// the central directory on every query by storing entries in the index file.
/// </summary>
public sealed class ZipDirectoryCache
{
    private readonly ZipDirectoryEntry[] _entries;

    /// <summary>Number of entries in the cached directory.</summary>
    public int Count => _entries.Length;

    /// <summary>The cached directory entries in their original order.</summary>
    public ReadOnlySpan<ZipDirectoryEntry> Entries => _entries;

    /// <summary>
    /// Creates a new ZIP directory cache from the given entries.
    /// </summary>
    /// <param name="entries">The directory entries to cache.</param>
    public ZipDirectoryCache(ZipDirectoryEntry[] entries)
    {
        _entries = entries;
    }

    /// <summary>
    /// Builds a cache from the entries of a <see cref="System.IO.Compression.ZipArchive"/>.
    /// </summary>
    /// <param name="archive">An open ZIP archive to read the central directory from.</param>
    /// <returns>A new cache containing all entries from the archive.</returns>
    public static ZipDirectoryCache BuildFromArchive(System.IO.Compression.ZipArchive archive)
    {
        ZipDirectoryEntry[] entries = new ZipDirectoryEntry[archive.Entries.Count];

        for (int i = 0; i < archive.Entries.Count; i++)
        {
            System.IO.Compression.ZipArchiveEntry entry = archive.Entries[i];
            entries[i] = new ZipDirectoryEntry(
                FileName: entry.FullName,
                CompressedSize: entry.CompressedLength,
                UncompressedSize: entry.Length,
                LocalHeaderOffset: -1, // ZIP API does not expose local header offset.
                Crc32: entry.Crc32);
        }

        return new ZipDirectoryCache(entries);
    }
}
