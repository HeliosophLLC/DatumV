using DatumIngest.Parsing.Ast;

namespace DatumIngest.Model;

/// <summary>
/// Derives output column names for SELECT expressions and resolves collisions
/// by appending positional suffixes (<c>_1</c>, <c>_2</c>, …) when two or more
/// columns would otherwise share the same auto-generated name.
/// Explicit <c>AS</c> aliases are never modified.
/// </summary>
public static class ColumnNameResolver
{
    /// <summary>
    /// Derives a raw (pre-deduplication) column name from an expression.
    /// Returns the column name for simple references, the function name for
    /// function calls, or <c>"expression"</c> for anything else.
    /// </summary>
    public static string GetRawName(Expression expression)
    {
        return expression switch
        {
            ColumnReference columnReference => columnReference.ColumnName,
            FunctionCallExpression functionCall => functionCall.FunctionName,
            StructLiteralExpression => "struct",
            _ => "expression",
        };
    }

    /// <summary>
    /// Scans the name array for duplicates and appends <c>_1</c>, <c>_2</c>, …
    /// to every occurrence of a name that appears more than once.
    /// Names at positions listed in <paramref name="aliasedPositions"/> are
    /// treated as explicit aliases and are never renamed.
    /// The array is modified in place.
    /// </summary>
    /// <param name="names">The column name array to deduplicate.</param>
    /// <param name="aliasedPositions">
    /// Indices of columns that have an explicit <c>AS</c> alias. These are
    /// excluded from collision detection and renaming.
    /// </param>
    public static void DeduplicateNames(string[] names, HashSet<int>? aliasedPositions = null)
    {
        // First pass: count occurrences of each auto-generated name.
        Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < names.Length; index++)
        {
            if (aliasedPositions is not null && aliasedPositions.Contains(index))
            {
                continue;
            }

            string name = names[index];
            counts[name] = counts.TryGetValue(name, out int existing) ? existing + 1 : 1;
        }

        // Second pass: rename only the names that appear more than once.
        Dictionary<string, int> counters = new(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < names.Length; index++)
        {
            if (aliasedPositions is not null && aliasedPositions.Contains(index))
            {
                continue;
            }

            string name = names[index];

            if (counts.TryGetValue(name, out int total) && total > 1)
            {
                int ordinal = counters.TryGetValue(name, out int current) ? current + 1 : 1;
                counters[name] = ordinal;
                names[index] = $"{name}_{ordinal}";
            }
        }
    }
}
