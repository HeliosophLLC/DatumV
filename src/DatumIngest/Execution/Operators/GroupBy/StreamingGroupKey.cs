using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Operators.GroupBy;

/// <summary>
/// Encapsulates GROUP BY key evaluation and key-change detection for the
/// streaming-sorted aggregation path. Hides the single-key / composite-key
/// branching: callers see one <see cref="EvaluateAsync"/>, one
/// <see cref="ScratchDiffersFromCurrent"/>, one <see cref="CaptureCurrent"/>.
/// </summary>
/// <remarks>
/// One instance per pipeline. Holds a reusable scratch buffer (a single
/// <see cref="DataValue"/> for the one-key fast path, or a
/// <see cref="DataValue"/>[] for composite keys) and the captured key of the
/// group currently being accumulated.
/// </remarks>
internal sealed class StreamingGroupKey
{
    private readonly IReadOnlyList<Expression> _groupByExpressions;
    private readonly bool _useSingleKey;
    private readonly int _keyCount;

    private DataValue _singleKeyScratch;
    private DataValue _currentSingleKey;
    private readonly DataValue[]? _compositeScratch;
    private DataValue[]? _currentCompositeKey;

    public StreamingGroupKey(IReadOnlyList<Expression> groupByExpressions)
    {
        _groupByExpressions = groupByExpressions;
        _keyCount = groupByExpressions.Count;
        _useSingleKey = _keyCount == 1;
        _compositeScratch = _useSingleKey ? null : new DataValue[_keyCount];
    }

    /// <summary>
    /// Evaluates each GROUP BY expression for the row into the internal scratch
    /// buffer. Does not change the captured "current" key.
    /// </summary>
    public async ValueTask EvaluateAsync(
        ExpressionEvaluator evaluator,
        Row row,
        CancellationToken cancellationToken)
    {
        if (_useSingleKey)
        {
            _singleKeyScratch = await evaluator.EvaluateAsync(
                _groupByExpressions[0], row, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            for (int index = 0; index < _keyCount; index++)
            {
                _compositeScratch![index] = await evaluator.EvaluateAsync(
                    _groupByExpressions[index], row, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// True when the freshly-evaluated scratch buffer does not match the most
    /// recently captured key — i.e. the input row starts a new group.
    /// Callers must only invoke this when a current group exists (i.e. after
    /// at least one <see cref="CaptureCurrent"/> call); the result is undefined
    /// before the first capture.
    /// </summary>
    public bool ScratchDiffersFromCurrent()
    {
        if (_useSingleKey)
        {
            return !_singleKeyScratch.Equals(_currentSingleKey);
        }

        for (int index = 0; index < _keyCount; index++)
        {
            if (!_currentCompositeKey![index].Equals(_compositeScratch![index]))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Captures the current scratch as the new "current" key and returns a
    /// fresh permanent <see cref="DataValue"/>[] suitable for
    /// <see cref="GroupState.KeyValues"/>. For composite keys, this is the
    /// only path that copies scratch into long-lived storage — happens once
    /// per group boundary, not per row.
    /// </summary>
    public DataValue[] CaptureCurrent()
    {
        if (_useSingleKey)
        {
            _currentSingleKey = _singleKeyScratch;
            return [_singleKeyScratch];
        }

        DataValue[] permanent = _compositeScratch!.AsSpan(0, _keyCount).ToArray();
        _currentCompositeKey = permanent;
        return permanent;
    }
}
