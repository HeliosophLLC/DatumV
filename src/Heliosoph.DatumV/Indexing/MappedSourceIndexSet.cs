using System.IO.MemoryMappedFiles;
using Heliosoph.DatumV.Indexing.Sorted;

namespace Heliosoph.DatumV.Indexing;

/// <summary>
/// Owns the memory-mapped file backing a v5 unified index, exposing a
/// <see cref="SourceIndexSet"/> whose mapped types (sorted indexes, B+Tree pages,
/// bloom filters, bitmap indexes) read directly from the mapped view without
/// materializing data into the managed heap.
/// </summary>
/// <remarks>
/// The <see cref="SourceIndexSet"/> and all <see cref="SourceIndex"/> instances
/// within it remain valid only while this set is undisposed.
/// </remarks>
internal sealed class MappedSourceIndexSet : IDisposable
{
    private readonly MemoryMappedFile _memoryMappedFile;
    private readonly MemoryMappedViewAccessor _sharedAccessor;
    private bool _disposed;

    /// <summary>The deserialized index set. Valid only while this instance is undisposed.</summary>
    public SourceIndexSet IndexSet { get; }

    /// <summary>
    /// The shared read-only view accessor covering the entire mapped file.
    /// Used by mmap-backed accessors (e.g. <see cref="Bloom.BloomFilter"/>) to read data on demand.
    /// </summary>
    internal MemoryMappedViewAccessor SharedAccessor => _sharedAccessor;

    /// <summary>
    /// Creates a mapped source index set owning the given memory-mapped file.
    /// </summary>
    /// <param name="memoryMappedFile">The memory-mapped file (disposed when this set is disposed).</param>
    /// <param name="sharedAccessor">The shared view accessor covering the entire file.</param>
    /// <param name="indexSet">The deserialized index set.</param>
    public MappedSourceIndexSet(
        MemoryMappedFile memoryMappedFile,
        MemoryMappedViewAccessor sharedAccessor,
        SourceIndexSet indexSet)
    {
        _memoryMappedFile = memoryMappedFile;
        _sharedAccessor = sharedAccessor;
        IndexSet = indexSet;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sharedAccessor.Dispose();
        _memoryMappedFile.Dispose();
    }
}
