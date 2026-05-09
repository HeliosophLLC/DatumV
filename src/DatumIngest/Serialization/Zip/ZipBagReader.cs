using System.IO.Compression;
using System.Runtime.CompilerServices;
using DatumIngest.Serialization.MediaBag;

namespace DatumIngest.Serialization.Zip;

/// <summary>
/// <see cref="IMediaBagReader"/> implementation over <see cref="ZipArchive"/>.
/// Yields one entry per non-directory file in central-directory order; each
/// entry's body is a fresh decompression stream owned by the iterator
/// (auto-disposed when the consumer moves to the next entry).
/// </summary>
/// <remarks>
/// Returns ALL non-directory entries — OS/editor metadata files
/// (<c>__MACOSX/</c>, <c>.DS_Store</c>, <c>thumbs.db</c>, …) are not filtered
/// at this layer. Consumers that treat archives as homogeneous media bags
/// (e.g. <see cref="MediaBag.MediaBagDeserializer"/>) apply the
/// <see cref="MediaBagFilter"/> on top; consumers that want a raw archive
/// scan (e.g. the <c>open_archive</c> TVF) see every entry and filter via
/// SQL.
/// </remarks>
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

            // Skip directory entries — they have no body and aren't meaningful as data rows.
            if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/')) continue;

            using Stream body = entry.Open();
            yield return new MediaBagEntry(entry.FullName, entry.Length, entry.LastWriteTime, body);
        }
    }
}
