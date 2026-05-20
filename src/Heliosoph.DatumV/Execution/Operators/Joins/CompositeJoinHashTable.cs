using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution.Operators.Joins;

/// <summary>
/// Composite-key <see cref="IJoinHashTable"/> backed by a
/// <see cref="Dictionary{TKey, TValue}"/> keyed on <see cref="CompositeKey"/>
/// with <see cref="CompositeKeyComparer.Instance"/>. Probes use the
/// <c>GetAlternateLookup&lt;ReadOnlySpan&lt;DataValue&gt;&gt;</c> so the
/// caller's scratch buffer probes the dictionary without allocating a fresh
/// <see cref="DataValue"/>[] per probe.
/// </summary>
internal sealed class CompositeJoinHashTable : IJoinHashTable
{
    private readonly IReadOnlyList<(Expression Left, Expression Right)> _keyPairs;
    private readonly bool _buildKeyIsRight;
    private readonly int _keyCount;
    private readonly Dictionary<CompositeKey, List<(int Index, Row Row)>> _buckets;
    private readonly Dictionary<CompositeKey, List<(int Index, Row Row)>>.AlternateLookup<ReadOnlySpan<DataValue>> _spanLookup;
    private readonly Expression[] _buildKeys;
    private readonly Expression[] _probeKeys;
    private readonly DataValue[] _buildScratch;

    public CompositeJoinHashTable(
        IReadOnlyList<(Expression Left, Expression Right)> keyPairs,
        bool buildKeyIsRight)
    {
        _keyPairs = keyPairs;
        _buildKeyIsRight = buildKeyIsRight;
        _keyCount = keyPairs.Count;
        _buckets = new Dictionary<CompositeKey, List<(int Index, Row Row)>>(CompositeKeyComparer.Instance);
        _spanLookup = _buckets.GetAlternateLookup<ReadOnlySpan<DataValue>>();
        _buildScratch = new DataValue[_keyCount];

        _buildKeys = new Expression[_keyCount];
        _probeKeys = new Expression[_keyCount];
        for (int i = 0; i < _keyCount; i++)
        {
            _buildKeys[i] = buildKeyIsRight ? keyPairs[i].Right : keyPairs[i].Left;
            _probeKeys[i] = buildKeyIsRight ? keyPairs[i].Left : keyPairs[i].Right;
        }
    }

    public int Count => _buckets.Count;
    public int KeyCount => _keyCount;
    public IReadOnlyList<Expression> BuildKeyExpressions => _buildKeys;
    public IReadOnlyList<Expression> ProbeKeyExpressions => _probeKeys;

    public async ValueTask<bool> TryEvaluateAndInsertAsync(
        ExpressionEvaluator evaluator,
        Row buildRow,
        int buildIndex,
        CancellationToken cancellationToken)
    {
        for (int i = 0; i < _keyCount; i++)
        {
            DataValue part = await evaluator.EvaluateAsync(_buildKeys[i], buildRow, cancellationToken).ConfigureAwait(false);
            if (part.IsNull) return false;
            _buildScratch[i] = part;
        }

        DataValue[] permanentKey = _buildScratch.AsSpan(0, _keyCount).ToArray();
        CompositeKey compositeKey = new(permanentKey);
        if (!_buckets.TryGetValue(compositeKey, out List<(int Index, Row Row)>? bucket))
        {
            bucket = new List<(int Index, Row Row)>();
            _buckets[compositeKey] = bucket;
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
        DataValue[] scratch = probeKeyScratch
            ?? throw new ArgumentNullException(nameof(probeKeyScratch),
                "Composite-key probe requires a caller-owned scratch buffer.");

        for (int i = 0; i < _keyCount; i++)
        {
            DataValue part = await evaluator.EvaluateAsync(_probeKeys[i], probeRow, cancellationToken).ConfigureAwait(false);
            if (part.IsNull)
            {
                return new JoinHashProbeResult(KeyIsNull: true, Matches: null);
            }
            scratch[i] = part;
        }

        _spanLookup.TryGetValue(scratch.AsSpan(0, _keyCount), out List<(int Index, Row Row)>? matches);
        return new JoinHashProbeResult(KeyIsNull: false, Matches: matches);
    }

    public void CollectDistinctKeysAt(int keyIndex, HashSet<DataValue> destination)
    {
        // Null components are excluded at insert time, so every stored key has non-null parts.
        foreach (CompositeKey compositeKey in _buckets.Keys)
        {
            destination.Add(compositeKey[keyIndex]);
        }
    }
}
