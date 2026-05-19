namespace Heliosoph.DatumV.DatumFile.Sidecar;

/// <summary>
/// On-disk constants for the <c>.datum-blob</c> sidecar format. The sidecar is a
/// flat append-only blob store referenced by absolute offsets recorded in the
/// companion <c>.datum</c> file's column pages.
/// </summary>
/// <remarks>
/// <para>
/// Layout:
/// </para>
/// <code>
/// [magic       : 8 bytes  "DATUMBLB" little-endian]
/// [version     : 4 bytes  uint32 little-endian]
/// [reserved1   : 4 bytes  (zero in v1)]
/// [fingerprint : 8 bytes  uint64 little-endian — must match the .datum footer's reference]
/// [payloadHash : 8 bytes  xxHash3-64 of the payload region [HeaderSize..EOF), little-endian]
/// [blob bytes  : append-only payload region]
/// </code>
/// <para>
/// The header is exactly <see cref="HeaderSize"/> = 32 bytes. Blob offsets stored in
/// <see cref="Model.DataValue"/>s are absolute file positions — i.e. they include the
/// <see cref="HeaderSize"/> bytes of header, so a reader just slices the mmap view at
/// the offset directly.
/// </para>
/// <para>
/// The <c>fingerprint</c> is a random 64-bit value generated when the sidecar is first
/// materialised. The companion <c>.datum</c> file's footer carries the same value;
/// <see cref="SidecarReadStore"/> refuses to open a sidecar whose fingerprint doesn't
/// match the one the <c>.datum</c> file expects, catching swap / staleness scenarios.
/// </para>
/// <para>
/// The <c>payloadHash</c> is xxHash3-64 over <c>[HeaderSize..EOF)</c>, computed and
/// patched into the header by <see cref="SidecarWriteStore.Dispose"/> after the final
/// append. <see cref="SidecarReadStore"/> verifies it on open. A zero hash is treated
/// as "unhashed" (legacy file written before integrity hashing was added) and the check
/// is skipped — this preserves backwards compatibility with sidecars produced by
/// earlier writers.
/// </para>
/// </remarks>
public static class SidecarConstants
{
    /// <summary>
    /// Magic bytes identifying a <c>.datum-blob</c> file: ASCII "DATUMBLB" stored
    /// little-endian as a single <see cref="ulong"/>.
    /// </summary>
    public const ulong Magic = 0x424C424D55544144UL;

    /// <summary>Current sidecar format version. Bumped on incompatible layout changes.</summary>
    public const uint Version = 1;

    /// <summary>Total bytes occupied by the file header before the first blob payload byte.</summary>
    public const int HeaderSize = 32;

    /// <summary>Byte offset within the header at which the payload xxHash3-64 lives.</summary>
    public const int PayloadHashOffset = 24;

    /// <summary>
    /// Conventional file extension for a sidecar associated with a <c>.datum</c> file.
    /// The two files live next to each other and are named identically apart from the
    /// extension (e.g. <c>foo.datum</c> + <c>foo.datum-blob</c>).
    /// </summary>
    public const string FileExtension = ".datum-blob";
}
