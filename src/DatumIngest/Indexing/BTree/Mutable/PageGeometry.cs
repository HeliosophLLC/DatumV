namespace DatumIngest.Indexing.BTree.Mutable;

/// <summary>
/// Per-tree page-size configuration. Production trees use 8 KiB pages
/// (<see cref="Default"/>); contract tests can construct smaller geometries
/// to exercise split logic at legible workloads (e.g. 512 B pages force
/// leaf splits at ~30 entries instead of ~700).
/// </summary>
/// <remarks>
/// The page size is persisted in the tree's header at <c>Create</c> time and
/// read back on <c>Open</c>; once a file exists, its geometry is fixed.
/// </remarks>
internal readonly record struct PageGeometry(int PageSize)
{
    /// <summary>Smallest legal page size. Must fit the leaf header + a couple of small entries.</summary>
    internal const int MinPageSize = 256;

    /// <summary>Largest legal page size. Sanity ceiling — production uses 8 KiB.</summary>
    internal const int MaxPageSize = 1 << 20; // 1 MiB

    /// <summary>Production default: 8 KiB pages.</summary>
    internal static PageGeometry Default { get; } = new(8192);

    /// <summary>Bytes available for leaf entries after the leaf header.</summary>
    internal int LeafPayloadCapacity => PageSize - MutableBPlusTreeConstants.LeafHeaderSize;

    /// <summary>
    /// Throws if <see cref="PageSize"/> is outside [<see cref="MinPageSize"/>,
    /// <see cref="MaxPageSize"/>]. Called by <c>Create</c> (caller-supplied) and
    /// <c>Open</c> (file-supplied) so corrupt headers fail loudly rather than
    /// silently producing pages of nonsensical size.
    /// </summary>
    internal void Validate()
    {
        if (PageSize < MinPageSize || PageSize > MaxPageSize)
        {
            throw new InvalidDataException(
                $"Page size {PageSize} is outside the legal range [{MinPageSize}, {MaxPageSize}].");
        }
    }
}
