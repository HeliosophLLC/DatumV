using DatumIngest.Model;

namespace DatumIngest.DatumFile;

/// <summary>
/// Accumulates per-column zone maps across all row groups for a single <c>.datum</c> file write.
/// Merges each row group's per-column <see cref="DatumZoneMap"/> into a file-level aggregate used
/// for outer zone-map indexes (e.g., <c>.datum-index</c> bloom filter threshold decisions).
/// </summary>
internal sealed class DatumZoneMapAccumulator
{
    private readonly uint[] _nullCounts;
    private readonly object?[] _minimums;
    private readonly object?[] _maximums;
    private readonly DataKind[] _kinds;
    private readonly int _columnCount;

    /// <summary>Creates an accumulator for a schema with the given number of columns.</summary>
    internal DatumZoneMapAccumulator(int columnCount)
    {
        _columnCount = columnCount;
        _nullCounts = new uint[columnCount];
        _minimums = new object?[columnCount];
        _maximums = new object?[columnCount];
        _kinds = new DataKind[columnCount];
    }

    /// <summary>
    /// Merges the zone maps from a flushed row group into the running aggregates.
    /// </summary>
    /// <param name="rowGroupDescriptor">The descriptor for the just-flushed row group.</param>
    internal void Merge(DatumRowGroupDescriptor rowGroupDescriptor)
    {
        for (int columnIndex = 0; columnIndex < _columnCount; columnIndex++)
        {
            DatumZoneMap zoneMap = rowGroupDescriptor.ColumnChunks[columnIndex].ZoneMap;
            _nullCounts[columnIndex] += zoneMap.NullCount;

            if (zoneMap.Minimum is null || zoneMap.Maximum is null)
            {
                continue;
            }

            // Record the kind on first observation; subsequent zone maps should share it.
            if (_kinds[columnIndex] == DataKind.Unknown)
            {
                _kinds[columnIndex] = zoneMap.Kind;
            }

            _minimums[columnIndex] = MergeMinimum(_minimums[columnIndex], zoneMap.Minimum);
            _maximums[columnIndex] = MergeMaximum(_maximums[columnIndex], zoneMap.Maximum);
        }
    }

    /// <summary>Returns the file-level aggregate zone map for the specified column index.</summary>
    internal DatumZoneMap GetFileZoneMap(int columnIndex)
    {
        object? minimum = _minimums[columnIndex];
        object? maximum = _maximums[columnIndex];

        if (minimum is null || maximum is null)
        {
            return new DatumZoneMap(_nullCounts[columnIndex]);
        }

        return new DatumZoneMap(_nullCounts[columnIndex], _kinds[columnIndex], minimum, maximum);
    }

    // ──────────────────── Merge helpers ────────────────────

    private static object MergeMinimum(object? existing, object candidate)
    {
        if (existing is null) return candidate;

        return CompareManaged(candidate, existing) < 0 ? candidate : existing;
    }

    private static object MergeMaximum(object? existing, object candidate)
    {
        if (existing is null) return candidate;

        return CompareManaged(candidate, existing) > 0 ? candidate : existing;
    }

    private static int CompareManaged(object a, object b)
    {
        return ((IComparable)a).CompareTo(b);
    }
}
