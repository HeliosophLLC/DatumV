namespace DatumIngest.Serialization.MediaBag;

/// <summary>
/// A single data entry yielded by an <see cref="IMediaBagReader"/>: the
/// archive-relative path, the uncompressed payload length declared by the
/// container header, the modification time when the container records it,
/// and a forward-only stream over the payload bytes.
/// </summary>
public sealed class MediaBagEntry
{
    /// <summary>Creates an entry handle. Ownership of <paramref name="body"/> stays with the reader.</summary>
    public MediaBagEntry(string fullName, long length, DateTimeOffset? modified, Stream body)
    {
        FullName = fullName;
        Length = length;
        Modified = modified;
        Body = body;
    }

    /// <summary>Archive-relative path of the entry (e.g. <c>train/0001.jpg</c>).</summary>
    public string FullName { get; }

    /// <summary>Uncompressed length in bytes, as declared by the container header.</summary>
    public long Length { get; }

    /// <summary>
    /// Modification timestamp from the container header, or <see langword="null"/>
    /// when the container omits it. ZIP entries report this as
    /// <see cref="System.IO.Compression.ZipArchiveEntry.LastWriteTime"/>; TAR entries
    /// report it as <see cref="System.Formats.Tar.TarEntry.ModificationTime"/>. Both
    /// are <see cref="DateTimeOffset"/>, so timezone information is preserved when
    /// available.
    /// </summary>
    public DateTimeOffset? Modified { get; }

    /// <summary>
    /// Forward-only stream over the entry's payload bytes. Must be fully consumed
    /// before the enumerator moves to the next entry — sequential containers
    /// (TAR) cannot rewind, and the reader will dispose the stream on the next
    /// step regardless.
    /// </summary>
    public Stream Body { get; }
}
