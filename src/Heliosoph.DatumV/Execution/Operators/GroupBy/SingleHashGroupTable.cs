using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution.Operators.GroupBy;

/// <summary>
/// Single-key <see cref="IHashGroupTable"/> backed by a
/// <see cref="Dictionary{TKey, TValue}"/> keyed directly on <see cref="DataValue"/>.
/// The single-key fast path avoids the <see cref="CompositeKey"/> wrapper and
/// the per-lookup heap allocation it requires.
/// </summary>
internal sealed class SingleHashGroupTable : IHashGroupTable
{
    private readonly Expression _keyExpression;
    private readonly Dictionary<DataValue, GroupState> _groups = new();
    private DataValue _scratch;

    public SingleHashGroupTable(Expression keyExpression)
    {
        _keyExpression = keyExpression;
    }

    public int KeyCount => 1;
    public int Count => _groups.Count;
    public IEnumerable<GroupState> AllGroups => _groups.Values;

    public async ValueTask EvaluateAsync(ExpressionEvaluator evaluator, Row row, CancellationToken cancellationToken)
    {
        _scratch = await evaluator.EvaluateAsync(_keyExpression, row, cancellationToken).ConfigureAwait(false);
    }

    public GroupState? TryGetExisting()
    {
        _groups.TryGetValue(_scratch, out GroupState? existing);
        return existing;
    }

    public void InsertNew(GroupState group)
    {
        group.KeyValues = [_scratch];
        _groups[_scratch] = group;
    }

    public int HashScratch() => _scratch.GetHashCode();

    public int StabilizeScratchInto(Span<DataValue> dest, Arena from, Arena to)
    {
        dest[0] = DataValueRetention.Stabilize(_scratch, from, to);
        return 1;
    }

    public DataValue[] ReadKeyFromRow(Row spillRow, ref int offset)
    {
        return [spillRow[offset++]];
    }

    public bool Contains(DataValue[] key) => _groups.ContainsKey(key[0]);

    public GroupState? TryGetByKey(DataValue[] key)
    {
        _groups.TryGetValue(key[0], out GroupState? existing);
        return existing;
    }

    public void Insert(DataValue[] key, GroupState group)
    {
        group.KeyValues = key;
        _groups[key[0]] = group;
    }

    public IHashGroupTable CreatePartitionLocal() => new SingleHashGroupTable(_keyExpression);
}
