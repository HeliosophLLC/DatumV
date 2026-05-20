using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;
using Heliosoph.DatumV.Pooling;

namespace Heliosoph.DatumV.Execution.Operators.Ordering;

/// <summary>
/// Pre-evaluates an ORDER BY's <see cref="OrderByItem.Expression"/> list against
/// a row, materialising the resulting <see cref="DataValue"/>s into a target
/// arena. Bundles the items + <see cref="ExpressionEvaluator"/> so individual
/// sort-key insertion sites collapse to a single call.
/// </summary>
/// <remarks>
/// <para>
/// Sort-key arrays live alongside the row's stabilised payload so the comparator
/// (<see cref="SortKeyComparer"/>) can read them past the input batch's recycle.
/// The asymmetric overload supports the top-N candidate path, where the
/// candidate row's payload is still in the input batch's arena but the resulting
/// keys must materialise into <see cref="ExecutionContext.Store"/> for storage
/// in the heap.
/// </para>
/// </remarks>
internal sealed class SortKeyEvaluator
{
    private readonly IReadOnlyList<OrderByItem> _items;
    private readonly ExpressionEvaluator _evaluator;

    public SortKeyEvaluator(IReadOnlyList<OrderByItem> orderByItems, ExpressionEvaluator evaluator)
    {
        _items = orderByItems;
        _evaluator = evaluator;
    }

    /// <summary>
    /// Pre-evaluates each ORDER BY item against <paramref name="row"/>. Results
    /// live in <paramref name="arena"/>.
    /// </summary>
    public ValueTask<DataValue[]> EvaluateAsync(Row row, Arena arena, CancellationToken cancellationToken)
        => EvaluateAsync(row, arena, arena, cancellationToken);

    /// <summary>
    /// Asymmetric variant: <paramref name="row"/>'s values are read against
    /// <paramref name="sourceArena"/>; resulting keys are materialised into
    /// <paramref name="targetArena"/>.
    /// </summary>
    public async ValueTask<DataValue[]> EvaluateAsync(
        Row row,
        Arena sourceArena,
        Arena targetArena,
        CancellationToken cancellationToken)
    {
        DataValue[] keys = new DataValue[_items.Count];
        EvaluationFrame frame = new(
            row, sourceArena, targetArena, _evaluator.Context, outerRow: null);
        for (int i = 0; i < _items.Count; i++)
        {
            keys[i] = await _evaluator
                .EvaluateAsync(_items[i].Expression, frame, cancellationToken)
                .ConfigureAwait(false);
        }
        return keys;
    }
}
