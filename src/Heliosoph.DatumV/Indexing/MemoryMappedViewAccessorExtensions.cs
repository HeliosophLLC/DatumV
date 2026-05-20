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
}
