using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DatumIngest.DatumFile.V2;

/// <summary>
/// Writes a <c>.datum-pack</c> file — the minimal container for
/// pages that have moved out of a primary <c>.datum</c> file (e.g. by
/// compaction). The writer emits a 32-byte header at construction and
/// appends raw page bytes via <see cref="AppendPage"/>; each call
/// returns the absolute file offset of the page so the caller can
/// stamp it into the primary's <see cref="PageDescriptorV2.PageOffset"/>.
/// </summary>
/// <remarks>
/// <para>
/// Pack files are intentionally minimal — no schema, no footer, no
/// tail. The primary <c>.datum</c>'s footer is authoritative; the
/// pack just holds bytes. Compaction-style code paths use this
/// writer to emit pages and the
/// <see cref="FileTableEntryV4.Fingerprint"/> bytes <see cref="Fingerprint"/>
/// returns to record a matching entry in the primary's footer.
/// </para>
/// <para>
/// PR7 ships the writer as the primitive both compaction (future) and
/// PR7 tests use to construct cross-file scenarios. The reader-side
/// dispatch through file-id lives in
/// <see cref="DatumFileReaderV2"/>.
/// </para>
/// </remarks>
public sealed class DatumPackWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private bool _disposed;

    /// <summary>
    /// Creates a pack writer at <paramref name="path"/>, overwriting
    /// any existing file. Generates a fresh
    /// <see cref="Fingerprint"/> and writes the header immediately;
    /// callers can begin appending pages right after construction.
    /// </summary>
    public DatumPackWriter(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 65_536,
            FileOptions.SequentialScan);
        _ownsStream = true;

        Fingerprint = GenerateFingerprint();
        WriteHeader(_stream, Fingerprint);
    }

    /// <summary>
    /// Creates a pack writer over <paramref name="stream"/>. The
    /// caller retains ownership of the stream. The header is written
    /// at the current stream position; downstream pages stack
    /// after it.
    /// </summary>
    public DatumPackWriter(Stream stream, byte[] fingerprint)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(fingerprint);
        if (fingerprint.Length != DatumPackConstants.FingerprintBytes)
        {
            throw new ArgumentException(
                $"Fingerprint must be {DatumPackConstants.FingerprintBytes} bytes.",
                nameof(fingerprint));
        }

        _stream = stream;
        _ownsStream = false;

        Fingerprint = (byte[])fingerprint.Clone();
        WriteHeader(_stream, Fingerprint);
    }

    /// <summary>
    /// 16-byte random identity stamp generated at construction.
    /// Callers must record this in the primary
    /// <c>.datum</c>'s file-table entry that points at this pack;
    /// readers verify the match on open.
    /// </summary>
    public byte[] Fingerprint { get; }

    /// <summary>
    /// Appends <paramref name="pageBytes"/> at the current end of the
    /// pack and returns the absolute file offset where they landed.
    /// The returned offset goes directly into
    /// <see cref="PageDescriptorV2.PageOffset"/> in the primary
    /// <c>.datum</c>'s footer.
    /// </summary>
    public long AppendPage(ReadOnlySpan<byte> pageBytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long offset = _stream.Position;
        _stream.Write(pageBytes);
        return offset;
    }

    /// <summary>Closes the pack file (when owned).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_ownsStream)
        {
            _stream.Flush();
            _stream.Dispose();
        }
    }

    private static byte[] GenerateFingerprint()
    {
        byte[] bytes = new byte[DatumPackConstants.FingerprintBytes];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    private static void WriteHeader(Stream stream, byte[] fingerprint)
    {
        Span<byte> header = stackalloc byte[DatumPackConstants.HeaderSize];
        DatumPackConstants.Magic.CopyTo(header[0..8]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..12], DatumPackConstants.Version);
        // bytes 12..16 reserved (zero)
        fingerprint.CopyTo(header[16..32]);
        stream.Write(header);
    }
}
