namespace DatumIngest.Indexing;

/// <summary>
/// Controls which index implementation is used when building column indexes
/// during <see cref="DatumIngester.BuildIndexAsync(string, DatumIndexerOptions?, System.Action{IndexingProgress}?, System.Threading.CancellationToken)"/>.
/// </summary>
public enum IndexStrategy
{
    /// <summary>
    /// Automatically selects the index implementation per column based on entry count.
    /// Columns with fewer than ~5 million entries use <see cref="Sorted"/>; larger
    /// columns use <see cref="BTree"/> to avoid excessive heap allocation.
    /// </summary>
    Auto,

    /// <summary>
    /// Forces a flat sorted array index (<see cref="SortedValueIndex"/>) for all columns.
    /// Fastest for lookups when the index fits comfortably in memory, but can cause
    /// <see cref="System.OutOfMemoryException"/> on very large datasets.
    /// </summary>
    Sorted,

    /// <summary>
    /// Forces a disk-resident B+Tree index for all columns, regardless of size.
    /// Useful for testing the B+Tree path on small datasets without requiring
    /// a multi-million row source.
    /// </summary>
    BTree,
}
