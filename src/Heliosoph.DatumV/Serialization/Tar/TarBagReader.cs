using System.Formats.Tar;
using System.Runtime.CompilerServices;
using Heliosoph.DatumV.Serialization.MediaBag;

namespace Heliosoph.DatumV.Serialization.Tar;

/// <summary>
/// <see cref="IMediaBagReader"/> implementation over <see cref="TarReader"/>.
/// Yields one entry per regular file in tar order; gzip / bzip2 wrappers (when
/// the source is <c>.tar.gz</c> or <c>.tar.bz2</c>) are handled transparently by
/// <see cref="FileFormatDescriptor.OpenAsync"/>, which materializes a
/// decompressed temp file once and reuses it for subsequent opens.
/// </summary>
/// <remarks>
/// <para>
/// Forward-only. Each <see cref="TarEntry.DataStream"/> is valid only until the
/// next <see cref="TarReader.GetNextEntryAsync(bool, CancellationToken)"/>, so
/// consumers must fully consume the body before pulling the next entry — which
/// <see cref="MediaBag.MediaBagDeserializer"/> does by streaming into the arena
/// or pooled buffer before yielding the row.
/// </para>
/// <para>
/// Symbolic-link, hard-link, and directory entries are skipped because their
/// payloads aren't byte streams. OS/editor metadata files (<c>__MACOSX/</c>,
/// <c>.DS_Store</c>, …) are NOT filtered here — consumers apply
/// <see cref="MediaBagFilter"/> on top when they want media-bag semantics, and
/// raw consumers (<c>open_archive</c>) see every regular-file entry.
/// </para>
/// </remarks>
public sealed class TarBagReader : IMediaBagReader
{
    private readonly FileFormatDescriptor _descriptor;

    /// <summary>Wraps a descriptor; the underlying file is opened on enumeration.</summary>
    public TarBagReader(FileFormatDescriptor descriptor)
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
        await using TarReader reader = new(stream, leaveOpen: true);

        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false)
               is TarEntry entry)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                continue;
            if (entry.DataStream is null) continue;

            yield return new MediaBagEntry(entry.Name, entry.Length, entry.ModificationTime, entry.DataStream);
        }
    }
}
