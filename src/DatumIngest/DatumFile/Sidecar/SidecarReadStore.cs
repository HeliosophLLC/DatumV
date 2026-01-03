using System.Buffers.Binary;
using System.IO.Hashing;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace DatumIngest.DatumFile.Sidecar;

/// <summary>
/// Memory-mapped read-only view over a <c>.datum-blob</c> sidecar file. Returned
/// spans are direct windows into the OS page cache — no per-read copying — and
/// remain valid until <see cref="Dispose"/>.
/// </summary>
/// <remarks>
/// <para>
/// Construction validates the sidecar's header (magic, version, fingerprint match)
/// and throws <see cref="InvalidDataException"/> on corruption or fingerprint
/// mismatch with the companion <c>.datum</c> file. Once open, the store is
/// thread-safe for concurrent <see cref="Read"/> calls.
/// </para>
/// </remarks>
public sealed class SidecarReadStore : IBlobSource
{
    private readonly MemoryMappedFile _mappedFile;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly unsafe byte* _basePointer;
    private readonly long _fileLength;
    private bool _disposed;

    /// <summary>
    /// Opens the sidecar at <paramref name="path"/>, validates its header, and
    /// verifies the fingerprint matches <paramref name="expectedFingerprint"/>
    /// (which the caller reads from the companion <c>.datum</c> footer).
    /// </summary>
    /// <param name="path">Full path to the <c>.datum-blob</c> file.</param>
    /// <param name="expectedFingerprint">The fingerprint recorded in the
    /// companion <c>.datum</c> file's footer.</param>
    /// <exception cref="FileNotFoundException">The sidecar file does not exist.</exception>
    /// <exception cref="InvalidDataException">The header is malformed, the version
    /// is unsupported, or the fingerprint does not match.</exception>
    public unsafe SidecarReadStore(string path, ulong expectedFingerprint)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Sidecar file not found: {path}. The companion .datum file references " +
                $"a sidecar but it is missing or has been moved separately.", path);
        }

        long length = new FileInfo(path).Length;
        if (length < SidecarConstants.HeaderSize)
        {
            throw new InvalidDataException(
                $"Sidecar file '{path}' is shorter than the header size " +
                $"({length} < {SidecarConstants.HeaderSize}).");
        }

        _mappedFile = MemoryMappedFile.CreateFromFile(
            path, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
        _accessor = _mappedFile.CreateViewAccessor(
            0, 0, MemoryMappedFileAccess.Read);
        _fileLength = length;

        byte* ptr = null;
        _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        _basePointer = ptr;

        try
        {
            ValidateHeader(expectedFingerprint);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens a sidecar at <paramref name="path"/> using the fingerprint
    /// recorded in the file's own header — i.e., skipping the
    /// cross-reference check against the companion <c>.datum</c> file.
    /// Used by <see cref="V2.DatumFileWriterV2.OpenForAppend"/> when
    /// rehydrating partial-page values during append; the calling
    /// writer trusts the local sidecar because it's about to write to
    /// it. Payload-hash validation still runs (a non-zero stored hash
    /// is verified on open).
    /// </summary>
    public static SidecarReadStore OpenWithoutFingerprintCheck(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Sidecar file not found: {path}", path);
        }

        ulong fingerprint;
        using (FileStream readHeader = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (readHeader.Length < SidecarConstants.HeaderSize)
            {
                throw new InvalidDataException(
                    $"Sidecar '{path}' is shorter than the header size.");
            }
            Span<byte> header = stackalloc byte[SidecarConstants.HeaderSize];
            readHeader.ReadExactly(header);
            fingerprint = BinaryPrimitives.ReadUInt64LittleEndian(header[16..24]);
        }
        return new SidecarReadStore(path, fingerprint);
    }

    /// <inheritdoc />
    public unsafe ReadOnlySpan<byte> Read(long offset, long length)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (offset < SidecarConstants.HeaderSize)
        {
            throw new ArgumentOutOfRangeException(nameof(offset),
                $"Offset {offset} is inside the sidecar header region " +
                $"(< {SidecarConstants.HeaderSize}); blob coordinates must address payload bytes.");
        }
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Length must be non-negative.");
        }
        if (offset + length > _fileLength)
        {
            throw new ArgumentOutOfRangeException(nameof(length),
                $"Read [{offset}..{offset + length}) extends past the sidecar's end ({_fileLength}).");
        }
        if (length > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(length),
                $"Length {length} exceeds Span<byte>'s int.MaxValue limit. Use a streaming " +
                $"reader for blobs > 2 GB.");
        }

        return new ReadOnlySpan<byte>(_basePointer + offset, (int)length);
    }

    /// <inheritdoc />
    public unsafe void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_basePointer != null)
        {
            _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        }
        _accessor.Dispose();
        _mappedFile.Dispose();
    }

    private unsafe void ValidateHeader(ulong expectedFingerprint)
    {
        ReadOnlySpan<byte> header = new(_basePointer, SidecarConstants.HeaderSize);

        ulong magic = BinaryPrimitives.ReadUInt64LittleEndian(header[0..8]);
        if (magic != SidecarConstants.Magic)
        {
            throw new InvalidDataException(
                $"Sidecar magic mismatch: file does not appear to be a .datum-blob sidecar.");
        }

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
        if (version != SidecarConstants.Version)
        {
            throw new InvalidDataException(
                $"Unsupported sidecar version: file v{version}, reader v{SidecarConstants.Version}.");
        }

        ulong actualFingerprint = BinaryPrimitives.ReadUInt64LittleEndian(header[16..24]);
        if (actualFingerprint != expectedFingerprint)
        {
            throw new InvalidDataException(
                $"Sidecar fingerprint mismatch: companion .datum expects {expectedFingerprint:X16} " +
                $"but the sidecar carries {actualFingerprint:X16}. The sidecar is stale or has been " +
                $"swapped with one from a different .datum file.");
        }

        ulong storedHash = BinaryPrimitives.ReadUInt64LittleEndian(
            header.Slice(SidecarConstants.PayloadHashOffset, 8));

        // Zero hash means "unhashed" — produced by writers predating Phase 9b. Skip
        // verification for these so older sidecars keep opening without re-ingest.
        if (storedHash == 0) return;

        ulong actualHash = ComputePayloadHash();
        if (actualHash != storedHash)
        {
            throw new InvalidDataException(
                $"Sidecar payload corrupted: header records xxHash3-64 {storedHash:X16}, " +
                $"actual content hashes to {actualHash:X16}. The .datum-blob has been " +
                $"modified or truncated since it was written.");
        }
    }

    private unsafe ulong ComputePayloadHash()
    {
        long payloadLength = _fileLength - SidecarConstants.HeaderSize;
        XxHash3 hasher = new();

        // Walk the mmap in span-sized windows. xxHash3 streams without needing
        // contiguous access to the whole file; this lets us hash multi-GB sidecars
        // without converting them to a single Span (which would be capped at int.MaxValue).
        byte* cursor = _basePointer + SidecarConstants.HeaderSize;
        long remaining = payloadLength;
        const int ChunkSize = 1 << 24; // 16 MiB

        while (remaining > 0)
        {
            int take = (int)Math.Min(remaining, ChunkSize);
            hasher.Append(new ReadOnlySpan<byte>(cursor, take));
            cursor += take;
            remaining -= take;
        }

        return hasher.GetCurrentHashAsUInt64();
    }
}
