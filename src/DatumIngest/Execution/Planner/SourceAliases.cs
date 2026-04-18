using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Planner;

/// <summary>
/// Resolves the alias / fallback name introduced by a <see cref="TableSource"/> AST node.
/// Used by <see cref="QueryPlanner"/> to track which aliases are visible on the current
/// side of a join (predicate-pushdown gating) and to expand <c>SELECT *</c> into
/// per-table wildcards.
/// </summary>
internal static class SourceAliases
{
    /// <summary>
    /// Collects the table aliases introduced by <paramref name="source"/> into
    /// <paramref name="aliases"/>. <see cref="TableReference"/> uses its declared
    /// alias or falls back to the table name; <see cref="SubquerySource"/> requires
    /// an alias; <see cref="FunctionSource"/> contributes only when one was written.
    /// </summary>
    public static void CollectSourceAliases(TableSource source, HashSet<string> aliases)
    {
        switch (source)
        {
            case TableReference tableRef:
                aliases.Add(tableRef.Alias ?? tableRef.Name);
                break;
            case SubquerySource subquery:
                aliases.Add(subquery.Alias);
                break;
            case FunctionSource functionSource:
                if (functionSource.Alias is not null)
                {
                    aliases.Add(functionSource.Alias);
                }
                break;
        }
    }

    /// <summary>
    /// Returns the alias (or fallback name) introduced by a table source, used when
    /// expanding <c>SELECT *</c> into per-table wildcards.
    /// </summary>
    public static string? GetSourceAlias(TableSource source)
    {
        return source switch
        {
            TableReference tableRef => tableRef.Alias ?? tableRef.Name,
            SubquerySource subquery => subquery.Alias,
            FunctionSource functionSource => functionSource.Alias,
            _ => null,
        };
    }

    /// <summary>
    /// Returns the source's <em>explicit</em> alias only — differs from
    /// <see cref="GetSourceAlias"/> by not falling back to the table name when no AS
    /// clause was written. Used to detect whether an <c>AliasOperator</c> wraps the
    /// source (and thus whether its physical columns carry a qualified <c>alias.col</c>
    /// prefix). <see cref="FunctionSource"/> without an alias is left unwrapped by the
    /// planner; <see cref="SubquerySource"/> is wrapped by <c>SubqueryOperator</c>
    /// (passthrough, no prefix).
    /// </summary>
    public static string? GetExplicitSourceAlias(TableSource source)
    {
        return source switch
        {
            TableReference tableRef => tableRef.Alias,
            FunctionSource functionSource => functionSource.Alias,
            _ => null,
        };
    }
}
