using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Planner;

/// <summary>
/// Builds the set of column references a statement touches across every clause and
/// projects that set down to the per-alias subset needed at scan time. The planner
/// then ships the per-alias <c>requiredColumns</c> hint to each
/// <see cref="DatumIngest.Catalog.ITableProvider"/> so it can skip materialising
/// columns the query never reads.
/// </summary>
internal static class ProjectionPushdown
{
    /// <summary>
    /// Collects all column references from every clause of <paramref name="statement"/>:
    /// SELECT, LET binding bodies, WHERE, JOIN ON, ORDER BY, GROUP BY, HAVING, and
    /// CROSS VALIDATE key/stratify/group columns. LATERAL subqueries recurse to surface
    /// outer-scope correlations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A wildcard in SELECT (<see cref="SelectAllColumns"/> or
    /// <see cref="SelectTableColumns"/>) short-circuits to an empty set, which the
    /// rest of the planner treats as "no restriction — all columns needed".
    /// </para>
    /// <para>
    /// LET-introduced names (binding name, output alias, destructure names) appear as
    /// <see cref="ColumnReference"/> nodes indistinguishable from real columns. We
    /// build a side-set of LET names during the binding walk and strip unqualified
    /// references matching them before returning — providers expect the result to be
    /// a subset of the table schema.
    /// </para>
    /// </remarks>
    public static HashSet<(string? TableName, string ColumnName)> CollectAllReferencedColumns(
        SelectStatement statement)
    {
        HashSet<(string? TableName, string ColumnName)> references = new();

        // Tracks LET-introduced names — built lazily during the LET binding ref-collection
        // pass below so statement.LetBindings is walked once.
        HashSet<string>? letNames = null;

        // SELECT * / SELECT table.* → empty set = "all columns needed".
        foreach (SelectColumn column in statement.Columns)
        {
            if (column is SelectAllColumns or SelectTableColumns)
            {
                return references;
            }
        }

        // SELECT columns.
        foreach (SelectColumn column in statement.Columns)
        {
            foreach ((string? tableName, string columnName) in
                ColumnReferenceCollector.Collect(column.Expression))
            {
                references.Add((tableName, columnName));
            }
        }

        // LET binding expressions. Same loop builds the LET-name set used by the final
        // filter pass below, so statement.LetBindings is walked once.
        if (statement.LetBindings is not null)
        {
            letNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (LetBinding binding in statement.LetBindings)
            {
                letNames.Add(binding.Name);
                if (binding.OutputAlias is not null)
                {
                    letNames.Add(binding.OutputAlias);
                }
                if (binding.Destructure is not null)
                {
                    foreach (string name in binding.Destructure.Names)
                    {
                        letNames.Add(name);
                    }
                }

                foreach ((string? tableName, string columnName) in
                    ColumnReferenceCollector.Collect(binding.Expression))
                {
                    references.Add((tableName, columnName));
                }
            }
        }

        // WHERE predicate.
        if (statement.Where is not null)
        {
            foreach ((string? tableName, string columnName) in
                ColumnReferenceCollector.Collect(statement.Where))
            {
                references.Add((tableName, columnName));
            }
        }

        // JOIN ON conditions.
        if (statement.Joins is not null)
        {
            foreach (JoinClause join in statement.Joins)
            {
                if (join.OnCondition is not null)
                {
                    foreach ((string? tableName, string columnName) in
                        ColumnReferenceCollector.Collect(join.OnCondition))
                    {
                        references.Add((tableName, columnName));
                    }
                }

                // LATERAL right-side subqueries can reference outer-scope columns
                // (correlation). Recurse to surface those refs in the parent's
                // required-columns set — otherwise projection pushdown trims columns
                // the lateral body needs at execution time. ComputeRequiredColumns
                // filters by alias, so inner-scope refs drop out naturally for outer
                // tables.
                if (join.IsLateral && join.Source is SubquerySource lateralSubquery)
                {
                    foreach ((string? tableName, string columnName) in
                        CollectAllReferencedColumns(lateralSubquery.Query))
                    {
                        references.Add((tableName, columnName));
                    }
                }
            }
        }

        // ORDER BY expressions.
        if (statement.OrderBy is not null)
        {
            foreach (OrderByItem item in statement.OrderBy.Items)
            {
                foreach ((string? tableName, string columnName) in
                    ColumnReferenceCollector.Collect(item.Expression))
                {
                    references.Add((tableName, columnName));
                }
            }
        }

        // GROUP BY expressions.
        if (statement.GroupBy is not null)
        {
            foreach (Expression groupExpression in statement.GroupBy.Expressions)
            {
                foreach ((string? tableName, string columnName) in
                    ColumnReferenceCollector.Collect(groupExpression))
                {
                    references.Add((tableName, columnName));
                }
            }
        }

        // HAVING predicate.
        if (statement.Having is not null)
        {
            foreach ((string? tableName, string columnName) in
                ColumnReferenceCollector.Collect(statement.Having))
            {
                references.Add((tableName, columnName));
            }
        }

        // CROSS VALIDATE key columns (ON, STRATIFY BY, GROUP BY).
        if (statement.CrossValidate is not null)
        {
            foreach (Expression keyExpr in statement.CrossValidate.KeyColumns)
            {
                foreach ((string? tableName, string columnName) in
                    ColumnReferenceCollector.Collect(keyExpr))
                {
                    references.Add((tableName, columnName));
                }
            }

            if (statement.CrossValidate.StratifyColumns is not null)
            {
                foreach (Expression stratifyExpr in statement.CrossValidate.StratifyColumns)
                {
                    foreach ((string? tableName, string columnName) in
                        ColumnReferenceCollector.Collect(stratifyExpr))
                    {
                        references.Add((tableName, columnName));
                    }
                }
            }

            if (statement.CrossValidate.GroupColumns is not null)
            {
                foreach (Expression groupExpr in statement.CrossValidate.GroupColumns)
                {
                    foreach ((string? tableName, string columnName) in
                        ColumnReferenceCollector.Collect(groupExpr))
                    {
                        references.Add((tableName, columnName));
                    }
                }
            }
        }

        // Strip LET-introduced names from the reference set. They look like
        // ColumnReference nodes but the contract for `requiredColumns` shipped to
        // providers is "subset of the table schema". Only filter unqualified refs —
        // a qualified `t.x` is always a real table column (LET names never carry a
        // table qualifier).
        if (letNames is not null)
        {
            references.RemoveWhere(r => r.TableName is null && letNames.Contains(r.ColumnName));
        }

        return references;
    }

    /// <summary>
    /// Computes the set of required column names for a specific table
    /// <paramref name="alias"/> from the globally referenced columns. Returns
    /// <see langword="null"/> when all columns are needed (SELECT * or no column
    /// analysis available).
    /// </summary>
    public static IReadOnlySet<string>? ComputeRequiredColumns(
        string? alias,
        HashSet<(string? TableName, string ColumnName)> allReferencedColumns,
        Schema? schema = null)
    {
        // Empty set means SELECT * — all columns needed.
        if (allReferencedColumns.Count == 0)
        {
            return null;
        }

        HashSet<string> required = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string? tableName, string columnName) in allReferencedColumns)
        {
            if (tableName is null)
            {
                // Unqualified reference — could come from any table OR be a post-projection
                // alias from a planner rewrite (e.g. __group_key_N_M minted by
                // SubqueryRewriter for correlated scalar/aggregate subqueries) OR a bad
                // identifier that downstream resolution will flag. When a schema is
                // supplied, filter to names that actually exist on this scan; only real
                // columns get pushed down. Synthetic / unresolved names drop out here
                // and get handled by their natural resolver (the join's combined row,
                // the evaluator's "column not found" path, etc.). Without a schema we
                // fall back to the original conservative "include and let the provider
                // sort it out" behaviour so unrelated callers don't regress.
                if (schema is null || schema.FindColumn(columnName) is not null)
                {
                    required.Add(columnName);
                }
            }
            else if (alias is not null
                && string.Equals(tableName, alias, StringComparison.OrdinalIgnoreCase))
            {
                required.Add(columnName);
            }
        }

        // If no columns matched this alias, the query may reference columns without
        // qualification. Return null (all columns) to be safe.
        return required.Count > 0 ? required : null;
    }

    /// <summary>
    /// Returns <see langword="true"/> if any expression in <paramref name="expressions"/>
    /// is an unqualified <see cref="ColumnReference"/> matching
    /// <paramref name="columnName"/> (case-insensitive). Used by the CROSS VALIDATE
    /// planning path to detect whether GROUP BY already names the fold-output column.
    /// </summary>
    public static bool HasColumnReference(IReadOnlyList<Expression> expressions, string columnName)
    {
        foreach (Expression expr in expressions)
        {
            if (expr is ColumnReference { TableName: null } col
                && col.ColumnName.Equals(columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
