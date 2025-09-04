using System.Diagnostics.CodeAnalysis;
using System.IO.MemoryMappedFiles;

namespace DatumIngest.Indexing;

/// <summary>
/// Collection of <see cref="MappedSortedIndex"/> instances keyed by column name, backed
/// by a single memory-mapped file. Disposing this set releases the memory-mapped file
/// and all associated view accessors.
/// </summary>
internal sealed class MappedSortedIndexSet : IDisposable
{
    private readonly MemoryMappedFile _memoryMappedFile;
    private readonly MemoryMappedViewAccessor _sharedAccessor;
    private readonly Dictionary<string, MappedSortedIndex> _indexes;
    private bool _disposed;

    /// <summary>Column names that have mapped sorted indexes.</summary>
    public IReadOnlyCollection<string> ColumnNames => _indexes.Keys;

    /// <summary>Number of indexed columns.</summary>
    public int Count => _indexes.Count;

    /// <summary>
    /// Creates a mapped sorted index set owning the given memory-mapped file.
    /// </summary>
    /// <param name="memoryMappedFile">The memory-mapped file (disposed when this set is disposed).</param>
    /// <param name="sharedAccessor">The shared view accessor used by all column indexes.</param>
    /// <param name="indexes">Per-column mapped sorted indexes.</param>
    public MappedSortedIndexSet(
        MemoryMappedFile memoryMappedFile,
        MemoryMappedViewAccessor sharedAccessor,
        Dictionary<string, MappedSortedIndex> indexes)
    {
        _memoryMappedFile = memoryMappedFile;
        _sharedAccessor = sharedAccessor;
        _indexes = indexes;
    }

    /// <summary>
    /// Retrieves the mapped sorted index for a specific column.
    /// </summary>
    /// <param name="columnName">Column name (case-insensitive lookup).</param>
    /// <param name="index">The column index, or <c>null</c> if not available.</param>
    /// <returns><c>true</c> if a mapped index exists for the specified column.</returns>
    public bool TryGetIndex(string columnName, [NotNullWhen(true)] out MappedSortedIndex? index)
    {
        return _indexes.TryGetValue(columnName, out index);
    }

    /// <summary>
    /// Checks whether a mapped sorted index exists for the specified column.
    /// </summary>
    /// <param name="columnName">Column name to check.</param>
    /// <returns><c>true</c> if a mapped index is available for this column.</returns>
    public bool HasColumn(string columnName)
    {
        return _indexes.ContainsKey(columnName);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // The individual MappedSortedIndex instances share the same accessor,
        // so we do NOT dispose them individually — the shared accessor handles cleanup.
        _sharedAccessor.Dispose();
        _memoryMappedFile.Dispose();
    }
}
