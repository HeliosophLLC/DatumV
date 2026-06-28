using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution.Planner;

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
    /// expanding <c>SELECT *</c> into per-table wildcards. An unaliased
    /// <see cref="FunctionSource"/> falls back to its function name so it shows up
    /// in wildcard expansion — matching PostgreSQL semantics
    /// (<c>SELECT * FROM t, unnest(t.col)</c> includes the unnest column) and
    /// mirroring the fallback <c>SourcePlanner.PlanFunctionSource</c> uses when
    /// wrapping the source with an <see cref="Operators.AliasOperator"/>.
    /// </summary>
    public static string? GetSourceAlias(TableSource source)
    {
        return source switch
        {
            TableReference tableRef => tableRef.Alias ?? tableRef.Name,
            SubquerySource subquery => subquery.Alias,
            FunctionSource functionSource => functionSource.Alias ?? functionSource.FunctionName,
            _ => null,
        };
    }

    /// <summary>
    /// Returns the source's <em>explicit</em> alias only — differs from
    /// <see cref="GetSourceAlias"/> by not falling back to the table name when no AS
    /// clause was written. Used to detect whether an <c>AliasOperator</c> wraps the
    /// source (and thus whether its physical columns carry a qualified <c>alias.col</c>
    /// prefix). <see cref="FunctionSource"/> without an alias is left unwrapped by the
    /// planner. <see cref="SubquerySource"/> stays unwrapped in the no-join case
    /// (a bare <c>SubqueryOperator</c> passthrough) and is wrapped with an
    /// <c>AliasOperator</c> in join contexts — returning <see langword="null"/> here
    /// keeps the no-join behaviour the rest of the planner expects; join-context
    /// wildcard expansion goes through <see cref="GetSourceAlias"/> instead.
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
