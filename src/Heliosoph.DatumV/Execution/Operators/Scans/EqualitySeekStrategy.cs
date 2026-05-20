using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Indexing;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Execution.Operators.Scans;

/// <summary>
/// Seeks via single-column equality predicates (<c>column = literal</c>).
/// For each extracted equality, probes the corresponding sorted column index
/// (if any) with the literal coerced to the column's declared kind.
/// </summary>
internal sealed class EqualitySeekStrategy : ISeekStrategy
{
    public void Contribute(
        SeekPlanningContext predicates,
        ITableProvider provider,
        Schema schema,
        SeekPlanner planner,
        Arena arena)
    {
        foreach ((string column, DataValue value) in predicates.Equalities)
        {
            if (!provider.TryGetColumnIndex(column, out IColumnIndex? index))
            {
                continue;
            }

            // The parser narrows numeric literals (sbyte → short → int → long),
            // so `WHERE a = 1` on an Int32 column arrives here with an Int8
            // literal. Without coercion the index probe would see a kind
            // mismatch and return 0 results, incorrectly "winning" the
            // fewest-positions tiebreak.
            DataValue coerced = CoerceLiteralToColumnKind(value, column, schema);
            if (coerced.IsNull) continue;

            planner.SubmitEntries(index.FindExact(coerced));
        }
    }

    /// <summary>
    /// Coerces <paramref name="value"/> to the schema kind of
    /// <paramref name="columnName"/>. Returns a typed null when no coercion
    /// path exists; callers treat that as "skip this strategy" rather than
    /// "matched zero rows".
    /// </summary>
    internal static DataValue CoerceLiteralToColumnKind(
        DataValue value, string columnName, Schema schema)
    {
        ColumnInfo? column = schema.FindColumn(columnName);
        if (column is null) return DataValue.Null(value.Kind);
        if (value.Kind == column.Kind) return value;
        return TypeCoercion.CoerceValue(value, column.Kind);
    }
}
