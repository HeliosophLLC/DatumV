using DatumIngest.Model;

namespace DatumIngest.Indexing;

/// <summary>
/// Unified interface for column-level index lookup. Operators and the query planner
/// consume indexes through this interface rather than concrete types, allowing
/// transparent substitution of <see cref="SortedValueIndex"/> (in-memory flat array)
/// and future B+Tree (disk-resident, demand-paged) implementations.
/// </summary>
/// <remarks>
/// <para>
/// The single entry point for obtaining an <see cref="IColumnIndex"/> is
/// <see cref="SourceIndex.TryGetColumnIndex"/>. Callers never construct or
/// select a concrete implementation directly.
/// </para>
/// <para>
/// All methods returning chunk sets use inclusive bounds unless otherwise noted.
/// Key comparison follows <see cref="Execution.StatisticsPredicateEvaluator.CompareValues"/>
/// semantics.
/// </para>
/// </remarks>
public interface IColumnIndex
{
    /// <summary>Total number of entries in this index.</summary>
    long EntryCount { get; }

    /// <summary>
    /// Searches for the exact key value and returns all matching entries.
    /// </summary>
    /// <param name="key">The value to look up.</param>
    /// <returns>All entries whose key equals the search value.</returns>
    IReadOnlyList<ValueIndexEntry> FindExact(DataValue key);

    /// <summary>
    /// Returns all entries whose key falls within the inclusive range [low, high].
    /// </summary>
    /// <param name="low">Lower bound (inclusive).</param>
    /// <param name="high">Upper bound (inclusive).</param>
    /// <returns>All entries with keys in the specified range.</returns>
    IReadOnlyList<ValueIndexEntry> FindRange(DataValue low, DataValue high);

    /// <summary>
    /// Returns the set of chunk indexes that contain any entry with the given key.
    /// </summary>
    /// <param name="key">The value to look up.</param>
    /// <returns>Distinct chunk indexes containing the key.</returns>
    IReadOnlySet<int> FindChunksContaining(DataValue key);

    /// <summary>
    /// Returns the set of chunk indexes that contain entries in the inclusive range.
    /// </summary>
    /// <param name="low">Lower bound (inclusive).</param>
    /// <param name="high">Upper bound (inclusive).</param>
    /// <returns>Distinct chunk indexes with keys in the range.</returns>
    IReadOnlySet<int> FindChunksInRange(DataValue low, DataValue high);

    /// <summary>
    /// Returns the set of chunk indexes that contain any entry with a key
    /// strictly less than the given bound.
    /// </summary>
    /// <param name="bound">The exclusive upper bound.</param>
    /// <returns>Distinct chunk indexes with keys less than the bound.</returns>
    IReadOnlySet<int> FindChunksLessThan(DataValue bound);

    /// <summary>
    /// Returns the set of chunk indexes that contain any entry with a key
    /// less than or equal to the given bound.
    /// </summary>
    /// <param name="bound">The inclusive upper bound.</param>
    /// <returns>Distinct chunk indexes with keys less than or equal to the bound.</returns>
    IReadOnlySet<int> FindChunksLessThanOrEqual(DataValue bound);

    /// <summary>
    /// Returns the set of chunk indexes that contain any entry with a key
    /// strictly greater than the given bound.
    /// </summary>
    /// <param name="bound">The exclusive lower bound.</param>
    /// <returns>Distinct chunk indexes with keys greater than the bound.</returns>
    IReadOnlySet<int> FindChunksGreaterThan(DataValue bound);

    /// <summary>
    /// Returns the set of chunk indexes that contain any entry with a key
    /// greater than or equal to the given bound.
    /// </summary>
    /// <param name="bound">The inclusive lower bound.</param>
    /// <returns>Distinct chunk indexes with keys greater than or equal to the bound.</returns>
    IReadOnlySet<int> FindChunksGreaterThanOrEqual(DataValue bound);

    /// <summary>
    /// Enumerates all entries in ascending key order. For <see cref="SortedValueIndex"/>,
    /// this iterates the in-memory array. For B+Tree, this walks the leaf chain
    /// page-by-page without materializing the full index.
    /// </summary>
    /// <returns>All entries in ascending key order.</returns>
    IEnumerable<ValueIndexEntry> TraverseForward();

    /// <summary>
    /// Enumerates all entries in descending key order. For <see cref="SortedValueIndex"/>,
    /// this reverse-iterates the in-memory array. For B+Tree, this walks the leaf chain
    /// backward page-by-page.
    /// </summary>
    /// <returns>All entries in descending key order.</returns>
    IEnumerable<ValueIndexEntry> TraverseBackward();
}
