namespace DatumIngest.Statistics.Accumulators;

using DatumIngest.Model;

/// <summary>
/// Accumulates the earliest and latest values for date and datetime columns.
/// </summary>
public sealed class TemporalRangeAccumulator : IStatisticAccumulator
{
    private DateOnly _minDate = DateOnly.MaxValue;
    private DateOnly _maxDate = DateOnly.MinValue;
    private DateTimeOffset _minDateTime = DateTimeOffset.MaxValue;
    private DateTimeOffset _maxDateTime = DateTimeOffset.MinValue;
    private TimeOnly _minTime = TimeOnly.MaxValue;
    private TimeOnly _maxTime = TimeOnly.MinValue;
    private long _count;
    private DataKind _observedKind;

    /// <inheritdoc />
    public void Add(DataValue value, IValueStore store)
    {
        if (value.IsNull)
        {
            return;
        }

        if (value.Kind == DataKind.Date)
        {
            DateOnly date = value.AsDate();
            _count++;
            _observedKind = DataKind.Date;

            if (date < _minDate)
            {
                _minDate = date;
            }

            if (date > _maxDate)
            {
                _maxDate = date;
            }
        }
        else if (value.Kind == DataKind.DateTime)
        {
            DateTimeOffset dateTime = value.AsDateTime();
            _count++;
            _observedKind = DataKind.DateTime;

            if (dateTime < _minDateTime)
            {
                _minDateTime = dateTime;
            }

            if (dateTime > _maxDateTime)
            {
                _maxDateTime = dateTime;
            }
        }
        else if (value.Kind == DataKind.Time)
        {
            TimeOnly time = value.AsTime();
            _count++;
            _observedKind = DataKind.Time;

            if (time < _minTime)
            {
                _minTime = time;
            }

            if (time > _maxTime)
            {
                _maxTime = time;
            }
        }
    }

    /// <inheritdoc />
    public StatisticResult GetResult()
    {
        if (_count == 0)
        {
            return new StatisticResult("temporal_range", new TemporalRangeResult(null, null));
        }

        if (_observedKind == DataKind.Date)
        {
            return new StatisticResult("temporal_range", new TemporalRangeResult(
                _minDate.ToString("O"),
                _maxDate.ToString("O")));
        }

        if (_observedKind == DataKind.Time)
        {
            return new StatisticResult("temporal_range", new TemporalRangeResult(
                _minTime.ToString("HH:mm:ss.FFFFFFF"),
                _maxTime.ToString("HH:mm:ss.FFFFFFF")));
        }

        return new StatisticResult("temporal_range", new TemporalRangeResult(
            _minDateTime.ToString("O"),
            _maxDateTime.ToString("O")));
    }
}

/// <summary>
/// Contains temporal range results.
/// </summary>
/// <param name="Earliest">ISO 8601 string of the earliest value, or null if no values observed.</param>
/// <param name="Latest">ISO 8601 string of the latest value, or null if no values observed.</param>
public sealed record TemporalRangeResult(string? Earliest, string? Latest)
{
    /// <summary>An empty result with no earliest or latest value.</summary>
    public static TemporalRangeResult Empty { get; } = new(null, null);
}
