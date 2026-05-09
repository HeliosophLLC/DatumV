namespace DatumIngest.Serialization.MediaBag;

/// <summary>
/// Container-agnostic reader over an archive-style media bag (ZIP, TAR, TAR.GZ,
/// …). Yields one <see cref="MediaBagEntry"/> per data entry in container order;
/// implementations are responsible for filtering out directory entries and the
/// usual OS/editor metadata pollution (<c>__MACOSX/</c>, leading-dot files,
/// <c>thumbs.db</c>, <c>desktop.ini</c>) before the entry is surfaced.
/// </summary>
/// <remarks>
/// The contract is forward-only: each entry's <see cref="MediaBagEntry.Body"/>
/// stream is valid only until the next move. Sequential containers (TAR over a
/// gzip stream) cannot rewind; random-access containers (ZIP) conform by opening
/// a fresh decompression stream per entry. Consumers must consume the body
/// before pulling the next entry.
/// </remarks>
public interface IMediaBagReader
{
    /// <summary>
    /// Source path of the archive — surfaced in validation error messages so the
    /// bad archive is identifiable without re-plumbing the descriptor through
    /// the deserializer's error paths.
    /// </summary>
    string Source { get; }

    /// <summary>
    /// Enumerates the bag's data entries. The returned stream lifetime is scoped
    /// to a single iteration step; see remarks on <see cref="IMediaBagReader"/>.
    /// </summary>
    IAsyncEnumerable<MediaBagEntry> EnumerateAsync(CancellationToken cancellationToken);
}
