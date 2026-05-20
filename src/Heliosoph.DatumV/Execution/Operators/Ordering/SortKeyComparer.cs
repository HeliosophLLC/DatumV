using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution.Operators.Ordering;

/// <summary>
/// Direction-aware element-by-element comparator over pre-evaluated ORDER BY
/// sort-key arrays. Bundles the <see cref="OrderByItem"/> directions so the
/// per-key sign flip happens once per <see cref="Compare"/> call rather than
/// being open-coded at every sort site.
/// </summary>
/// <remarks>
/// <para>
/// Used by <see cref="OrderByOperator"/>'s top-N heap, the unbounded-path
/// in-memory sort, and the k-way merge heap. The leaf comparator
/// <see cref="CompareDataValues"/> handles nulls-last and dispatches to
/// <see cref="DataValueComparer"/> for cross-tier (inline / arena / sidecar)
/// comparisons.
/// </para>
/// <para>
/// The two arenas and registries are passed separately to support asymmetric
/// comparisons — e.g. a candidate row's keys live in the input batch's arena
/// while the in-heap row's keys live in <see cref="ExecutionContext.Store"/>.
/// </para>
/// </remarks>
internal sealed class SortKeyComparer
{
    private readonly IReadOnlyList<OrderByItem> _items;

    public SortKeyComparer(IReadOnlyList<OrderByItem> orderByItems)
    {
        _items = orderByItems;
    }

    /// <summary>
    /// Compares two pre-evaluated sort-key arrays. First non-equal key
    /// determines the result; all-equal returns 0. Direction is applied
    /// per-key from <see cref="OrderByItem.Direction"/>.
    /// </summary>
    public int Compare(
        DataValue[] left, IValueStore leftStore, SidecarRegistry? leftRegistry,
        DataValue[] right, IValueStore rightStore, SidecarRegistry? rightRegistry)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            int comparison = CompareDataValues(
                left[i], leftStore, leftRegistry,
                right[i], rightStore, rightRegistry);

            if (_items[i].Direction == SortDirection.Descending)
            {
                comparison = -comparison;
            }

            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    /// <summary>
    /// Compares two <see cref="DataValue"/> instances for ordering across all three
    /// storage tiers (inline / arena-backed / sidecar-backed). Nulls sort last.
    /// Exposed as a static so callers that need a single-key comparison without
    /// constructing a full <see cref="SortKeyComparer"/> (e.g. GroupBy's spilled-buffer
    /// sort) can reuse the same null-handling and arena-dispatch logic.
    /// </summary>
    public static int CompareDataValues(
        DataValue left, IValueStore leftStore, SidecarRegistry? leftRegistry,
        DataValue right, IValueStore rightStore, SidecarRegistry? rightRegistry)
    {
        if (left.IsNull && right.IsNull) return 0;
        if (left.IsNull) return 1;
        if (right.IsNull) return -1;

        return DataValueComparer.Compare(
            left, leftStore, leftRegistry,
            right, rightStore, rightRegistry);
    }
}
