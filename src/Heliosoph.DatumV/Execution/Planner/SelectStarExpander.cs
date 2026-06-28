using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution.Planner;

/// <summary>
/// Rewrites a bare <c>SELECT *</c> projection into per-table wildcards
/// (<c>a.*, b.*</c>) when the statement has joins, or into a single
/// <c>alias.*</c> when the only source carries an explicit alias.
/// </summary>
/// <remarks>
/// <para>
/// Two rewrites — both keyed off a single <see cref="SelectAllColumns"/>:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// With joins: expand to <c>fromAlias.*, joinAlias.*, …</c> in SQL-text
/// order so a downstream <see cref="Operators.ProjectOperator"/> emits
/// columns in the original FROM/JOIN declaration order even when greedy
/// join reordering has swapped the physical probe/build sides.
/// </description>
/// </item>
/// <item>
/// <description>
/// Single source with an explicit alias: expand to <c>alias.*</c> with
/// <see cref="SelectTableColumns.QualifyOutput"/> = <see langword="false"/>.
/// Without this rewrite the wildcard passthrough leaks the
/// <see cref="Operators.AliasOperator"/>'s qualified physical names into
/// the output, which breaks outer-query references when the result is
/// later re-aliased (e.g. <c>WITH cte AS (SELECT * FROM t a) SELECT c.col
/// FROM cte c</c> would fail because the CTE's output column is
/// <c>a.col</c>).
/// </description>
/// </item>
/// </list>
/// <para>
/// Any wildcard <see cref="SelectAllColumns.ExcludedColumns"/> /
/// <see cref="SelectAllColumns.ReplacedColumns"/> propagates onto every
/// expanded <see cref="SelectTableColumns"/>.
/// </para>
/// </remarks>
internal static class SelectStarExpander
{
    /// <summary>
    /// Returns an expanded projection when <paramref name="projectionColumns"/>
    /// is a single <see cref="SelectAllColumns"/> over a join tree or an
    /// explicitly-aliased single source; otherwise returns
    /// <paramref name="projectionColumns"/> unchanged.
    /// </summary>
    public static IReadOnlyList<SelectColumn> ExpandSelectStar(
        SelectStatement statement, IReadOnlyList<SelectColumn> projectionColumns)
    {
        projectionColumns = RewriteQualifiedStarsOverUnprefixedSources(statement, projectionColumns);

        if (projectionColumns.Count != 1
            || projectionColumns[0] is not SelectAllColumns selectAll)
        {
            return projectionColumns;
        }

        if (statement.Joins is not null)
        {
            List<SelectColumn> expanded = new();

            if (statement.From is not null)
            {
                string? fromAlias = SourceAliases.GetSourceAlias(statement.From.Source);
                if (fromAlias is not null)
                {
                    expanded.Add(new SelectTableColumns(fromAlias,
                        ExcludedColumns: selectAll.ExcludedColumns,
                        ReplacedColumns: selectAll.ReplacedColumns,
                        QualifyOutput: true));
                }
            }

            foreach (JoinClause join in statement.Joins)
            {
                string? joinAlias = SourceAliases.GetSourceAlias(join.Source);
                if (joinAlias is not null)
                {
                    expanded.Add(new SelectTableColumns(joinAlias,
                        ExcludedColumns: selectAll.ExcludedColumns,
                        ReplacedColumns: selectAll.ReplacedColumns,
                        QualifyOutput: true));
                }
            }

            return expanded.Count > 0 ? expanded : projectionColumns;
        }

        if (statement.From is not null
            && SourceAliases.GetExplicitSourceAlias(statement.From.Source) is string explicitAlias)
        {
            return [new SelectTableColumns(
                explicitAlias,
                ExcludedColumns: selectAll.ExcludedColumns,
                ReplacedColumns: selectAll.ReplacedColumns,
                QualifyOutput: false)];
        }

        return projectionColumns;
    }

    /// <summary>
    /// Rewrites user-written <c>SELECT alias.*</c> entries into bare
    /// <see cref="SelectAllColumns"/> when <c>alias</c> refers to a source
    /// whose runtime output carries no <c>alias.</c> prefix — currently a
    /// <see cref="SubquerySource"/>, whose <see cref="Operators.SubqueryOperator"/>
    /// is a pure passthrough.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The runtime <see cref="Operators.ProjectOperator"/> expands
    /// <see cref="SelectTableColumns"/> by string-prefix-matching the input
    /// batch's column names against <c>"alias."</c> (plus a bare-<c>alias</c>
    /// equality fallback). Sources wrapped by
    /// <see cref="Operators.AliasOperator"/> carry that prefix on every
    /// column, so the match works; subquery sources don't, so the same
    /// match silently produces an empty projection. Rewriting at plan time
    /// lets the operator's already-correct
    /// <see cref="SelectAllColumns"/> arm handle the case.
    /// </para>
    /// <para>
    /// Limited to the no-join case: in a join, <c>SELECT t.*</c> must emit
    /// only <c>t</c>'s columns out of a mixed input batch, and a bare
    /// <c>*</c> rewrite would over-project. Join-context subquery
    /// qualified-star expansion needs a different lowering and is not
    /// handled here.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<SelectColumn> RewriteQualifiedStarsOverUnprefixedSources(
        SelectStatement statement, IReadOnlyList<SelectColumn> projectionColumns)
    {
        if (statement.From is null) return projectionColumns;
        if (statement.Joins is not null && statement.Joins.Count > 0) return projectionColumns;
        if (statement.From.Source is not SubquerySource subquery) return projectionColumns;

        List<SelectColumn>? rewritten = null;
        for (int i = 0; i < projectionColumns.Count; i++)
        {
            SelectColumn column = projectionColumns[i];
            if (column is SelectTableColumns qualifiedStar
                && string.Equals(qualifiedStar.TableName, subquery.Alias, StringComparison.OrdinalIgnoreCase))
            {
                rewritten ??= new List<SelectColumn>(projectionColumns);
                rewritten[i] = new SelectAllColumns(
                    ExcludedColumns: qualifiedStar.ExcludedColumns,
                    ReplacedColumns: qualifiedStar.ReplacedColumns);
            }
        }

        return rewritten ?? projectionColumns;
    }
}
