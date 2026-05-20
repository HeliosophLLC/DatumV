using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution.Operators.GroupBy;

/// <summary>
/// Composite-key <see cref="IHashGroupTable"/> backed by a
/// <see cref="Dictionary{TKey, TValue}"/> keyed on <see cref="CompositeKey"/>
/// with <see cref="CompositeKeyComparer.Instance"/>. The scratch buffer is a
/// reusable <see cref="DataValue"/>[] that the operator owns — only inserts
/// allocate a permanent <see cref="DataValue"/>[] for the key.
/// </summary>
internal sealed class CompositeHashGroupTable : IHashGroupTable
{
    private readonly IReadOnlyList<Expression> _keyExpressions;
    private readonly int _keyCount;
    private readonly Dictionary<CompositeKey, GroupState> _groups;
    private readonly Dictionary<CompositeKey, GroupState>.AlternateLookup<ReadOnlySpan<DataValue>> _spanLookup;
    private readonly DataValue[] _scratch;

    public CompositeHashGroupTable(IReadOnlyList<Expression> keyExpressions)
    {
        _keyExpressions = keyExpressions;
        _keyCount = keyExpressions.Count;
        _groups = new Dictionary<CompositeKey, GroupState>(CompositeKeyComparer.Instance);
        _spanLookup = _groups.GetAlternateLookup<ReadOnlySpan<DataValue>>();
        _scratch = new DataValue[_keyCount];
    }

    public int KeyCount => _keyCount;
    public int Count => _groups.Count;
    public IEnumerable<GroupState> AllGroups => _groups.Values;

    public async ValueTask EvaluateAsync(ExpressionEvaluator evaluator, Row row, CancellationToken cancellationToken)
    {
        for (int index = 0; index < _keyCount; index++)
        {
            _scratch[index] = await evaluator.EvaluateAsync(_keyExpressions[index], row, cancellationToken).ConfigureAwait(false);
        }
    }

    public GroupState? TryGetExisting()
    {
        _spanLookup.TryGetValue(_scratch.AsSpan(0, _keyCount), out GroupState? existing);
        return existing;
    }

    public void InsertNew(GroupState group)
    {
        DataValue[] permanent = _scratch.AsSpan(0, _keyCount).ToArray();
        group.KeyValues = permanent;
        _groups[new CompositeKey(permanent)] = group;
    }

    public int HashScratch() => CompositeKeyHashMap<GroupState>.ComputeHash(_scratch.AsSpan(0, _keyCount));

    public int StabilizeScratchInto(Span<DataValue> dest, Arena from, Arena to)
    {
        for (int index = 0; index < _keyCount; index++)
        {
            dest[index] = DataValueRetention.Stabilize(_scratch[index], from, to);
        }
        return _keyCount;
    }

    public DataValue[] ReadKeyFromRow(Row spillRow, ref int offset)
    {
        DataValue[] key = new DataValue[_keyCount];
        for (int index = 0; index < _keyCount; index++)
        {
            key[index] = spillRow[offset++];
        }
        return key;
    }

    public bool Contains(DataValue[] key) => _spanLookup.ContainsKey(key.AsSpan());

    public GroupState? TryGetByKey(DataValue[] key)
    {
        _spanLookup.TryGetValue(key.AsSpan(), out GroupState? existing);
        return existing;
    }

    public void Insert(DataValue[] key, GroupState group)
    {
        group.KeyValues = key;
        _groups[new CompositeKey(key)] = group;
    }

    public IHashGroupTable CreatePartitionLocal() => new CompositeHashGroupTable(_keyExpressions);
}
