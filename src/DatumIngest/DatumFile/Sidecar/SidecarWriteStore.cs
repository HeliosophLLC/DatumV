using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DatumIngest.DatumFile.Sidecar;

/// <summary>
/// Writes Large Binary Objects (images, byte arrays, future video, etc.) to a
/// <c>.datum-blob</c> sidecar file. Created lazily — the file does not exist on
/// disk until the first <see cref="Append"/> call. <c>.datum</c> files containing
/// no LBO data leave no orphan <c>.datum-blob</c> behind.
/// </summary>
/// <remarks>
/// <para>
/// Concurrent <see cref="Append"/> calls are serialised internally; callers can
/// hand the same instance to multiple producers and trust that returned
/// <c>(Offset, Length)</c> pairs are unique and non-overlapping.
/// </para>
/// <para>
/// On <see cref="Dispose"/> the file (if materialised) is flushed and closed. The
/// containing <c>DatumFileWriter</c> embeds <see cref="Fingerprint"/> in the
/// <c>.datum</c> footer only when <see cref="WasMaterialized"/> is true, so the
/// presence of the field in the footer is the canonical signal that a sidecar
/// must accompany the <c>.datum</c> file at read time.
/// </para>
/// </remarks>
public sealed class SidecarWriteStore : IBlobSink, IDisposable
{
    private readonly string _path;
    private readonly Lock _lock = new();
    private FileStream? _stream;
    private long _writeOffset;
    private bool _disposed;

    /// <summary>
    /// Creates a sidecar writer targeting <paramref name="path"/>. The file is not
    /// created until the first <see cref="Append"/> call; constructing the writer
    /// has no on-disk side effect.
    /// </summary>
    /// <param name="path">Full path to the sidecar file (typically the companion
    /// <c>.datum</c> path with extension swapped to <see cref="SidecarConstants.FileExtension"/>).</param>
    public SidecarWriteStore(string path)
    {
        _path = path;
        Fingerprint = GenerateFingerprint();
    }

    /// <summary>
    /// 64-bit random value generated at construction. The companion <c>.datum</c>
    /// file's footer must record this value; readers compare against the sidecar's
    /// header to detect stale or swapped files.
    /// </summary>
    public ulong Fingerprint { get; }

    /// <summary>
    /// True once <see cref="Append"/> has been called at least once and the sidecar
    /// file exists on disk. Writers that finalise without any LBO data will see
    /// <c>false</c> and skip both the file flush and the .datum footer's sidecar
    /// reference, keeping pure-tabular ingest free of sidecar artefacts.
    /// </summary>
    public bool WasMaterialized => _stream is not null;

    /// <inheritdoc />
    public (long Offset, long Length) Append(ReadOnlySpan<byte> bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            if (_stream is null)
            {
                _stream = new FileStream(
                    _path,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 1 << 20,
                    options: FileOptions.SequentialScan);
                WriteHeader(_stream, Fingerprint);
                _writeOffset = SidecarConstants.HeaderSize;
            }

            long offset = _writeOffset;
            _stream.Write(bytes);
            _writeOffset += bytes.Length;
            return (offset, bytes.Length);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream?.Flush();
        _stream?.Dispose();
        _stream = null;
    }

    private static ulong GenerateFingerprint()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private static void WriteHeader(Stream stream, ulong fingerprint)
    {
        Span<byte> header = stackalloc byte[SidecarConstants.HeaderSize];
        BinaryPrimitives.WriteUInt64LittleEndian(header[0..8], SidecarConstants.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..12], SidecarConstants.Version);
        // header[12..16] reserved (zero)
        BinaryPrimitives.WriteUInt64LittleEndian(header[16..24], fingerprint);
        // header[24..32] reserved (zero)
        stream.Write(header);
    }
}
