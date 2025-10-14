namespace DatumIngest.Statistics.Accumulators;

using DatumIngest.Model;

/// <summary>
/// Accumulates non-null count and null/empty count for a column.
/// </summary>
public sealed class CountAccumulator : IStatisticAccumulator
{
    private long _nonNullCount;
    private long _nullOrEmptyCount;

    /// <summary>Gets the number of non-null values observed.</summary>
    public long NonNullCount => _nonNullCount;

    /// <summary>Gets the number of null or empty values observed.</summary>
    public long NullOrEmptyCount => _nullOrEmptyCount;

    /// <inheritdoc />
    public void Add(DataValue value, IValueStore store)
    {
        if (value.IsNull)
        {
            _nullOrEmptyCount++;
        }
        else if (value.Kind == DataKind.String && value.AsString(store).Length == 0)
        {
            _nullOrEmptyCount++;
        }
        else
        {
            _nonNullCount++;
        }
    }

    /// <inheritdoc />
    public StatisticResult GetResult()
    {
        return new StatisticResult("count", new CountResult(_nonNullCount, _nullOrEmptyCount));
    }
}

/// <summary>
/// Contains the count accumulation results.
/// </summary>
/// <param name="NonNull">Number of non-null, non-empty values.</param>
/// <param name="NullOrEmpty">Number of null or empty values.</param>
public sealed record CountResult(long NonNull, long NullOrEmpty);
