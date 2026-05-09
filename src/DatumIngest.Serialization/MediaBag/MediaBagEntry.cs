namespace DatumIngest.Serialization.MediaBag;

/// <summary>
/// A single data entry yielded by an <see cref="IMediaBagReader"/>: the
/// archive-relative path, the uncompressed payload length declared by the
/// container header, and a forward-only stream over the payload bytes.
/// </summary>
public sealed class MediaBagEntry
{
    /// <summary>Creates an entry handle. Ownership of <paramref name="body"/> stays with the reader.</summary>
    public MediaBagEntry(string fullName, long length, Stream body)
    {
        FullName = fullName;
        Length = length;
        Body = body;
    }

    /// <summary>Archive-relative path of the entry (e.g. <c>train/0001.jpg</c>).</summary>
    public string FullName { get; }

    /// <summary>Uncompressed length in bytes, as declared by the container header.</summary>
    public long Length { get; }

    /// <summary>
    /// Forward-only stream over the entry's payload bytes. Must be fully consumed
    /// before the enumerator moves to the next entry — sequential containers
    /// (TAR) cannot rewind, and the reader will dispose the stream on the next
    /// step regardless.
    /// </summary>
    public Stream Body { get; }
}
