using System.Diagnostics.CodeAnalysis;

namespace Heliosoph.DatumV.Indexing.Bloom;

/// <summary>
/// Collection of <see cref="BloomFilter"/> instances keyed by column name and chunk index.
/// Each bloom filter records membership for one column within one chunk, enabling
/// chunk-level pruning during joins: if a join key is definitely absent from a chunk's
/// bloom filter, that entire chunk can be skipped.
/// </summary>
public sealed class BloomFilterSet
{
    private readonly Dictionary<string, BloomFilter[]> _filters;

    /// <summary>Column names that have bloom filters.</summary>
    public IReadOnlyCollection<string> ColumnNames => _filters.Keys;

    /// <summary>
    /// Gets the number of columns with bloom filters.
    /// </summary>
    public int ColumnCount => _filters.Count;

    /// <summary>Number of chunks per column (all columns share the same chunk count).</summary>
    public int ChunkCount { get; }

    /// <summary>
    /// Creates a bloom filter set from a pre-built dictionary.
    /// </summary>
    /// <param name="filters">Bloom filters keyed by column name, each array indexed by chunk index.</param>
    /// <param name="chunkCount">Number of chunks.</param>
    public BloomFilterSet(Dictionary<string, BloomFilter[]> filters, int chunkCount)
    {
        _filters = filters;
        ChunkCount = chunkCount;
    }

    /// <summary>
    /// Retrieves the bloom filter for a specific column and chunk.
    /// </summary>
    /// <param name="columnName">Column name (case-insensitive lookup).</param>
    /// <param name="chunkIndex">Zero-based chunk index.</param>
    /// <param name="filter">The bloom filter, or <c>null</c> if not available.</param>
    /// <returns><c>true</c> if a bloom filter exists for the specified column and chunk.</returns>
    public bool TryGetFilter(string columnName, int chunkIndex, [NotNullWhen(true)] out BloomFilter? filter)
    {
        if (_filters.TryGetValue(columnName, out BloomFilter[]? columnFilters)
            && chunkIndex >= 0
            && chunkIndex < columnFilters.Length)
        {
            filter = columnFilters[chunkIndex];
            return filter is not null;
        }

        filter = null;
        return false;
    }

    /// <summary>
    /// Checks whether bloom filters exist for the specified column.
    /// </summary>
    /// <param name="columnName">Column name to check.</param>
    /// <returns><c>true</c> if bloom filters are available for this column.</returns>
    public bool HasColumn(string columnName)
    {
        return _filters.ContainsKey(columnName);
    }

    /// <summary>
    /// Returns all bloom filters for a column, indexed by chunk.
    /// </summary>
    /// <param name="columnName">Column name.</param>
    /// <returns>Array of bloom filters, one per chunk, or <c>null</c> if the column is not indexed.</returns>
    public BloomFilter[]? GetColumnFilters(string columnName)
    {
        return _filters.TryGetValue(columnName, out BloomFilter[]? filters) ? filters : null;
    }

    /// <summary>
    /// Returns the underlying dictionary for serialization.
    /// </summary>
    internal IReadOnlyDictionary<string, BloomFilter[]> Filters => _filters;
}
