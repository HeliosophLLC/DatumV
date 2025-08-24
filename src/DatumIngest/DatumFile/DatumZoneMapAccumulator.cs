using DatumIngest.Execution;
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
    private readonly DataValue?[] _minimums;
    private readonly DataValue?[] _maximums;
    private readonly int _columnCount;

    /// <summary>Creates an accumulator for a schema with the given number of columns.</summary>
    internal DatumZoneMapAccumulator(int columnCount)
    {
        _columnCount = columnCount;
        _nullCounts = new uint[columnCount];
        _minimums = new DataValue?[columnCount];
        _maximums = new DataValue?[columnCount];
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

            if (!zoneMap.Minimum.HasValue || !zoneMap.Maximum.HasValue)
            {
                continue;
            }

            _minimums[columnIndex] = MergeMinimum(_minimums[columnIndex], zoneMap.Minimum.Value);
            _maximums[columnIndex] = MergeMaximum(_maximums[columnIndex], zoneMap.Maximum.Value);
        }
    }

    /// <summary>Returns the file-level aggregate zone map for the specified column index.</summary>
    internal DatumZoneMap GetFileZoneMap(int columnIndex)
    {
        return new DatumZoneMap(_nullCounts[columnIndex], _minimums[columnIndex], _maximums[columnIndex]);
    }

    // ──────────────────── Merge helpers ────────────────────

    private static DataValue? MergeMinimum(DataValue? existing, DataValue candidate)
    {
        if (existing is null) return candidate;

        return StatisticsPredicateEvaluator.CompareValues(candidate, existing.Value) < 0 ? candidate : existing;
    }

    private static DataValue? MergeMaximum(DataValue? existing, DataValue candidate)
    {
        if (existing is null) return candidate;

        return StatisticsPredicateEvaluator.CompareValues(candidate, existing.Value) > 0 ? candidate : existing;
    }
}
