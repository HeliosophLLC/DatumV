using System.Security.Cryptography;

namespace DatumIngest.Indexing;

/// <summary>
/// Immutable fingerprint of a source file for staleness detection.
/// Computes a striped hash by reading <see cref="IndexConstants.FingerprintSampleSize"/> bytes
/// every <see cref="IndexConstants.FingerprintSampleInterval"/> bytes, then hashing all
/// samples together with SHA-256. Combined with file size, this detects modifications
/// without reading the entire file — suitable for multi-GB blobs accessed via HTTP range reads.
/// </summary>
public sealed class SourceFingerprint : IEquatable<SourceFingerprint>
{
    /// <summary>Total size of the source file in bytes.</summary>
    public long FileSize { get; }

    /// <summary>SHA-256 hash of the concatenated stripe samples (32 bytes).</summary>
    public byte[] StripedHash { get; }

    /// <summary>
    /// Creates a fingerprint from pre-computed components.
    /// </summary>
    /// <param name="fileSize">Total size of the source file in bytes.</param>
    /// <param name="stripedHash">SHA-256 hash of the concatenated stripe samples.</param>
    public SourceFingerprint(long fileSize, byte[] stripedHash)
    {
        FileSize = fileSize;
        StripedHash = stripedHash;
    }

    /// <summary>
    /// Computes a fingerprint for the given source stream.
    /// The stream must support <see cref="Stream.Length"/> and <see cref="Stream.Seek"/>.
    /// </summary>
    /// <param name="source">Readable, seekable stream over the source file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A new fingerprint capturing the file's size and content signature.</returns>
    public static async Task<SourceFingerprint> ComputeAsync(Stream source, CancellationToken cancellationToken)
    {
        long fileSize = source.Length;
        byte[] hash = await ComputeStripedHashAsync(source, fileSize, cancellationToken).ConfigureAwait(false);
        return new SourceFingerprint(fileSize, hash);
    }

    /// <summary>
    /// Synchronous variant of <see cref="ComputeAsync"/>. Both produce the
    /// same fingerprint; pick the path that matches the caller's context.
    /// </summary>
    public static SourceFingerprint Compute(Stream source)
    {
        long fileSize = source.Length;
        byte[] hash = ComputeStripedHash(source, fileSize);
        return new SourceFingerprint(fileSize, hash);
    }

    /// <summary>
    /// Checks whether a source stream still matches this fingerprint.
    /// </summary>
    /// <param name="source">Readable, seekable stream over the source file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if the file matches; <c>false</c> if it has changed.</returns>
    public async Task<bool> MatchesAsync(Stream source, CancellationToken cancellationToken)
    {
        if (source.Length != FileSize)
        {
            return false;
        }

        byte[] hash = await ComputeStripedHashAsync(source, FileSize, cancellationToken).ConfigureAwait(false);
        return StripedHash.AsSpan().SequenceEqual(hash);
    }

    /// <summary>
    /// Synchronous variant of <see cref="MatchesAsync"/>.
    /// </summary>
    public bool Matches(Stream source)
    {
        if (source.Length != FileSize)
        {
            return false;
        }

        byte[] hash = ComputeStripedHash(source, FileSize);
        return StripedHash.AsSpan().SequenceEqual(hash);
    }

    private static async Task<byte[]> ComputeStripedHashAsync(
        Stream source,
        long fileSize,
        CancellationToken cancellationToken)
    {
        using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] sampleBuffer = new byte[IndexConstants.FingerprintSampleSize];

        long position = 0;
        while (position < fileSize)
        {
            source.Position = position;
            int bytesToRead = (int)Math.Min(IndexConstants.FingerprintSampleSize, fileSize - position);
            int totalRead = 0;

            while (totalRead < bytesToRead)
            {
                int read = await source.ReadAsync(
                    sampleBuffer.AsMemory(totalRead, bytesToRead - totalRead),
                    cancellationToken).ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }

            hasher.AppendData(sampleBuffer, 0, totalRead);
            position += IndexConstants.FingerprintSampleInterval;
        }

        return hasher.GetHashAndReset();
    }

    private static byte[] ComputeStripedHash(Stream source, long fileSize)
    {
        using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] sampleBuffer = new byte[IndexConstants.FingerprintSampleSize];

        long position = 0;
        while (position < fileSize)
        {
            source.Position = position;
            int bytesToRead = (int)Math.Min(IndexConstants.FingerprintSampleSize, fileSize - position);
            int totalRead = 0;

            while (totalRead < bytesToRead)
            {
                int read = source.Read(sampleBuffer, totalRead, bytesToRead - totalRead);
                if (read == 0) break;
                totalRead += read;
            }

            hasher.AppendData(sampleBuffer, 0, totalRead);
            position += IndexConstants.FingerprintSampleInterval;
        }

        return hasher.GetHashAndReset();
    }

    /// <inheritdoc/>
    public bool Equals(SourceFingerprint? other)
    {
        if (other is null)
        {
            return false;
        }

        return FileSize == other.FileSize && StripedHash.AsSpan().SequenceEqual(other.StripedHash);
    }

    /// <inheritdoc/>
    public override bool Equals(object? other) => Equals(other as SourceFingerprint);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hashCode = new();
        hashCode.Add(FileSize);
        hashCode.AddBytes(StripedHash);
        return hashCode.ToHashCode();
    }
}
