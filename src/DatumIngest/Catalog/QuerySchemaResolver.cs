using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Catalog;

/// <summary>
/// Resolves the combined column schema of all table sources referenced in a
/// parsed <see cref="SelectStatement"/>. Handles FROM, JOINs, subqueries,
/// and table-valued functions to produce a <see cref="ResolvedQuerySchema"/>
/// suitable for editor autocomplete and query validation.
/// </summary>
public sealed class QuerySchemaResolver
{
    private readonly TableCatalog _catalog;
    private readonly FunctionRegistry _functionRegistry;

    /// <summary>
    /// Creates a resolver backed by the given catalog, function registry,
    /// and optional virtual schema registry.
    /// </summary>
    /// <param name="catalog">The catalog used to resolve table names to schemas.</param>
    /// <param name="functionRegistry">The registry used to resolve table-valued function schemas.</param>
    public QuerySchemaResolver(
        TableCatalog catalog,
        FunctionRegistry functionRegistry)
    {
        _catalog = catalog;
        _functionRegistry = functionRegistry;
    }

    /// <summary>
    /// Resolves the combined schema of all table sources in the given statement,
    /// merging FROM and JOIN sources with alias-aware column naming.
    /// </summary>
    /// <param name="statement">The parsed SELECT statement to analyze.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved schema containing columns from all referenced sources.</returns>
    /// <inheritdoc cref="ResolveAsync(SelectStatement, CancellationToken)"/>
    public Task<ResolvedQuerySchema> ResolveAsync(
        SelectStatement statement,
        CancellationToken cancellationToken)
    {
        return ResolveAsyncCore(statement, null, cancellationToken);
    }

    /// <summary>
    /// Core resolution method. Builds the effective CTE scope by layering
    /// <paramref name="inheritedCommonTableExpressions"/> (the outer scope, passed
    /// from a CTE body that references sibling CTEs) under the statement's own
    /// WITH-clause definitions, then resolves FROM and JOIN sources.
    /// </summary>
    private async Task<ResolvedQuerySchema> ResolveAsyncCore(
        SelectStatement statement,
        IReadOnlyDictionary<string, CommonTableExpression>? inheritedCommonTableExpressions,
        CancellationToken cancellationToken)
    {
        // Build the effective CTE scope: start from the inherited outer scope
        // and overlay this statement's own WITH definitions (inner always wins).
        Dictionary<string, CommonTableExpression>? commonTableExpressionsByName = null;

        if (inheritedCommonTableExpressions is not null && inheritedCommonTableExpressions.Count > 0)
        {
            commonTableExpressionsByName = new(inheritedCommonTableExpressions, StringComparer.OrdinalIgnoreCase);
        }

        if (statement.CommonTableExpressions is not null && statement.CommonTableExpressions.Count > 0)
        {
            commonTableExpressionsByName ??= new(StringComparer.OrdinalIgnoreCase);
            foreach (CommonTableExpression commonTableExpression in statement.CommonTableExpressions)
            {
                commonTableExpressionsByName[commonTableExpression.Name] = commonTableExpression;
            }
        }

        List<ResolvedColumn> allColumns = new();

        // Resolve the primary FROM source.
        if (statement.From is not null)
        {
            IReadOnlyList<ResolvedColumn> fromColumns =
                await ResolveSourceAsync(statement.From.Source, commonTableExpressionsByName, cancellationToken).ConfigureAwait(false);
            allColumns.AddRange(fromColumns);
        }

        // Resolve each JOINed source.
        if (statement.Joins is not null)
        {
            foreach (JoinClause join in statement.Joins)
            {
                IReadOnlyList<ResolvedColumn> joinColumns =
                    await ResolveSourceAsync(join.Source, commonTableExpressionsByName, cancellationToken).ConfigureAwait(false);

                // LEFT/RIGHT/FULL OUTER joins may produce nulls for the outer side.
                if (join.Type is JoinType.Left or JoinType.Right or JoinType.FullOuter)
                {
                    joinColumns = MarkNullable(joinColumns);
                }

                allColumns.AddRange(joinColumns);
            }
        }

        return new ResolvedQuerySchema(allColumns);
    }

    /// <summary>
    /// Dispatches to the appropriate resolution method based on the table source type.
    /// </summary>
    private async Task<IReadOnlyList<ResolvedColumn>> ResolveSourceAsync(
        TableSource source,
        IReadOnlyDictionary<string, CommonTableExpression>? commonTableExpressionsByName,
        CancellationToken cancellationToken)
    {
        return source switch
        {
            TableReference tableReference => await ResolveTableReferenceAsync(
                tableReference, commonTableExpressionsByName, cancellationToken).ConfigureAwait(false),

            SubquerySource subquery => await ResolveSubqueryAsync(
                subquery, cancellationToken).ConfigureAwait(false),

            FunctionSource functionSource => ResolveFunctionSource(functionSource),

            _ => throw new InvalidOperationException(
                $"Unsupported table source type: {source.GetType().Name}."),
        };
    }

    /// <summary>
    /// Resolves a named table reference. Unqualified names check CTE
    /// definitions first, then fall through to the catalog.
    /// Applies the alias (or table name) as the source identifier.
    /// </summary>
    private async Task<IReadOnlyList<ResolvedColumn>> ResolveTableReferenceAsync(
        TableReference tableReference,
        IReadOnlyDictionary<string, CommonTableExpression>? commonTableExpressionsByName,
        CancellationToken cancellationToken)
    {
        // Check CTE definitions before falling through to the catalog.
        if (commonTableExpressionsByName is not null &&
            commonTableExpressionsByName.TryGetValue(tableReference.Name, out CommonTableExpression? commonTableExpression))
        {
            return await ResolveCommonTableExpressionAsync(
                commonTableExpression, tableReference.Alias, commonTableExpressionsByName, cancellationToken).ConfigureAwait(false);
        }

        Schema schema = _catalog[tableReference.Name].GetSchema();

        string sourceIdentifier = tableReference.Alias ?? tableReference.Name;
        return ToResolvedColumns(schema, sourceIdentifier);
    }

    /// <summary>
    /// Resolves the output schema of a CTE by extracting the leftmost SELECT
    /// statement from the CTE body's query expression.
    /// </summary>
    /// <summary>
    /// Resolves the output schema of a CTE by extracting the leftmost SELECT
    /// statement from the CTE body's query expression.
    /// </summary>
    private async Task<IReadOnlyList<ResolvedColumn>> ResolveCommonTableExpressionAsync(
        CommonTableExpression commonTableExpression,
        string? tableAlias,
        IReadOnlyDictionary<string, CommonTableExpression>? outerCommonTableExpressions,
        CancellationToken cancellationToken)
    {
        // Extract the leftmost SELECT from the CTE body to determine the output schema.
        // Pass the outer CTE scope so the body can reference sibling CTEs.
        SelectStatement leftmostStatement = ExtractLeftmostStatement(commonTableExpression.Body);

        // Resolve the FROM/JOIN source schema so SELECT expressions can reference columns.
        ResolvedQuerySchema sourceSchema = await ResolveAsyncCore(
            leftmostStatement, outerCommonTableExpressions, cancellationToken).ConfigureAwait(false);

        string sourceIdentifier = tableAlias ?? commonTableExpression.Name;

        // Build a flat Schema for type inference on SELECT expressions.
        Schema flatSourceSchema = ToSchema(sourceSchema);

        // Project the CTE body's SELECT clause to compute the actual output columns.
        // This mirrors ResolveSubqueryAsync so that a CTE with a narrowing projection
        // (e.g. SELECT a, b FROM wide_table) exposes only those columns to the outer query.
        List<ResolvedColumn> outputColumns = new();
        HashSet<int> aliasedPositions = new();

        foreach (SelectColumn selectColumn in leftmostStatement.Columns)
        {
            switch (selectColumn)
            {
                case SelectAllColumns:
                    foreach (ResolvedColumn inner in sourceSchema.Columns)
                    {
                        outputColumns.Add(inner with { SourceTableOrAlias = sourceIdentifier });
                    }
                    break;

                case SelectTableColumns tableColumns:
                    foreach (ResolvedColumn inner in sourceSchema.FindColumns(tableColumns.TableName))
                    {
                        outputColumns.Add(inner with { SourceTableOrAlias = sourceIdentifier });
                    }
                    break;

                default:
                    string outputName = selectColumn.Alias
                        ?? ColumnNameResolver.GetRawName(selectColumn.Expression);

                    DataKind? kind = ExpressionTypeResolver.ResolveType(
                        selectColumn.Expression, flatSourceSchema, _functionRegistry);

                    outputColumns.Add(new ResolvedColumn(
                        outputName,
                        kind ?? DataKind.String,
                        Nullable: true,
                        sourceIdentifier));

                    if (selectColumn.Alias is not null)
                    {
                        aliasedPositions.Add(outputColumns.Count - 1);
                    }
                    break;
            }
        }

        // Deduplicate auto-generated column names (same as ResolveSubqueryAsync).
        string[] names = outputColumns.Select(column => column.ColumnName).ToArray();
        ColumnNameResolver.DeduplicateNames(names, aliasedPositions);
        List<ResolvedColumn> deduplicatedColumns = new(outputColumns.Count);
        for (int index = 0; index < outputColumns.Count; index++)
        {
            deduplicatedColumns.Add(outputColumns[index] with { ColumnName = names[index] });
        }

        // If explicit column names were provided in the CTE definition (e.g. WITH cte(a, b) AS (...)),
        // rename the output columns positionally, overriding whatever the SELECT clause produced.
        if (commonTableExpression.ColumnNames is not null && commonTableExpression.ColumnNames.Count > 0)
        {
            List<ResolvedColumn> renamedColumns = new(deduplicatedColumns.Count);
            for (int index = 0; index < deduplicatedColumns.Count; index++)
            {
                string columnName = index < commonTableExpression.ColumnNames.Count
                    ? commonTableExpression.ColumnNames[index]
                    : deduplicatedColumns[index].ColumnName;

                renamedColumns.Add(deduplicatedColumns[index] with
                {
                    ColumnName = columnName,
                    SourceTableOrAlias = sourceIdentifier,
                });
            }

            return renamedColumns;
        }

        return deduplicatedColumns;
    }

    /// <summary>
    /// Extracts the leftmost <see cref="SelectStatement"/> from a query expression,
    /// walking into the left branch of compound queries.
    /// </summary>
    private static SelectStatement ExtractLeftmostStatement(QueryExpression query)
    {
        return query switch
        {
            SelectQueryExpression select => select.Statement,
            CompoundQueryExpression compound => ExtractLeftmostStatement(compound.Left),
            _ => throw new InvalidOperationException(
                $"Unexpected query expression type: {query.GetType().Name}"),
        };
    }

    /// <summary>
    /// Resolves a subquery by recursively resolving its inner statement and then
    /// determining the output columns from the SELECT clause.
    /// </summary>
    private async Task<IReadOnlyList<ResolvedColumn>> ResolveSubqueryAsync(
        SubquerySource subquery,
        CancellationToken cancellationToken)
    {
        // Recursively resolve the inner query's source schema to determine
        // what columns the inner SELECT expressions can reference.
        ResolvedQuerySchema innerSourceSchema = await ResolveAsync(
            subquery.Query, cancellationToken).ConfigureAwait(false);

        // Build a Schema from the inner resolved columns so ExpressionTypeResolver
        // can look up column types.
        Schema innerSchema = ToSchema(innerSourceSchema);

        // Now determine what the SELECT clause projects out.
        List<ResolvedColumn> outputColumns = new();
        HashSet<int> aliasedPositions = new();

        foreach (SelectColumn selectColumn in subquery.Query.Columns)
        {
            switch (selectColumn)
            {
                case SelectAllColumns:
                    // SELECT * passes through all inner source columns.
                    foreach (ResolvedColumn inner in innerSourceSchema.Columns)
                    {
                        outputColumns.Add(inner with { SourceTableOrAlias = subquery.Alias });
                    }
                    break;

                case SelectTableColumns tableColumns:
                    // SELECT t.* passes through columns from the specified table.
                    foreach (ResolvedColumn inner in innerSourceSchema.FindColumns(tableColumns.TableName))
                    {
                        outputColumns.Add(inner with { SourceTableOrAlias = subquery.Alias });
                    }
                    break;

                default:
                    // Named expression — infer type and determine output name.
                    string outputName = selectColumn.Alias
                        ?? ColumnNameResolver.GetRawName(selectColumn.Expression);

                    DataKind? kind = ExpressionTypeResolver.ResolveType(
                        selectColumn.Expression, innerSchema, _functionRegistry);

                    outputColumns.Add(new ResolvedColumn(
                        outputName,
                        kind ?? DataKind.String,
                        Nullable: true,
                        subquery.Alias));
                    if (selectColumn.Alias is not null)
                    {
                        aliasedPositions.Add(outputColumns.Count - 1);
                    }
                    break;
            }
        }

        // Deduplicate auto-generated column names.
        string[] names = outputColumns.Select(column => column.ColumnName).ToArray();
        ColumnNameResolver.DeduplicateNames(names, aliasedPositions);
        List<ResolvedColumn> deduplicatedColumns = new(outputColumns.Count);
        for (int index = 0; index < outputColumns.Count; index++)
        {
            deduplicatedColumns.Add(outputColumns[index] with { ColumnName = names[index] });
        }

        return deduplicatedColumns;
    }

    /// <summary>
    /// Resolves a table-valued function source. If the function implements
    /// <see cref="ISchemaAwareTableFunction"/>, returns its output schema;
    /// otherwise returns an empty column list.
    /// </summary>
    private IReadOnlyList<ResolvedColumn> ResolveFunctionSource(FunctionSource functionSource)
    {
        ITableValuedFunction? function = _functionRegistry.TryGetTableValued(functionSource.FunctionName);
        if (function is null)
        {
            return [];
        }

        if (function is not ISchemaAwareTableFunction schemaAware)
        {
            return [];
        }

        // Resolve argument kinds from literal expressions where possible.
        DataKind[] argumentKinds = new DataKind[functionSource.Arguments.Count];
        for (int index = 0; index < functionSource.Arguments.Count; index++)
        {
            // Use an empty schema — function source arguments are typically
            // literals, not column references.
            Schema emptySchema = new([new ColumnInfo("_placeholder", DataKind.Float32, nullable: false)]);
            DataKind? kind = ExpressionTypeResolver.ResolveType(
                functionSource.Arguments[index], emptySchema, _functionRegistry);
            argumentKinds[index] = kind ?? DataKind.Float32;
        }

        // The element-kind-aware dispatch (formerly used by UNNEST) was retired
        // alongside UnnestFunction. Restore it as part of the reference-type-array
        // consolidation when typed-array TVFs reappear.
        Schema outputSchema = schemaAware.GetOutputSchema(argumentKinds);

        string sourceIdentifier = functionSource.Alias ?? functionSource.FunctionName;
        return ToResolvedColumns(outputSchema, sourceIdentifier);
    }

    /// <summary>
    /// Converts a <see cref="Schema"/> to a list of <see cref="ResolvedColumn"/>
    /// entries with the given source identifier.
    /// </summary>
    private static IReadOnlyList<ResolvedColumn> ToResolvedColumns(Schema schema, string sourceIdentifier)
    {
        ResolvedColumn[] columns = new ResolvedColumn[schema.Columns.Count];

        for (int index = 0; index < schema.Columns.Count; index++)
        {
            ColumnInfo column = schema.Columns[index];
            columns[index] = new ResolvedColumn(
                column.Name,
                column.Kind,
                column.Nullable,
                sourceIdentifier,
                column.ArrayElementKind);
        }

        return columns;
    }

    /// <summary>
    /// Builds a flat <see cref="Schema"/> from a <see cref="ResolvedQuerySchema"/>,
    /// using both qualified and unqualified column names so that
    /// <see cref="ExpressionTypeResolver"/> can resolve either form.
    /// </summary>
    private static Schema ToSchema(ResolvedQuerySchema resolvedSchema)
    {
        List<ColumnInfo> columns = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (ResolvedColumn resolved in resolvedSchema.Columns)
        {
            // Add qualified name (alias.column).
            if (resolved.SourceTableOrAlias is not null)
            {
                string qualifiedName = $"{resolved.SourceTableOrAlias}.{resolved.ColumnName}";
                if (seen.Add(qualifiedName))
                {
                    columns.Add(new ColumnInfo(qualifiedName, resolved.Kind, resolved.Nullable, resolved.ArrayElementKind));
                }
            }

            // Add unqualified name (first occurrence wins).
            if (seen.Add(resolved.ColumnName))
            {
                columns.Add(new ColumnInfo(resolved.ColumnName, resolved.Kind, resolved.Nullable, resolved.ArrayElementKind));
            }
        }

        return new Schema(columns);
    }

    /// <summary>
    /// Returns a copy of the column list with all columns marked nullable,
    /// reflecting that outer joins may produce NULL values for unmatched rows.
    /// </summary>
    private static IReadOnlyList<ResolvedColumn> MarkNullable(IReadOnlyList<ResolvedColumn> columns)
    {
        ResolvedColumn[] result = new ResolvedColumn[columns.Count];

        for (int index = 0; index < columns.Count; index++)
        {
            result[index] = columns[index] with { Nullable = true };
        }

        return result;
    }
}
