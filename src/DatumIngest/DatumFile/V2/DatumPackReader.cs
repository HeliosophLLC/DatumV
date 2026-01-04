using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;

namespace DatumIngest.DatumFile.V2;

/// <summary>
/// Memory-mapped read-only view over a <c>.datum-pack</c> file.
/// Validates the header (magic, version, fingerprint) on open and
/// satisfies <see cref="ReadBytesAt"/> calls by copying out of the
/// mmap. One pack reader per external file; the primary
/// <see cref="DatumFileReaderV2"/> opens one per file-table entry on
/// initial open and disposes them when the primary reader disposes.
/// </summary>
/// <remarks>
/// <para>
/// Fingerprint validation catches stale-manifest cases where an
/// external pack has been replaced or recreated under the same path
/// since the primary's footer was written. Mismatch raises
/// <see cref="InvalidDataException"/>.
/// </para>
/// <para>
/// Returned spans are direct mmap views; callers must finish using
/// them before disposing the reader. The primary
/// <see cref="DatumFileReaderV2"/> copies bytes out into freshly-
/// allocated arrays before returning to consumers, so consumer
/// lifetimes don't bind to the reader.
/// </para>
/// </remarks>
public sealed class DatumPackReader : IDisposable
{
    private readonly MemoryMappedFile _mappedFile;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly unsafe byte* _basePointer;
    private readonly long _fileLength;
    private bool _disposed;

    /// <summary>
    /// Opens the pack at <paramref name="path"/>, validates its
    /// header, and verifies the fingerprint matches
    /// <paramref name="expectedFingerprint"/> (which the caller reads
    /// from the primary <c>.datum</c>'s file-table entry).
    /// </summary>
    public unsafe DatumPackReader(string path, ReadOnlySpan<byte> expectedFingerprint)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Pack file not found: {path}. The primary .datum's file table references " +
                "an external pack that is missing or has been moved.", path);
        }

        long length = new FileInfo(path).Length;
        if (length < DatumPackConstants.HeaderSize)
        {
            throw new InvalidDataException(
                $"Pack file '{path}' is shorter than the header size " +
                $"({length} < {DatumPackConstants.HeaderSize}).");
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
    /// Reads <paramref name="destination"/>.Length bytes starting at
    /// <paramref name="offset"/> in the pack file. Throws if the
    /// range escapes the pack's payload region (offset less than
    /// header size or end past EOF).
    /// </summary>
    public unsafe void ReadBytesAt(long offset, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (offset < DatumPackConstants.HeaderSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset), offset,
                $"Offset {offset} is inside the pack's header region " +
                $"(< {DatumPackConstants.HeaderSize}); pack page offsets must address payload bytes.");
        }
        if (offset + destination.Length > _fileLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(destination),
                $"Read [{offset}..{offset + destination.Length}) extends past the pack's end ({_fileLength}).");
        }

        ReadOnlySpan<byte> source = new(_basePointer + offset, destination.Length);
        source.CopyTo(destination);
    }

    /// <summary>Releases the mmap view and the underlying handle.</summary>
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

    private unsafe void ValidateHeader(ReadOnlySpan<byte> expectedFingerprint)
    {
        ReadOnlySpan<byte> header = new(_basePointer, DatumPackConstants.HeaderSize);

        if (!header[0..8].SequenceEqual(DatumPackConstants.Magic))
        {
            throw new InvalidDataException(
                "Pack magic mismatch: file does not appear to be a .datum-pack.");
        }

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
        if (version != DatumPackConstants.Version)
        {
            throw new InvalidDataException(
                $"Unsupported pack version: file v{version}, reader v{DatumPackConstants.Version}.");
        }

        ReadOnlySpan<byte> actualFingerprint =
            header.Slice(DatumPackConstants.FingerprintOffset, DatumPackConstants.FingerprintBytes);
        if (!actualFingerprint.SequenceEqual(expectedFingerprint))
        {
            throw new InvalidDataException(
                "Pack fingerprint mismatch: the pack at this path does not match the primary " +
                ".datum's file-table entry. The pack has been replaced, recreated, or this " +
                "manifest is stale.");
        }
    }
}
