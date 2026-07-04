using Heliosoph.DatumV.Catalog.Registries;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Catalog;

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
    /// Stack of views currently being expanded during this resolver call.
    /// Mirrors <see cref="Execution.Planner.SourcePlanner"/>'s cycle
    /// detection — a reference to a view already on the stack is a
    /// circular dependency and gets rejected. Per-instance, so each
    /// resolver call (LSP manifest build, REST catalog request) has its
    /// own protection without cross-call interference.
    /// </summary>
    private readonly Stack<QualifiedName> _viewExpansionStack = new();

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
    /// Resolves the projected output columns of <paramref name="statement"/>
    /// — what the SELECT clause emits — rather than its FROM/JOIN source
    /// columns. Use this when the caller wants the schema of a query's
    /// result rows: e.g. CREATE VIEW body resolution surfaces only the
    /// view's declared projection through hover / completion, not the
    /// (much wider) set of columns the body's underlying tables expose.
    /// </summary>
    /// <param name="statement">The SELECT statement whose projection to resolve.</param>
    /// <param name="outputAlias">
    /// Logical name to stamp on each output column's
    /// <see cref="ResolvedColumn.SourceTableOrAlias"/>. Callers from a
    /// view path typically pass the view's qualified name so qualified
    /// references downstream resolve correctly.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Internally synthesises a <see cref="SubquerySource"/> wrapping the
    /// statement and dispatches through the same projection-aware path
    /// the subquery resolver uses, so SELECT * / table.* / aliased
    /// expressions / column-name deduplication all flow uniformly.
    /// </remarks>
    public async Task<ResolvedQuerySchema> ResolveProjectionAsync(
        SelectStatement statement,
        string outputAlias,
        CancellationToken cancellationToken)
    {
        SubquerySource synthetic = new(statement, outputAlias);
        IReadOnlyList<ResolvedColumn> columns =
            await ResolveSubqueryAsync(synthetic, cancellationToken).ConfigureAwait(false);
        return new ResolvedQuerySchema(columns);
    }

    /// <summary>
    /// Resolves the projected output columns of a <see cref="QueryExpression"/>,
    /// covering both bare <c>SELECT</c> statements and compound queries
    /// (<c>UNION</c> / <c>INTERSECT</c> / <c>EXCEPT</c>). Compound queries
    /// recursively unify each leaf SELECT's projection — column names come
    /// from the leftmost branch (PostgreSQL semantics), column types are
    /// the per-position common shape via
    /// <see cref="TypeCoercion.FindCommonShape"/>, and a column is
    /// nullable if any branch's column is nullable.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when compound branches have differing column counts, or when
    /// per-position column shapes are incompatible (e.g. <c>String</c> on
    /// one branch and <c>Int32</c> on another, or array on one branch and
    /// scalar on another).
    /// </exception>
    public async Task<ResolvedQuerySchema> ResolveProjectionAsync(
        QueryExpression query,
        string outputAlias,
        CancellationToken cancellationToken)
    {
        switch (query)
        {
            case SelectQueryExpression select:
                return await ResolveProjectionAsync(select.Statement, outputAlias, cancellationToken)
                    .ConfigureAwait(false);

            case CompoundQueryExpression compound:
                return await ResolveCompoundProjectionAsync(compound, outputAlias, cancellationToken)
                    .ConfigureAwait(false);

            default:
                throw new NotSupportedException(
                    $"Cannot resolve projection schema for query expression of type " +
                    $"'{query.GetType().Name}'.");
        }
    }

    private async Task<ResolvedQuerySchema> ResolveCompoundProjectionAsync(
        CompoundQueryExpression compound,
        string outputAlias,
        CancellationToken cancellationToken)
    {
        // Recurse through both arms — either side may itself be a
        // compound, so the leftmost SELECT in a chain like
        // (A UNION B) UNION C ends up the column-name source for the
        // whole tree.
        ResolvedQuerySchema left = await ResolveProjectionAsync(
            compound.Left, outputAlias, cancellationToken).ConfigureAwait(false);
        ResolvedQuerySchema right = await ResolveProjectionAsync(
            compound.Right, outputAlias, cancellationToken).ConfigureAwait(false);

        if (left.Columns.Count != right.Columns.Count)
        {
            throw new InvalidOperationException(
                $"each {compound.OperationType.ToString().ToUpperInvariant()} query must have the " +
                $"same number of columns (left has {left.Columns.Count}, " +
                $"right has {right.Columns.Count}).");
        }

        ResolvedColumn[] unified = new ResolvedColumn[left.Columns.Count];
        for (int i = 0; i < left.Columns.Count; i++)
        {
            ResolvedColumn l = left.Columns[i];
            ResolvedColumn r = right.Columns[i];

            (DataKind Kind, bool IsArray, bool IsMultiDim)? shape = TypeCoercion.FindCommonShape(
                l.Kind, l.IsArray, l.IsMultiDim,
                r.Kind, r.IsArray, r.IsMultiDim);
            if (shape is null)
            {
                throw new InvalidOperationException(
                    $"{compound.OperationType.ToString().ToUpperInvariant()} types " +
                    $"{DescribeShape(l)} and {DescribeShape(r)} cannot be matched at column " +
                    $"{i + 1} (\"{l.ColumnName}\").");
            }

            unified[i] = new ResolvedColumn(
                ColumnName: l.ColumnName,
                Kind: shape.Value.Kind,
                Nullable: l.Nullable || r.Nullable,
                SourceTableOrAlias: outputAlias,
                IsArray: shape.Value.IsArray,
                IsMultiDim: shape.Value.IsMultiDim);
        }

        return new ResolvedQuerySchema(unified);
    }

    private static string DescribeShape(ResolvedColumn column)
    {
        string kind = column.Kind.ToString();
        if (!column.IsArray) return kind;
        return column.IsMultiDim ? $"{kind}[][]" : $"{kind}[]";
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

        // View reference: resolve the body's SELECT projection by wrapping
        // it as a synthetic subquery source. The existing subquery resolver
        // handles SELECT * / table.* / aliased expressions correctly, so we
        // get column-name-and-shape projection for free.
        if (_catalog.Views.TryResolve(tableReference.SchemaName, tableReference.Name, _catalog.SearchPath, out ViewDescriptor? view))
        {
            if (_viewExpansionStack.Contains(view.QualifiedName))
            {
                string chain = string.Join(" -> ",
                    _viewExpansionStack.Reverse().Select(n => n.ToString()).Append(view.QualifiedName.ToString()));
                throw new InvalidOperationException(
                    $"Circular view reference detected: {chain}.");
            }

            _viewExpansionStack.Push(view.QualifiedName);
            try
            {
                SubquerySource subquery = new(view.Body, tableReference.Alias ?? tableReference.Name);
                return await ResolveSubqueryAsync(subquery, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _viewExpansionStack.Pop();
            }
        }

        // Honor explicit schema qualifiers — `datasets.foo`, `system.udfs`, etc.
        // Previously this path dropped `SchemaName` and routed every lookup
        // through the `public.<name>` default in `QualifiedName.Parse`, which
        // silently broke COPY / CTAS / CREATE VIEW bodies that reference a
        // mounted non-public schema (datasets / system / information_schema).
        QualifiedName qn = tableReference.SchemaName is not null
            ? new QualifiedName(tableReference.SchemaName, tableReference.Name)
            : QualifiedName.Parse(tableReference.Name);
        Schema schema = _catalog[qn].GetSchema();

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

        AppendLetBindingProjections(
            leftmostStatement.LetBindings, flatSourceSchema, sourceIdentifier, outputColumns, aliasedPositions);

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

                    (DataKind Kind, bool IsArray, bool IsMultiDim)? shape = ExpressionTypeResolver.ResolveTypeShape(
                        selectColumn.Expression, flatSourceSchema, _functionRegistry);

                    // Surface struct-literal field metadata so downstream
                    // consumers (notably ExportPlan / Parquet sink) can build
                    // a real STRUCT column instead of a fields-less Struct
                    // ColumnInfo that the encoder factory has to reject.
                    IReadOnlyList<ColumnInfo>? structFields = ResolveStructFields(
                        selectColumn.Expression, flatSourceSchema);

                    outputColumns.Add(new ResolvedColumn(
                        outputName,
                        shape?.Kind ?? DataKind.String,
                        Nullable: true,
                        sourceIdentifier,
                        IsArray: shape?.IsArray ?? false,
                        IsMultiDim: shape?.IsMultiDim ?? false)
                    {
                        Fields = structFields,
                    });

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

        // LET bindings with an OutputAlias (`LET x = expr AS name`) project
        // into the output schema ahead of the explicit SELECT columns —
        // matches how ModelInvocationHoister and the runtime ProjectOperator
        // build the projection. Without this, exports (COPY) and CTAS that
        // wrap a LET-projecting query miss those columns entirely.
        AppendLetBindingProjections(
            subquery.Query.LetBindings, innerSchema, subquery.Alias, outputColumns, aliasedPositions);

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

                    (DataKind Kind, bool IsArray, bool IsMultiDim)? shape = ExpressionTypeResolver.ResolveTypeShape(
                        selectColumn.Expression, innerSchema, _functionRegistry);

                    IReadOnlyList<ColumnInfo>? structFields = ResolveStructFields(
                        selectColumn.Expression, innerSchema);

                    outputColumns.Add(new ResolvedColumn(
                        outputName,
                        shape?.Kind ?? DataKind.String,
                        Nullable: true,
                        subquery.Alias,
                        IsArray: shape?.IsArray ?? false,
                        IsMultiDim: shape?.IsMultiDim ?? false)
                    {
                        Fields = structFields,
                    });
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
    /// Resolves a table-valued function source by calling
    /// <see cref="ITableValuedFunction.ValidateArguments"/> with the inferred
    /// argument kinds and constant-folded literal values for any argument that
    /// is a literal (in source or post-<c>ParameterBinder</c> substitution).
    /// Returns an empty column list if the function is unknown, if argument
    /// validation fails, or if the TVF's plan-time peek raises an IO/format
    /// exception against the resolved file.
    /// </summary>
    private IReadOnlyList<ResolvedColumn> ResolveFunctionSource(FunctionSource functionSource)
    {
        ITableValuedFunction? function = _functionRegistry.TryGetTableValued(functionSource.CallName);
        if (function is null)
        {
            return [];
        }

        int argCount = functionSource.Arguments.Count;
        DataKind[] argumentKinds = new DataKind[argCount];
        DataValue?[] constantArguments = new DataValue?[argCount];

        // Per-call store backing any constant DataValue (long file paths,
        // dataset names) surfaced to the TVF for this single ValidateArguments
        // call. ByteArrayValueStore — a plain managed byte[] — is right-sized
        // for the throwaway use case: no VA reservation, no refcount, no
        // IDisposable. The TVF's returned Schema references only ColumnInfo
        // (name + kind + nullability), so the store falls out of scope and
        // gets GC'd as soon as we return. ITableValuedFunction.ValidateArguments
        // documents that implementations must not retain the constant
        // references past the call.
        ByteArrayValueStore constantStore = new();

        // For each argument: resolve the kind (used by all TVFs), and try to
        // surface the constant-folded value (used by TVFs whose output schema
        // depends on the data they're about to read — open_fits_table,
        // open_h5_dataset). LiteralExpression includes $parameter references
        // after ParameterBinder substitution, so this path fires for both
        // literal-in-source and bound-parameter calls.
        for (int index = 0; index < argCount; index++)
        {
            Expression argument = functionSource.Arguments[index];

            Schema emptySchema = new([new ColumnInfo("_placeholder", DataKind.Float32, nullable: false)]);
            DataKind? kind = ExpressionTypeResolver.ResolveType(
                argument, emptySchema, _functionRegistry);
            argumentKinds[index] = kind ?? DataKind.Float32;

            if (LiteralHoister.TryGetConstantValue(argument, constantStore, out DataValue constantValue))
            {
                constantArguments[index] = constantValue;
            }
        }

        try
        {
            Schema outputSchema = function.ValidateArguments(
                argumentKinds,
                constantArguments,
                constantStore,
                cancellationToken: default);
            string sourceIdentifier = functionSource.Alias ?? functionSource.FunctionName;
            return ToResolvedColumns(outputSchema, sourceIdentifier);
        }
        catch (FunctionArgumentException)
        {
            return [];
        }
        // TVFs that peek files at plan time (open_fits_table, open_h5_dataset)
        // can raise IO/format exceptions before they can produce a fallback
        // schema. Swallow those here so a broken file makes the source resolve
        // to "no columns known" rather than blowing up the whole resolution
        // (which would also kill column completion for unrelated sources in
        // the same query). Runtime execution will surface the underlying
        // failure with a real diagnostic when the TVF actually runs.
        catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException
            or FormatException
            or InvalidDataException
            or NotSupportedException)
        {
            return [];
        }
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
            // Source columns become multi-dim either explicitly (IsMultiDim flag)
            // or by carrying a multi-dim FixedShape — keep both paths in sync so
            // the resolved view is the union.
            bool isMultiDim = column.IsMultiDim
                || (column.IsArray && column.FixedShape is { Length: >= 2 });
            columns[index] = new ResolvedColumn(
                column.Name,
                column.Kind,
                column.Nullable,
                sourceIdentifier,
                IsArray: column.IsArray,
                IsMultiDim: isMultiDim)
            {
                // Struct columns keep their field lists through SELECT * /
                // whole-schema resolution so CTAS targets and export sinks
                // see the declared shape.
                Fields = column.Fields,
            };
        }

        return columns;
    }

    /// <summary>
    /// Appends one <see cref="ResolvedColumn"/> per LET binding whose
    /// <see cref="LetBinding.OutputAlias"/> is non-null, in declaration
    /// order. Mirrors <c>ModelInvocationHoister.RewriteProjectionWithLet</c>
    /// — aliased LET bindings project ahead of the explicit SELECT columns.
    /// No-op when <paramref name="letBindings"/> is null or contains no
    /// aliased entries.
    /// </summary>
    private void AppendLetBindingProjections(
        IReadOnlyList<LetBinding>? letBindings,
        Schema sourceSchema,
        string sourceIdentifier,
        List<ResolvedColumn> outputColumns,
        HashSet<int> aliasedPositions)
    {
        if (letBindings is null || letBindings.Count == 0) return;
        foreach (LetBinding binding in letBindings)
        {
            if (binding.OutputAlias is null) continue;

            (DataKind Kind, bool IsArray, bool IsMultiDim)? shape =
                ExpressionTypeResolver.ResolveTypeShape(
                    binding.Expression, sourceSchema, _functionRegistry);

            outputColumns.Add(new ResolvedColumn(
                binding.OutputAlias,
                shape?.Kind ?? DataKind.String,
                Nullable: true,
                sourceIdentifier,
                IsArray: shape?.IsArray ?? false,
                IsMultiDim: shape?.IsMultiDim ?? false));
            aliasedPositions.Add(outputColumns.Count - 1);
        }
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
                    columns.Add(BuildColumnInfo(qualifiedName, resolved));
                }
            }

            // Add unqualified name (first occurrence wins).
            if (seen.Add(resolved.ColumnName))
            {
                columns.Add(BuildColumnInfo(resolved.ColumnName, resolved));
            }
        }

        return new Schema(columns);
    }

    /// <summary>
    /// Builds a <see cref="ColumnInfo"/> from a <see cref="ResolvedColumn"/>
    /// under <paramref name="name"/>. Routes <see cref="DataKind.Struct"/>
    /// columns that carry resolved field metadata to the struct constructor
    /// so the children round-trip into the schema; everything else uses the
    /// flat scalar constructor with <see cref="ColumnInfo.IsArray"/> /
    /// <see cref="ColumnInfo.IsMultiDim"/> set.
    /// </summary>
    private static ColumnInfo BuildColumnInfo(string name, ResolvedColumn resolved)
    {
        if (resolved.Kind == DataKind.Struct && resolved.Fields is { } fields)
        {
            // Preserve IsArray so Array<Struct> column shapes survive the
            // round trip through this helper.
            return new ColumnInfo(name, resolved.Nullable, fields)
            {
                IsArray = resolved.IsArray,
                IsMultiDim = resolved.IsMultiDim,
            };
        }
        return new ColumnInfo(name, resolved.Kind, resolved.Nullable)
        {
            IsArray = resolved.IsArray,
            IsMultiDim = resolved.IsMultiDim,
        };
    }

    /// <summary>
    /// Extracts struct field metadata from a projection expression when the
    /// shape is statically known. Today this is the struct-literal case
    /// (<c>{ a: 1, b: 'x' }</c>); the resolver builds a transient
    /// <see cref="ColumnInfo"/> via <see cref="ExpressionTypeResolver.ResolveOutputColumnInfo"/>
    /// and forwards its <see cref="ColumnInfo.Fields"/>. Returns
    /// <see langword="null"/> for non-struct expressions and for struct
    /// expressions whose fields don't fully resolve — downstream consumers
    /// treat the column as <see cref="DataKind.Struct"/> without children.
    /// </summary>
    private IReadOnlyList<ColumnInfo>? ResolveStructFields(Expression expression, Schema sourceSchema)
    {
        // Direct struct literal — most common case (`SELECT { a:1, b:'x' }`).
        if (expression is StructLiteralExpression literal)
        {
            ColumnInfo info = ExpressionTypeResolver.ResolveOutputColumnInfo(
                literal, outputName: "_struct_probe", nullable: true, sourceSchema, _functionRegistry);
            return info.Fields;
        }

        // Array literal `[{ ... }, ...]` desugars to `array({ ... }, ...)`.
        // The element kind is the first arg's kind; pull child Fields from
        // the first struct-literal element. ArrayConstructorFunction enforces
        // homogeneity at ValidateArguments time, so peeking at element 0 is
        // sufficient for static shape recovery.
        if (expression is FunctionCallExpression call
            && string.Equals(call.CallName, "array", StringComparison.OrdinalIgnoreCase)
            && call.Arguments.Count > 0
            && call.Arguments[0] is StructLiteralExpression elementLiteral)
        {
            ColumnInfo info = ExpressionTypeResolver.ResolveOutputColumnInfo(
                elementLiteral, outputName: "_struct_array_probe", nullable: true, sourceSchema, _functionRegistry);
            return info.Fields;
        }

        // Direct column reference to a struct column (`SELECT s FROM src`) —
        // the source schema's ColumnInfo already carries the field list.
        if (expression is ColumnReference colRef)
        {
            ColumnInfo? source = sourceSchema.FindColumn(colRef.ColumnName);
            if (source is null && colRef.QualifiedName is { } qualifiedName)
            {
                // Joined schemas carry combined "alias.column" names.
                source = sourceSchema.FindColumn(qualifiedName);
            }
            if (source is { Kind: DataKind.Struct })
            {
                return source.Fields;
            }
        }

        return null;
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
