namespace Heliosoph.DatumV.Statistics.Accumulators;

using Heliosoph.DatumV.Model;

/// <summary>
/// Accumulates the earliest and latest values for date and datetime columns.
/// </summary>
public sealed class TemporalRangeAccumulator : IStatisticAccumulator
{
    private DateOnly _minDate = DateOnly.MaxValue;
    private DateOnly _maxDate = DateOnly.MinValue;
    private DateTimeOffset _minTimestampTz = DateTimeOffset.MaxValue;
    private DateTimeOffset _maxTimestampTz = DateTimeOffset.MinValue;
    private DateTime _minTimestamp = DateTime.MaxValue;
    private DateTime _maxTimestamp = DateTime.MinValue;
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
        else if (value.Kind == DataKind.TimestampTz)
        {
            DateTimeOffset dateTime = value.AsTimestampTz();
            _count++;
            _observedKind = DataKind.TimestampTz;

            if (dateTime < _minTimestampTz)
            {
                _minTimestampTz = dateTime;
            }

            if (dateTime > _maxTimestampTz)
            {
                _maxTimestampTz = dateTime;
            }
        }
        else if (value.Kind == DataKind.Timestamp)
        {
            DateTime ts = value.AsTimestamp();
            _count++;
            _observedKind = DataKind.Timestamp;

            if (ts < _minTimestamp) _minTimestamp = ts;
            if (ts > _maxTimestamp) _maxTimestamp = ts;
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
    public IEnumerable<StatisticResult> GetResults()
    {
        if (_count == 0)
        {
            yield return new StatisticResult("temporal_range", new TemporalRangeResult(null, null));
            yield break;
        }

        if (_observedKind == DataKind.Date)
        {
            yield return new StatisticResult("temporal_range", new TemporalRangeResult(
                _minDate.ToString("O"),
                _maxDate.ToString("O")));
            yield break;
        }

        if (_observedKind == DataKind.Time)
        {
            yield return new StatisticResult("temporal_range", new TemporalRangeResult(
                _minTime.ToString("HH:mm:ss.FFFFFFF"),
                _maxTime.ToString("HH:mm:ss.FFFFFFF")));
            yield break;
        }

        if (_observedKind == DataKind.Timestamp)
        {
            yield return new StatisticResult("temporal_range", new TemporalRangeResult(
                _minTimestamp.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFF"),
                _maxTimestamp.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFF")));
            yield break;
        }

        yield return new StatisticResult("temporal_range", new TemporalRangeResult(
            _minTimestampTz.ToString("O"),
            _maxTimestampTz.ToString("O")));
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
