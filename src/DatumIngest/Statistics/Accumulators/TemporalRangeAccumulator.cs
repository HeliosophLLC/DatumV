namespace DatumIngest.Statistics.Accumulators;

using DatumIngest.Model;

/// <summary>
/// Accumulates the earliest and latest values for date and datetime columns.
/// </summary>
public sealed class TemporalRangeAccumulator : IStatisticAccumulator
{
    private DateOnly _minDate = DateOnly.MaxValue;
    private DateOnly _maxDate = DateOnly.MinValue;
    private DateTime _minDateTime = DateTime.MaxValue;
    private DateTime _maxDateTime = DateTime.MinValue;
    private long _count;
    private DataKind _observedKind;

    /// <inheritdoc />
    public void Add(DataValue value)
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
            DateTime dateTime = value.AsDateTime();
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
    }

    /// <inheritdoc />
    public void Merge(IStatisticAccumulator other)
    {
        if (other is not TemporalRangeAccumulator otherTemporal || otherTemporal._count == 0)
        {
            return;
        }

        if (_count == 0)
        {
            _count = otherTemporal._count;
            _observedKind = otherTemporal._observedKind;
            _minDate = otherTemporal._minDate;
            _maxDate = otherTemporal._maxDate;
            _minDateTime = otherTemporal._minDateTime;
            _maxDateTime = otherTemporal._maxDateTime;
            return;
        }

        _count += otherTemporal._count;

        if (_minDate > otherTemporal._minDate)
        {
            _minDate = otherTemporal._minDate;
        }

        if (_maxDate < otherTemporal._maxDate)
        {
            _maxDate = otherTemporal._maxDate;
        }

        if (_minDateTime > otherTemporal._minDateTime)
        {
            _minDateTime = otherTemporal._minDateTime;
        }

        if (_maxDateTime < otherTemporal._maxDateTime)
        {
            _maxDateTime = otherTemporal._maxDateTime;
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
