using System.IO.MemoryMappedFiles;

namespace Heliosoph.DatumV.Indexing;

/// <summary>
/// Extension methods for <see cref="MemoryMappedViewAccessor"/> to read into spans.
/// Shared across every mmap-backed index reader (sorted, bloom, bitmap, B+Tree).
/// </summary>
internal static class MemoryMappedViewAccessorExtensions
{
    /// <summary>
    /// Reads a sequence of bytes from the accessor into the destination span
    /// using the bounds-checked <see cref="System.Runtime.InteropServices.SafeBuffer.ReadSpan{T}"/> API.
    /// </summary>
    /// <param name="accessor">The view accessor to read from.</param>
    /// <param name="position">The byte position in the accessor to start reading.</param>
    /// <param name="destination">The span to fill with bytes from the accessor.</param>
    public static void ReadArray(this MemoryMappedViewAccessor accessor, long position, Span<byte> destination)
    {
        accessor.SafeMemoryMappedViewHandle.ReadSpan(
            (ulong)(accessor.PointerOffset + position), destination);
    }

    /// <summary>
    /// Verifies that a read of <paramref name="byteCount"/> bytes starting at
    /// <paramref name="position"/> fits within <paramref name="maxOffset"/>,
    /// throwing <see cref="InvalidDataException"/> with a torn-write message
    /// otherwise. Use before issuing any MMF read whose offset/length comes
    /// from on-disk metadata that has not already been validated against the
    /// file's true extent — Windows MMF views are page-padded and silently
    /// zero-fill an over-read; Linux views are exact-sized and throw an
    /// opaque <see cref="ArgumentException"/>. Calling this first makes both
    /// platforms emit the same surface-area error for corrupt indexes.
    /// </summary>
    public static void ValidateReadBounds(long position, long byteCount, long maxOffset)
    {
        if (position < 0 || byteCount < 0 || position + byteCount > maxOffset)
        {
            throw new InvalidDataException(
                $"Index read of {byteCount} bytes at offset {position} extends beyond bound {maxOffset}; "
                + "treat as torn write and run REINDEX.");
        }
    }
}
