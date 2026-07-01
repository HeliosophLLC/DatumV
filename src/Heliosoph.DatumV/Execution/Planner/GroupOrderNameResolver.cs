using System.Diagnostics.CodeAnalysis;
using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Catalog.Providers;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution.Planner;

/// <summary>
/// Resolves bare-name and ordinal-position references in <c>GROUP BY</c> and
/// <c>ORDER BY</c> against the SELECT list, following PostgreSQL's rules:
/// <list type="bullet">
///   <item>Only a whole-item bare name or integer ordinal is a candidate — a
///   name buried inside a larger expression (<c>GROUP BY x + 1</c>) refers to an
///   input column, never an output alias.</item>
///   <item>An integer ordinal <c>N</c> selects the Nth SELECT item. In
///   <c>GROUP BY</c> it resolves to that item's underlying expression (grouping
///   runs pre-projection); in <c>ORDER BY</c> it resolves to a reference to that
///   item's output column (sorting runs post-projection).</item>
///   <item>For an ambiguous simple name (both an input column and an output
///   alias), <c>GROUP BY</c> binds to the input column — so an alias is only
///   substituted when the name is <em>not</em> a source column.</item>
/// </list>
/// <c>ORDER BY</c> only needs ordinal handling here: because it runs after the
/// <see cref="Operators.ProjectOperator"/>, a bare alias already resolves as a
/// real output column at runtime.
/// </summary>
internal static class GroupOrderNameResolver
{
    /// <summary>
    /// Rewrites <c>GROUP BY</c> items: an integer ordinal becomes the referenced
    /// SELECT item's expression, and a bare non-input-column name that matches an
    /// output alias becomes that alias's expression. Everything else is returned
    /// unchanged. When <paramref name="sourceColumns"/> is <see langword="null"/>
    /// (the source columns couldn't be determined), alias substitution is skipped
    /// — the conservative choice that never mis-resolves a shadowed name.
    /// </summary>
    public static IReadOnlyList<Expression> ResolveGroupBy(
        IReadOnlyList<Expression> groupBy,
        IReadOnlyList<SelectColumn> projection,
        IReadOnlySet<string>? sourceColumns)
    {
        if (ProjectionHasStar(projection))
        {
            // A star projection expands to an unknown number of output columns,
            // so ordinal positions can't be counted statically. Leave GROUP BY
            // untouched (bare source-column names already resolve at runtime).
            return groupBy;
        }

        List<Expression>? rewritten = null;
        for (int i = 0; i < groupBy.Count; i++)
        {
            Expression original = groupBy[i];
            Expression resolved = ResolveGroupByItem(original, projection, sourceColumns);
            if (!ReferenceEquals(resolved, original))
            {
                rewritten ??= [.. groupBy];
                rewritten[i] = resolved;
            }
        }

        return rewritten ?? groupBy;
    }

    private static Expression ResolveGroupByItem(
        Expression item,
        IReadOnlyList<SelectColumn> projection,
        IReadOnlySet<string>? sourceColumns)
    {
        if (TryGetOrdinalPosition(item, out long position))
        {
            return ProjectionItem(projection, position, "GROUP BY").Expression;
        }

        // A whole-item bare name resolves to an output alias only when it is not
        // an input column — PG binds an ambiguous GROUP BY name to the input
        // column. Unknown source columns (null set) are treated as input columns.
        if (item is ColumnReference { TableName: null, ColumnName: { } name }
            && sourceColumns is not null
            && !sourceColumns.Contains(name)
            && TryFindAlias(projection, name, out Expression? aliasExpression))
        {
            return aliasExpression;
        }

        return item;
    }

    /// <summary>
    /// Rewrites <c>ORDER BY</c> integer ordinals into references to the Nth
    /// output column. Bare aliases are left untouched — ORDER BY runs after
    /// projection, so they already resolve as real output columns.
    /// </summary>
    public static OrderByClause ResolveOrderBy(
        OrderByClause orderBy,
        IReadOnlyList<SelectColumn> projection)
    {
        if (ProjectionHasStar(projection))
        {
            return orderBy;
        }

        List<OrderByItem>? rewritten = null;
        for (int i = 0; i < orderBy.Items.Count; i++)
        {
            OrderByItem item = orderBy.Items[i];
            if (TryGetOrdinalPosition(item.Expression, out long position))
            {
                SelectColumn target = ProjectionItem(projection, position, "ORDER BY");
                string outputName = target.Alias ?? ColumnNameResolver.GetRawName(target.Expression);
                rewritten ??= [.. orderBy.Items];
                rewritten[i] = item with { Expression = new ColumnReference(outputName) };
            }
        }

        return rewritten is null ? orderBy : new OrderByClause(rewritten);
    }

    private static SelectColumn ProjectionItem(
        IReadOnlyList<SelectColumn> projection, long position, string clause)
    {
        if (position < 1 || position > projection.Count)
        {
            throw new QueryPlanException(
                $"{clause} position {position} is not in select list");
        }

        return projection[(int)position - 1];
    }

    private static bool TryFindAlias(
        IReadOnlyList<SelectColumn> projection, string name, [NotNullWhen(true)] out Expression? expression)
    {
        foreach (SelectColumn column in projection)
        {
            if (column.Alias is { } alias
                && string.Equals(alias, name, StringComparison.OrdinalIgnoreCase))
            {
                expression = column.Expression;
                return true;
            }
        }

        expression = null;
        return false;
    }

    private static bool ProjectionHasStar(IReadOnlyList<SelectColumn> projection)
    {
        foreach (SelectColumn column in projection)
        {
            if (column is SelectAllColumns or SelectTableColumns)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads an integer ordinal from a literal item. Only integral literals are
    /// ordinals — a fractional constant is an ordinary (no-op) sort/group key, as
    /// in PostgreSQL. The parser emits unary minus as a separate node, so a bare
    /// literal is always non-negative here.
    /// </summary>
    private static bool TryGetOrdinalPosition(Expression expression, out long position)
    {
        position = 0;
        if (expression is not LiteralExpression { Value: { } value })
        {
            return false;
        }

        switch (value)
        {
            case sbyte v: position = v; return true;
            case byte v: position = v; return true;
            case short v: position = v; return true;
            case ushort v: position = v; return true;
            case int v: position = v; return true;
            case uint v: position = v; return true;
            case long v: position = v; return true;
            case ulong v when v <= long.MaxValue: position = (long)v; return true;
            default: return false;
        }
    }

    /// <summary>
    /// Collects the set of column names exposed by a statement's FROM and JOIN
    /// sources, or <see langword="null"/> when any source's columns can't be
    /// determined statically (a view, a file-peeking table-valued function, or a
    /// subquery / CTE that projects <c>*</c>). A null result signals callers to
    /// skip GROUP BY alias substitution rather than risk mis-resolving a name
    /// that is actually an input column.
    /// </summary>
    public static IReadOnlySet<string>? TryCollectSourceColumnNames(
        SelectStatement statement, TableCatalog catalog, FunctionRegistry functions)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        SchemaResolver resolver = new(catalog, catalog.SearchPath);

        Dictionary<string, CommonTableExpression>? ctes = null;
        if (statement.CommonTableExpressions is { Count: > 0 } cteList)
        {
            ctes = new Dictionary<string, CommonTableExpression>(StringComparer.OrdinalIgnoreCase);
            foreach (CommonTableExpression cte in cteList)
            {
                ctes[cte.Name] = cte;
            }
        }

        if (statement.From is not null
            && !TryAddSourceColumns(statement.From.Source, catalog, functions, resolver, ctes, names))
        {
            return null;
        }

        if (statement.Joins is not null)
        {
            foreach (JoinClause join in statement.Joins)
            {
                if (!TryAddSourceColumns(join.Source, catalog, functions, resolver, ctes, names))
                {
                    return null;
                }
            }
        }

        return names;
    }

    private static bool TryAddSourceColumns(
        TableSource source,
        TableCatalog catalog,
        FunctionRegistry functions,
        SchemaResolver resolver,
        Dictionary<string, CommonTableExpression>? ctes,
        HashSet<string> names)
    {
        switch (source)
        {
            case TableReference tableRef:
                if (tableRef.SchemaName is null
                    && ctes is not null
                    && ctes.TryGetValue(tableRef.Name, out CommonTableExpression? cte))
                {
                    return TryAddQueryOutputNames(cte.Body, names);
                }

                if (resolver.TryResolve(tableRef.SchemaName, tableRef.Name, out QualifiedName qn)
                    && catalog.TryGetTable(qn, out ITableProvider? provider))
                {
                    foreach (ColumnInfo column in provider.GetSchema().Columns)
                    {
                        names.Add(column.Name);
                    }

                    return true;
                }

                // Unresolved as a base table (view, or an unknown name) — bail to
                // the conservative path.
                return false;

            case SubquerySource subquery:
                return TryAddSelectOutputNames(subquery.Query, names);

            case FunctionSource functionSource:
                return TryAddTvfColumns(functionSource, functions, names);

            default:
                return false;
        }
    }

    /// <summary>
    /// Adds a table-valued function's output column names, discovered by calling
    /// its <see cref="ITableValuedFunction.ValidateArguments"/> with placeholder
    /// argument kinds — the same best-effort probe the scope validator uses.
    /// Returns <see langword="false"/> when the function is unknown or rejects the
    /// probe (e.g. a path-typed reader that peeks a real file), leaving the
    /// caller on the conservative path.
    /// </summary>
    private static bool TryAddTvfColumns(
        FunctionSource source, FunctionRegistry functions, HashSet<string> names)
    {
        ITableValuedFunction? tvf = functions.TryGetTableValued(source.CallName);
        if (tvf is null)
        {
            return false;
        }

        try
        {
            DataKind[] argumentKinds = new DataKind[source.Arguments.Count];
            DataValue?[] constantArguments = new DataValue?[source.Arguments.Count];
            for (int i = 0; i < source.Arguments.Count; i++)
            {
                // Float32 matches DataKindMatcher.Any, so TVFs whose output names
                // don't depend on argument types accept the probe. Those that do
                // inspect kinds may reject it — handled by the catch below.
                argumentKinds[i] = DataKind.Float32;
                constantArguments[i] = null;
            }

            Schema outputSchema = tvf.ValidateArguments(
                argumentKinds, constantArguments, new ByteArrayValueStore(), cancellationToken: default);
            if (outputSchema.Columns.Count == 0)
            {
                return false;
            }

            foreach (ColumnInfo column in outputSchema.Columns)
            {
                names.Add(column.Name);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryAddQueryOutputNames(QueryExpression query, HashSet<string> names)
    {
        // Only a plain SELECT exposes statically-known output names here; a set
        // operation (UNION/INTERSECT/EXCEPT) falls through to the conservative
        // path.
        return query is SelectQueryExpression { Statement: { } statement }
            && TryAddSelectOutputNames(statement, names);
    }

    private static bool TryAddSelectOutputNames(SelectStatement select, HashSet<string> names)
    {
        if (select.Columns is null)
        {
            return false;
        }

        foreach (SelectColumn column in select.Columns)
        {
            if (column is SelectAllColumns or SelectTableColumns)
            {
                return false;
            }

            names.Add(column.Alias ?? ColumnNameResolver.GetRawName(column.Expression));
        }

        return true;
    }
}
