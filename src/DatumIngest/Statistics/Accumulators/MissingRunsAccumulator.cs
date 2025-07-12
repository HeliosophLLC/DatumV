namespace DatumQuery.Statistics.Accumulators;

using DatumQuery.Model;

/// <summary>
/// Counts the number of contiguous runs of null or empty values in a column.
/// A run is one or more consecutive missing values bounded by non-missing values
/// (or the start/end of the data). Supports merge for parallel accumulation.
/// </summary>
public sealed class MissingRunsAccumulator : IStatisticAccumulator
{
    private long _runCount;
    private bool _inRun;
    private bool _startsWithMissing;
    private bool _endsWithMissing;
    private bool _hasValues;

    /// <inheritdoc />
    public void Add(DataValue value)
    {
        bool isMissing = value.IsNull || (value.Kind == DataKind.String && value.AsString().Length == 0);

        if (!_hasValues)
        {
            _hasValues = true;
            _startsWithMissing = isMissing;
        }

        if (isMissing)
        {
            if (!_inRun)
            {
                _inRun = true;
                _runCount++;
            }
        }
        else
        {
            _inRun = false;
        }

        _endsWithMissing = isMissing;
    }

    /// <inheritdoc />
    public void Merge(IStatisticAccumulator other)
    {
        if (other is not MissingRunsAccumulator otherRuns)
        {
            return;
        }

        if (!otherRuns._hasValues)
        {
            return;
        }

        if (!_hasValues)
        {
            _runCount = otherRuns._runCount;
            _inRun = otherRuns._inRun;
            _startsWithMissing = otherRuns._startsWithMissing;
            _endsWithMissing = otherRuns._endsWithMissing;
            _hasValues = true;
            return;
        }

        long combined = _runCount + otherRuns._runCount;

        // If this chunk ends with missing and the next starts with missing,
        // they form one continuous run — subtract the double-count.
        if (_endsWithMissing && otherRuns._startsWithMissing)
        {
            combined--;
        }

        _runCount = combined;
        _endsWithMissing = otherRuns._endsWithMissing;
        _inRun = otherRuns._inRun;
    }

    /// <inheritdoc />
    public StatisticResult GetResult()
    {
        return new StatisticResult("missing_runs", new MissingRunsResult(_runCount));
    }
}

/// <summary>
/// Contains the missing-runs accumulation result.
/// </summary>
/// <param name="RunCount">Number of contiguous runs of null or empty values.</param>
public sealed record MissingRunsResult(long RunCount);
