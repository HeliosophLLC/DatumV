namespace DatumIngest.DatumFile.V2;

/// <summary>
/// On-disk layout of the <c>.datum-pack</c> sidecar — a stripped-down
/// container for column pages that have been moved out of a primary
/// <c>.datum</c> file by compaction. Pack files are referenced from
/// the primary's footer prologue file table; their internal layout is
/// simply a 32-byte header followed by concatenated raw page bytes
/// addressed by absolute offset.
/// </summary>
/// <remarks>
/// <para>
/// Pack files have no schema, no footer, and no tail. They are opaque
/// to anyone not holding the primary <c>.datum</c> — the primary's
/// page directory is the index. This intentional minimalism keeps
/// compaction simple: dump pages to a pack, record offsets, point the
/// primary's <see cref="PageDescriptorV2.FileId"/> at the pack's id in
/// the file table.
/// </para>
/// <para>
/// The fingerprint embedded in the header is the same value recorded
/// in the primary's file-table entry; readers verify the match on
/// open and refuse mismatched packs (path collision / stale manifest
/// detection).
/// </para>
/// </remarks>
public static class DatumPackConstants
{
    /// <summary>Magic bytes identifying a .datum-pack file: ASCII "DTMPACKB".</summary>
    public static ReadOnlySpan<byte> Magic => "DTMPACKB"u8;

    /// <summary>Pack-file format version. <c>1</c> in v4 PR7.</summary>
    public const uint Version = 1;

    /// <summary>
    /// Fixed-size pack file header in bytes:
    /// magic(8) + version(4) + reserved(4) + fingerprint(16) = 32.
    /// </summary>
    public const int HeaderSize = 32;

    /// <summary>Byte offset of the version field within the header.</summary>
    public const int VersionOffset = 8;

    /// <summary>Byte offset of the fingerprint field within the header.</summary>
    public const int FingerprintOffset = 16;

    /// <summary>Length of the fingerprint in bytes.</summary>
    public const int FingerprintBytes = FileTableEntryV4.FingerprintBytes;

    /// <summary>Conventional file extension for pack files.</summary>
    public const string FileExtension = ".datum-pack";
}
