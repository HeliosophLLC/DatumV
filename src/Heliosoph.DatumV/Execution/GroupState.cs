using System.Runtime.InteropServices;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Execution;

/// <summary>
/// Mutable state for a single group during hash aggregation.
/// Pooled by <see cref="Pooling.Pool"/> to avoid per-group heap allocations.
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
    /// (aggregate has no ORDER BY) or a flat buffer storing arguments and sort
    /// keys with stride-based access to avoid per-row array allocations.
    /// </summary>
    internal OrderedAggregateBuffer?[]? OrderedBuffers;
}

/// <summary>
/// Flat buffer for ordered aggregate (e.g. <c>STRING_AGG(x, ',' ORDER BY y)</c>)
/// deferred rows. Stores arguments and sort keys in contiguous <see cref="List{T}"/>
/// buffers with stride-based access, eliminating per-row <see cref="DataValue"/>[]
/// allocations.
/// </summary>
internal sealed class OrderedAggregateBuffer
{
    private readonly List<DataValue> _arguments = [];
    private readonly List<DataValue> _sortKeys = [];
    private readonly int _argStride;
    private readonly int _sortStride;

    public OrderedAggregateBuffer(int argCount, int sortKeyCount)
    {
        _argStride = argCount;
        _sortStride = sortKeyCount;
    }

    /// <summary>Number of buffered rows.</summary>
    public int Count => _argStride > 0 ? _arguments.Count / _argStride : 0;

    /// <summary>Appends one row of arguments and sort keys from scratch spans.</summary>
    public void Add(ReadOnlySpan<DataValue> arguments, ReadOnlySpan<DataValue> sortKeys)
    {
        CollectionsMarshal.SetCount(_arguments, _arguments.Count + _argStride);
        arguments.Slice(0, _argStride).CopyTo(
            CollectionsMarshal.AsSpan(_arguments).Slice(_arguments.Count - _argStride));

        CollectionsMarshal.SetCount(_sortKeys, _sortKeys.Count + _sortStride);
        sortKeys.Slice(0, _sortStride).CopyTo(
            CollectionsMarshal.AsSpan(_sortKeys).Slice(_sortKeys.Count - _sortStride));
    }

    /// <summary>Returns the arguments for row <paramref name="index"/>.</summary>
    public ReadOnlySpan<DataValue> GetArguments(int index) =>
        CollectionsMarshal.AsSpan(_arguments).Slice(index * _argStride, _argStride);

    /// <summary>Returns the sort keys for row <paramref name="index"/>.</summary>
    public ReadOnlySpan<DataValue> GetSortKeys(int index) =>
        CollectionsMarshal.AsSpan(_sortKeys).Slice(index * _sortStride, _sortStride);

    /// <summary>Sorts buffered rows by sort keys using the given comparison.</summary>
    public void Sort(Comparison<int> rowIndexComparison)
    {
        int count = Count;
        if (count <= 1) return;

        // Build an index array, sort it, then reorder both lists in-place.
        int[] indices = new int[count];
        for (int i = 0; i < count; i++) indices[i] = i;
        Array.Sort(indices, rowIndexComparison);

        // Reorder arguments and sort keys according to sorted indices.
        ReorderByIndices(_arguments, _argStride, indices, count);
        ReorderByIndices(_sortKeys, _sortStride, indices, count);
    }

    /// <summary>Reorders a flat list of stride-packed rows according to the given index permutation.</summary>
    private static void ReorderByIndices(List<DataValue> list, int stride, int[] indices, int count)
    {
        DataValue[] temp = new DataValue[count * stride];
        Span<DataValue> src = CollectionsMarshal.AsSpan(list);
        for (int i = 0; i < count; i++)
        {
            src.Slice(indices[i] * stride, stride).CopyTo(temp.AsSpan(i * stride, stride));
        }
        temp.AsSpan().CopyTo(src);
    }

    /// <summary>Appends all rows from <paramref name="other"/> into this buffer.</summary>
    public void AddRange(OrderedAggregateBuffer other)
    {
        Span<DataValue> otherArgs = CollectionsMarshal.AsSpan(other._arguments);
        Span<DataValue> otherSort = CollectionsMarshal.AsSpan(other._sortKeys);

        int oldArgCount = _arguments.Count;
        CollectionsMarshal.SetCount(_arguments, oldArgCount + otherArgs.Length);
        otherArgs.CopyTo(CollectionsMarshal.AsSpan(_arguments).Slice(oldArgCount));

        int oldSortCount = _sortKeys.Count;
        CollectionsMarshal.SetCount(_sortKeys, oldSortCount + otherSort.Length);
        otherSort.CopyTo(CollectionsMarshal.AsSpan(_sortKeys).Slice(oldSortCount));
    }

    /// <summary>Clears all buffered rows.</summary>
    public void Clear()
    {
        _arguments.Clear();
        _sortKeys.Clear();
    }
}
