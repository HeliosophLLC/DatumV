using System.IO.Compression;
using System.Runtime.CompilerServices;
using DatumIngest.Serialization.MediaBag;

namespace DatumIngest.Serialization.Zip;

/// <summary>
/// <see cref="IMediaBagReader"/> implementation over <see cref="ZipArchive"/>.
/// Yields one entry per non-directory, non-metadata file in central-directory
/// order; each entry's body is a fresh decompression stream owned by the
/// iterator (auto-disposed when the consumer moves to the next entry).
/// </summary>
public sealed class ZipBagReader : IMediaBagReader
{
    private readonly FileFormatDescriptor _descriptor;

    /// <summary>Wraps a descriptor; the underlying file is opened on enumeration.</summary>
    public ZipBagReader(FileFormatDescriptor descriptor)
    {
        _descriptor = descriptor;
    }

    /// <inheritdoc/>
    public string Source => _descriptor.FilePath;

    /// <inheritdoc/>
    public async IAsyncEnumerable<MediaBagEntry> EnumerateAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using Stream stream = await _descriptor.OpenAsync(cancellationToken).ConfigureAwait(false);
        using ZipArchive archive = new(stream, ZipArchiveMode.Read);

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Skip directory entries.
            if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/')) continue;
            if (MediaBagFilter.IsIgnorableMetadata(entry.FullName)) continue;

            using Stream body = entry.Open();
            yield return new MediaBagEntry(entry.FullName, entry.Length, body);
        }
    }
}
