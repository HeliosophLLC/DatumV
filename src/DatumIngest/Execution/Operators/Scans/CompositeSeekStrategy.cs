using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Indexing;
using DatumIngest.Model;

namespace DatumIngest.Execution.Operators.Scans;

/// <summary>
/// Seeks via composite (multi-column) indexes using leftmost-prefix
/// matching, mirroring PostgreSQL B-tree composite-index semantics.
/// For each composite index, walks the declared columns from the left,
/// collecting equality predicates from the filter until the first
/// uncovered column. A full-tuple match uses <c>FindExact</c>; a partial
/// prefix uses <c>FindPrefix</c> (range scan over the byte-encoded prefix).
/// </summary>
/// <remarks>
/// <para>
/// A gap in coverage (e.g. equality predicates on columns 0 and 2 of a
/// 3-column index) yields a prefix of length 1, not a 3-tuple with a hole.
/// </para>
/// <para>
/// Each prefix value is coerced to the column's declared schema kind
/// before encoding so the probe key matches the encoded entry key the
/// index built at INSERT time. The coercion is the same one
/// <see cref="EqualitySeekStrategy"/> uses; a non-parseable coercion
/// (e.g. a string into an Int32) is treated as "skip this column" rather
/// than "match zero rows".
/// </para>
/// </remarks>
internal sealed class CompositeSeekStrategy : ISeekStrategy
{
    public void Contribute(
        SeekPlanningContext predicates,
        ITableProvider provider,
        Schema schema,
        SeekPlanner planner,
        Arena arena)
    {
        IReadOnlyList<ICompositeIndex> compositeIndexes = provider.GetCompositeIndexes();
        if (compositeIndexes.Count == 0 || predicates.Equalities.Count == 0)
        {
            return;
        }

        // Build a case-insensitive lookup of equality column → value once.
        Dictionary<string, DataValue> equalityByColumn = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string column, DataValue value) in predicates.Equalities)
        {
            equalityByColumn[column] = value;
        }

        foreach (ICompositeIndex compositeIndex in compositeIndexes)
        {
            // Walk this index's columns from the left, collecting coerced
            // values for each one covered by an equality predicate. Stop at
            // the first uncovered column — leftmost-prefix semantics.
            DataValue[] fullTuple = new DataValue[compositeIndex.Columns.Count];
            int prefixLen = 0;
            for (int i = 0; i < compositeIndex.Columns.Count; i++)
            {
                string indexedCol = compositeIndex.Columns[i];
                if (!equalityByColumn.TryGetValue(indexedCol, out DataValue v))
                {
                    break;
                }

                // Coerce to the schema kind so the encoded probe key matches
                // the encoded entry key. FindColumn is case-insensitive;
                // null means the index references a column that no longer
                // exists (schema drift). ALTER DROP COLUMN cascades — defensive
                // skip just in case.
                ColumnInfo? column = schema.FindColumn(indexedCol);
                if (column is null) break;

                DataValue coerced = v.Kind == column.Kind
                    ? v
                    : TypeCoercion.CoerceValue(v, column.Kind);

                // Coercion can produce a null when no path exists (e.g. a
                // non-parseable string into an int). The probe would never
                // match — stop accumulating prefix.
                if (coerced.IsNull) break;
                fullTuple[i] = coerced;
                prefixLen++;
            }

            if (prefixLen == 0) continue;

            IReadOnlyList<ValueIndexEntry> entries;
            if (prefixLen == compositeIndex.Columns.Count)
            {
                // Full-tuple match → cheaper point lookup.
                entries = compositeIndex.FindExact(fullTuple);
            }
            else
            {
                // Leftmost-prefix match → range scan over byte-encoded prefix.
                DataValue[] prefixTuple = new DataValue[prefixLen];
                Array.Copy(fullTuple, prefixTuple, prefixLen);
                entries = compositeIndex.FindPrefix(prefixTuple);
            }

            planner.SubmitCompositeEntries(entries);
        }
    }
}
