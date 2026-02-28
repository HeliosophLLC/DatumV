using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Operators.Joins;

/// <summary>
/// Single-key <see cref="IJoinHashTable"/> backed by a
/// <see cref="Dictionary{TKey, TValue}"/> keyed directly on
/// <see cref="DataValue"/>. The single-key fast path avoids the
/// <see cref="CompositeKey"/> wrapper and the per-probe heap allocation it
/// would otherwise require.
/// </summary>
internal sealed class SingleJoinHashTable : IJoinHashTable
{
    private readonly Expression _buildKeyExpression;
    private readonly Expression _probeKeyExpression;
    private readonly Dictionary<DataValue, List<(int Index, Row Row)>> _buckets = new();
    private readonly Expression[] _buildKeys;
    private readonly Expression[] _probeKeys;

    public SingleJoinHashTable(Expression buildKey, Expression probeKey)
    {
        _buildKeyExpression = buildKey;
        _probeKeyExpression = probeKey;
        _buildKeys = [buildKey];
        _probeKeys = [probeKey];
    }

    public int Count => _buckets.Count;
    public int KeyCount => 1;
    public IReadOnlyList<Expression> BuildKeyExpressions => _buildKeys;
    public IReadOnlyList<Expression> ProbeKeyExpressions => _probeKeys;

    public async ValueTask<bool> TryEvaluateAndInsertAsync(
        ExpressionEvaluator evaluator,
        Row buildRow,
        int buildIndex,
        CancellationToken cancellationToken)
    {
        DataValue keyValue = await evaluator.EvaluateAsync(_buildKeyExpression, buildRow, cancellationToken).ConfigureAwait(false);
        if (keyValue.IsNull) return false;

        if (!_buckets.TryGetValue(keyValue, out List<(int, Row)>? bucket))
        {
            bucket = new List<(int, Row)>();
            _buckets[keyValue] = bucket;
        }
        bucket.Add((buildIndex, buildRow));
        return true;
    }

    public async ValueTask<JoinHashProbeResult> ProbeAsync(
        ExpressionEvaluator evaluator,
        Row probeRow,
        DataValue[]? probeKeyScratch,
        CancellationToken cancellationToken)
    {
        DataValue keyValue = await evaluator.EvaluateAsync(_probeKeyExpression, probeRow, cancellationToken).ConfigureAwait(false);
        if (keyValue.IsNull)
        {
            return new JoinHashProbeResult(KeyIsNull: true, Matches: null);
        }

        _buckets.TryGetValue(keyValue, out List<(int Index, Row Row)>? matches);
        return new JoinHashProbeResult(KeyIsNull: false, Matches: matches);
    }

    public void CollectDistinctKeysAt(int keyIndex, HashSet<DataValue> destination)
    {
        // Single-key table has exactly one column; null keys are excluded at insert time.
        foreach (DataValue key in _buckets.Keys)
        {
            destination.Add(key);
        }
    }
}
