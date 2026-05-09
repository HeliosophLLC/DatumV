using System.Diagnostics.CodeAnalysis;
using DatumIngest.Serialization.MediaBag;

namespace DatumIngest.Serialization.Tar;

/// <summary>
/// Format handler for TAR archive files, including the common <c>.tar.gz</c>
/// and <c>.tar.bz2</c> compressed variants. Compression wrappers are unwrapped
/// by <see cref="FileFormatDescriptor"/> before this handler sees the file
/// (<c>foo.tar.gz</c> / <c>foo.tar.bz2</c>'s
/// <see cref="FileFormatDescriptor.LogicalExtension"/> is <c>.tar</c>), so a
/// single extension check covers all three shapes. Per-entry deserialization
/// is delegated to <see cref="MediaBagDeserializer"/>; like ZIP, TAR archives
/// are treated as homogeneous media bags (one media kind per archive) with the
/// schema decided by the first-entry probe.
/// </summary>
public sealed class TarFileFormat : IFileFormat
{
    /// <inheritdoc />
    public string Name => "tar";

    /// <inheritdoc />
    public bool CanHandle(
        FileFormatDescriptor descriptor,
        [NotNullWhen(true)] out IFormatDeserializer? deserializer)
    {
        string ext = descriptor.LogicalExtension;
        if (ext.Equals(".tar", StringComparison.OrdinalIgnoreCase))
        {
            deserializer = new MediaBagDeserializer(new TarBagReader(descriptor));
            return true;
        }

        deserializer = null;
        return false;
    }
}
