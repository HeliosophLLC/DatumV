using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Execution.Operators.Pivot;

/// <summary>
/// Resolves the "key" columns of a shape-rotation operator (PIVOT, UNPIVOT) from
/// a representative input row by capturing every column whose name is NOT in the
/// supplied exclusion set.
/// </summary>
/// <remarks>
/// Both PIVOT and UNPIVOT carry their input's non-rotated columns through to
/// the output unchanged. They differ only in WHICH columns get excluded —
/// UNPIVOT excludes the source columns being unpivoted, PIVOT excludes the
/// pivot column plus any aggregate-argument columns. The "scan first row,
/// filter by name" mechanic is identical, so it lives here.
/// </remarks>
internal static class KeyColumnResolver
{
    /// <summary>
    /// Returns the input ordinals and column names of every field in
    /// <paramref name="firstRow"/> whose name is not contained in
    /// <paramref name="excludedColumnNames"/>, preserving input order.
    /// </summary>
    public static (int[] Ordinals, string[] Names) Resolve(
        Row firstRow, HashSet<string> excludedColumnNames)
    {
        List<int> ordinals = new(firstRow.FieldCount);
        List<string> names = new(firstRow.FieldCount);

        for (int i = 0; i < firstRow.FieldCount; i++)
        {
            string columnName = firstRow.ColumnNames[i];
            if (!excludedColumnNames.Contains(columnName))
            {
                ordinals.Add(i);
                names.Add(columnName);
            }
        }

        return (ordinals.ToArray(), names.ToArray());
    }
}
