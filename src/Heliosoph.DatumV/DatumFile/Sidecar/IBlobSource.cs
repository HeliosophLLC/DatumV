namespace Heliosoph.DatumV.DatumFile.Sidecar;

/// <summary>
/// Read-only random-access source for Large Binary Objects, typically backed by a
/// memory-mapped <c>.datum-blob</c> file. Returned spans are valid for the lifetime
/// of the source — callers must not retain them past <see cref="IDisposable.Dispose"/>.
/// </summary>
/// <remarks>
/// Uses 64-bit coordinates (matching <see cref="IBlobSink"/>) so a single source can
/// address a terabyte-scale sidecar without truncation.
/// </remarks>
public interface IBlobSource : IDisposable
{
    /// <summary>
    /// Returns a read-only span over the bytes at the given absolute offset and length
    /// in the underlying source. The span is a direct view into the source's memory
    /// (e.g. an mmap region) — no copy is made.
    /// </summary>
    /// <param name="offset">Absolute byte offset into the source.</param>
    /// <param name="length">Number of bytes to expose.</param>
    /// <returns>A read-only span over the requested byte range.</returns>
    ReadOnlySpan<byte> Read(long offset, long length);
}
