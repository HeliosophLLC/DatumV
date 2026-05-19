using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Execution.Operators.Ordering;

/// <summary>
/// A buffered row paired with its pre-evaluated ORDER BY sort keys. The keys
/// live in the same arena as the row's stabilised payload, so they share its
/// lifetime — comparators can read them past the input batch's recycle.
/// </summary>
/// <remarks>
/// Used as the unit of work for both bounded (top-N heap) and unbounded
/// (buffer + sort + optional spill) paths in <see cref="OrderByOperator"/>:
/// rows enter as <see cref="KeyedRow"/>s with keys evaluated against the input
/// arena, and the sort comparator reads the keys directly without re-evaluating.
/// </remarks>
internal readonly struct KeyedRow(Row row, DataValue[] keys)
{
    public Row Row { get; } = row;

    public DataValue[] Keys { get; } = keys;
}
