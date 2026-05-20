using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Serialization.Tar;
using Heliosoph.DatumV.Serialization.Zip;

namespace Heliosoph.DatumV.Serialization.MediaBag;

/// <summary>
/// Static dispatch factory that opens an archive file as an
/// <see cref="IMediaBagReader"/>, picking the right concrete reader based on
/// the source's logical extension and (when unambiguous from extension)
/// magic-byte detection. Used by the <c>open_archive</c> TVF and any other
/// SQL-layer consumer that wants raw archive iteration without going through
/// the homogeneous-media <see cref="MediaBagDeserializer"/> pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Compression wrappers (<c>.gz</c>, <c>.bz2</c>) are stripped by
/// <see cref="FileFormatDescriptor.LogicalExtension"/>, so a <c>foo.tar.gz</c>
/// source surfaces as <c>.tar</c> here and goes through <see cref="TarBagReader"/>.
/// The underlying gzip / bz2 unwrap is owned by <see cref="FileFormatDescriptor.OpenAsync"/>,
/// which materialises a decompressed temp file the first time the descriptor is
/// opened and reuses it thereafter.
/// </para>
/// <para>
/// Extension dispatch is the primary path; magic-byte fallback only kicks in
/// for uncompressed sources whose extensions don't disambiguate (e.g. a
/// <c>.bin</c> file that happens to be a ZIP). For ambiguous extensions on
/// compressed sources we'd need to unwrap before sniffing, which defeats the
/// purpose — extensions are the contract on compressed inputs.
/// </para>
/// </remarks>
public static class OpenArchiveReader
{
    /// <summary>
    /// Opens <paramref name="source"/> as an archive and returns the matching
    /// <see cref="IMediaBagReader"/>. The returned reader owns the descriptor
    /// internally and disposes the underlying stream when enumeration completes.
    /// Throws <see cref="InvalidDataException"/> when the source isn't a
    /// recognised archive format.
    /// </summary>
    public static IMediaBagReader Open(string source)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);

        FileFormatDescriptor descriptor = new(source);
        string ext = descriptor.LogicalExtension;

        if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return new ZipBagReader(descriptor);
        }
        if (ext.Equals(".tar", StringComparison.OrdinalIgnoreCase))
        {
            return new TarBagReader(descriptor);
        }

        // Magic-byte fallback for uncompressed sources whose extension doesn't
        // disambiguate. Compressed sources (.gz / .bz2) would need an unwrap
        // pass to sniff, which is wasteful — for those the extension is the contract.
        if (descriptor.Compression == CompressionKind.None && File.Exists(descriptor.FilePath))
        {
            ArchiveKind sniffed = SniffArchiveKind(descriptor.FilePath);
            return sniffed switch
            {
                ArchiveKind.Zip => new ZipBagReader(descriptor),
                ArchiveKind.Tar => new TarBagReader(descriptor),
                _ => throw new InvalidDataException(BuildUnknownFormatError(source, ext)),
            };
        }

        throw new InvalidDataException(BuildUnknownFormatError(source, ext));
    }

    private enum ArchiveKind { None, Zip, Tar }

    private static ArchiveKind SniffArchiveKind(string filePath)
    {
        // ZIP local-file-header magic at byte 0; TAR's "ustar" signature at byte 257.
        // 264 bytes covers both with a single read.
        Span<byte> buffer = stackalloc byte[264];
        using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        int read = fs.Read(buffer);
        if (read >= 4 && buffer[0] == 'P' && buffer[1] == 'K' && buffer[2] == 0x03 && buffer[3] == 0x04)
            return ArchiveKind.Zip;
        if (read >= 263
            && buffer[257] == (byte)'u' && buffer[258] == (byte)'s' && buffer[259] == (byte)'t'
            && buffer[260] == (byte)'a' && buffer[261] == (byte)'r')
            return ArchiveKind.Tar;
        return ArchiveKind.None;
    }

    private static string BuildUnknownFormatError(string source, string ext) =>
        $"open_archive() does not recognise '{source}' as a supported archive format. " +
        $"Logical extension '{ext}' is not one of .zip / .tar (covers .tar.gz and .tar.bz2), " +
        $"and magic-byte sniffing did not find a ZIP or TAR signature.";
}
