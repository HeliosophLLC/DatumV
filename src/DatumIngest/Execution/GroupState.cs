using DatumIngest.Functions;
using DatumIngest.Model;

namespace DatumIngest.Execution;

/// <summary>
/// Mutable state for a single group during hash aggregation.
/// Pooled by <see cref="GlobalBufferPool"/> to avoid per-group heap allocations.
/// </summary>
public sealed class GroupState
{
    /// <summary>The GROUP BY key values for this group (null for global aggregation).</summary>
    public DataValue[]? KeyValues;

    /// <summary>
    /// Logical number of accumulators. May be less than <see cref="Accumulators"/>
    /// array length when the backing array comes from <see cref="System.Buffers.ArrayPool{T}"/>.
    /// </summary>
    public int AccumulatorCount;

    /// <summary>One accumulator per aggregate column.</summary>
    public IAggregateAccumulator[] Accumulators = [];

    /// <summary>
    /// Buffered rows for aggregates with intra-aggregate ORDER BY. Null when
    /// no aggregates in the query use ORDER BY. Each element is either null
    /// (aggregate has no ORDER BY) or a list of (arguments, sort-keys) tuples.
    /// </summary>
    public List<(DataValue[] Arguments, DataValue[] SortKeys)>?[]? OrderedBuffers;
}
