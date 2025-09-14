using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Evaluates SELECT column expressions against each incoming row,
/// producing output rows with the projected column set.
/// Expression results are wrapped in <see cref="LazyDataValue"/> thunks
/// so computation chains through nested SELECTs without eagerly materializing.
/// </summary>
public sealed class ProjectOperator : IQueryOperator
{
    private readonly IQueryOperator _source;
    private readonly IReadOnlyList<SelectColumn> _columns;
    private readonly IReadOnlyList<LetBinding>? _letBindings;
    private readonly Schema? _sourceSchema;

    /// <summary>
    /// Creates a project operator.
    /// </summary>
    /// <param name="source">The child operator producing rows.</param>
    /// <param name="columns">The SELECT columns to project.</param>
    /// <param name="letBindings">Optional LET bindings to evaluate before projection.</param>
    /// <param name="sourceSchema">Optional source schema for star expansion.</param>
    public ProjectOperator(
        IQueryOperator source,
        IReadOnlyList<SelectColumn> columns,
        IReadOnlyList<LetBinding>? letBindings = null,
        Schema? sourceSchema = null)
    {
        _source = source;
        _columns = columns;
        _letBindings = letBindings;
        _sourceSchema = sourceSchema;
    }

    /// <summary>The child operator producing rows.</summary>
    public IQueryOperator Source => _source;

    /// <summary>The projected SELECT columns.</summary>
    public IReadOnlyList<SelectColumn> Columns => _columns;

    /// <summary>The LET bindings evaluated before projection, or null if none.</summary>
    public IReadOnlyList<LetBinding>? LetBindings => _letBindings;

    /// <inheritdoc/>
    public OperatorPlanDescription DescribeForExplain()
    {
        List<string> columnDescriptions = [];
        foreach (SelectColumn column in _columns)
        {
            string formatted = QueryExplainer.FormatExpression(column.Expression);
            columnDescriptions.Add(column.Alias is not null
                ? $"{formatted} AS {column.Alias}"
                : formatted);
        }

        Dictionary<string, string> properties = new()
        {
            ["columns"] = string.Join(", ", columnDescriptions),
        };

        if (_letBindings is { Count: > 0 })
        {
            properties["let"] = string.Join(", ", _letBindings.Select(binding => binding.Name));
        }

        return new OperatorPlanDescription("Project")
        {
            Properties = properties,
            Children = [(Source, null)],
        };
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(ExecutionContext context)
    {
        ExpressionEvaluator evaluator = new(context.FunctionRegistry, context.QueryMeter, context.OuterRow);
        ProjectionSchema? schema = null;
        LocalBufferPool pool = context.LocalBufferPool;

        await foreach (RowBatch inputBatch in _source.ExecuteAsync(context).ConfigureAwait(false))
        {
            RowBatch outputBatch = RowBatch.Rent(inputBatch.Count);

            for (int index = 0; index < inputBatch.Count; index++)
            {
                Row row = inputBatch[index];
                schema ??= ProjectionSchema.Build(_columns, _letBindings, row);
                outputBatch.Add(schema.Project(row, evaluator, pool));
            }

            inputBatch.Return();
            yield return outputBatch;
        }
    }

    /// <summary>
    /// Pre-computed projection layout built once from the first source row.
    /// Holds the shared output column names, name-index dictionary, and a plan
    /// describing how to populate each output slot (copy from source ordinal or
    /// evaluate an expression). When LET bindings are present, the schema also
    /// holds an augmented column layout that includes source columns and LET
    /// binding names, enabling memoized evaluation of LET expressions before
    /// projection. Subsequent rows allocate only a <see cref="DataValue"/> array.
    /// </summary>
    private sealed class ProjectionSchema
    {
        private readonly string[] _names;
        private readonly Dictionary<string, int> _nameIndex;
        private readonly ProjectionSlot[] _slots;

        // LET binding support: augmented row layout for memoized evaluation.
        private readonly string[]? _augmentedNames;
        private readonly Dictionary<string, int>? _augmentedNameIndex;
        private readonly Expression[]? _letExpressions;
        private readonly int _sourceFieldCount;

        private ProjectionSchema(
            string[] names,
            Dictionary<string, int> nameIndex,
            ProjectionSlot[] slots,
            string[]? augmentedNames = null,
            Dictionary<string, int>? augmentedNameIndex = null,
            Expression[]? letExpressions = null,
            int sourceFieldCount = 0)
        {
            _names = names;
            _nameIndex = nameIndex;
            _slots = slots;
            _augmentedNames = augmentedNames;
            _augmentedNameIndex = augmentedNameIndex;
            _letExpressions = letExpressions;
            _sourceFieldCount = sourceFieldCount;
        }

        /// <summary>
        /// Builds the projection schema from the column list, optional LET bindings,
        /// and the first source row. Star and table-star columns are expanded using
        /// the source row's schema; named expressions record their expression for
        /// per-row evaluation. LET bindings produce an augmented row layout so their
        /// values are memoized and accessible by name during projection.
        /// </summary>
        internal static ProjectionSchema Build(
            IReadOnlyList<SelectColumn> columns,
            IReadOnlyList<LetBinding>? letBindings,
            Row firstRow)
        {
            int letCount = letBindings?.Count ?? 0;

            // Build augmented row layout when LET bindings are present.
            string[]? augmentedNames = null;
            Dictionary<string, int>? augmentedNameIndex = null;
            Expression[]? letExpressions = null;

            if (letCount > 0)
            {
                augmentedNames = new string[firstRow.FieldCount + letCount];
                for (int index = 0; index < firstRow.FieldCount; index++)
                {
                    augmentedNames[index] = firstRow.ColumnNames[index];
                }

                letExpressions = new Expression[letCount];
                for (int index = 0; index < letCount; index++)
                {
                    augmentedNames[firstRow.FieldCount + index] = letBindings![index].Name;
                    letExpressions[index] = letBindings[index].Expression;
                }

                augmentedNameIndex =
                    new Dictionary<string, int>(augmentedNames.Length, StringComparer.OrdinalIgnoreCase);
                for (int index = 0; index < augmentedNames.Length; index++)
                {
                    augmentedNameIndex[augmentedNames[index]] = index;
                }
            }

            // Build output column layout.
            List<string> names = new();
            HashSet<int> aliasedPositions = new();
            List<ProjectionSlot> slots = new();

            // Aliased LET bindings appear at the beginning of the output.
            for (int index = 0; index < letCount; index++)
            {
                if (letBindings![index].OutputAlias is not null)
                {
                    names.Add(letBindings[index].OutputAlias!);
                    aliasedPositions.Add(names.Count - 1);
                    slots.Add(ProjectionSlot.CopyOrdinal(firstRow.FieldCount + index));
                }
            }

            // Regular output columns.
            foreach (SelectColumn column in columns)
            {
                switch (column)
                {
                    case SelectAllColumns allColumns:
                        for (int index = 0; index < firstRow.FieldCount; index++)
                        {
                            if (IsExcluded(firstRow.ColumnNames[index], allColumns.ExcludedColumns))
                                continue;
                            Expression? replacement = FindReplacement(
                                firstRow.ColumnNames[index], allColumns.ReplacedColumns);
                            names.Add(firstRow.ColumnNames[index]);
                            slots.Add(replacement is not null
                                ? ProjectionSlot.Evaluate(replacement)
                                : ProjectionSlot.CopyOrdinal(index));
                        }
                        break;

                    case SelectTableColumns tableColumns:
                        string prefix = tableColumns.TableName + ".";
                        for (int index = 0; index < firstRow.FieldCount; index++)
                        {
                            string columnName = firstRow.ColumnNames[index];
                            if (columnName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                || columnName.Equals(
                                    tableColumns.TableName, StringComparison.OrdinalIgnoreCase))
                            {
                                if (IsExcluded(columnName, tableColumns.ExcludedColumns, prefix))
                                    continue;
                                Expression? replacement = FindReplacement(
                                    columnName, tableColumns.ReplacedColumns, prefix);
                                names.Add(columnName);
                                slots.Add(replacement is not null
                                    ? ProjectionSlot.Evaluate(replacement)
                                    : ProjectionSlot.CopyOrdinal(index));
                            }
                        }
                        break;

                    default:
                        string name = column.Alias
                            ?? ColumnNameResolver.GetRawName(column.Expression);
                        names.Add(name);
                        if (column.Alias is not null)
                        {
                            aliasedPositions.Add(names.Count - 1);
                        }
                        slots.Add(ProjectionSlot.Evaluate(column.Expression));
                        break;
                }
            }

            string[] nameArray = names.ToArray();
            ColumnNameResolver.DeduplicateNames(nameArray, aliasedPositions);
            Dictionary<string, int> nameIndex =
                new(nameArray.Length, StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < nameArray.Length; index++)
            {
                nameIndex[nameArray[index]] = index;
            }

            return new ProjectionSchema(
                nameArray, nameIndex, slots.ToArray(),
                augmentedNames, augmentedNameIndex, letExpressions, firstRow.FieldCount);
        }

        /// <summary>
        /// Projects a source row using the pre-computed schema. When LET bindings
        /// are present, builds an augmented row with memoized LET values before
        /// evaluating projection expressions. The output <see cref="DataValue"/>
        /// array is rented from <paramref name="pool"/> and owned for the query
        /// lifetime — the row can be safely held across iterations.
        /// </summary>
        internal Row Project(Row sourceRow, ExpressionEvaluator evaluator, LocalBufferPool pool)
        {
            if (_letExpressions is not null && _letExpressions.Length > 0)
            {
                return ProjectWithLetBindings(sourceRow, evaluator, pool);
            }

            DataValue[] values = pool.RentOwned(_slots.Length);

            for (int index = 0; index < _slots.Length; index++)
            {
                ProjectionSlot slot = _slots[index];
                values[index] = slot.SourceOrdinal >= 0
                    ? sourceRow[slot.SourceOrdinal]
                    : evaluator.Evaluate(slot.Expression!, sourceRow);
            }

            return new Row(_names, values, _nameIndex);
        }

        /// <summary>
        /// Projects a source row with LET bindings. Builds a temporary augmented
        /// row containing source columns plus LET binding values, evaluates each
        /// LET expression sequentially (so later bindings can reference earlier
        /// ones), then evaluates projection expressions against the augmented row.
        /// </summary>
        private Row ProjectWithLetBindings(Row sourceRow, ExpressionEvaluator evaluator, LocalBufferPool pool)
        {
            // Build augmented values: source columns + LET binding slots.
            DataValue[] augmentedValues = new DataValue[_sourceFieldCount + _letExpressions!.Length];
            for (int index = 0; index < _sourceFieldCount; index++)
            {
                augmentedValues[index] = sourceRow[index];
            }

            // The Row constructor stores the array by reference, so mutations
            // to augmentedValues are visible through the Row's indexers.
            Row augmentedRow = new(_augmentedNames!, augmentedValues, _augmentedNameIndex!);

            // Evaluate each LET binding sequentially. Each binding's result
            // is written into the augmented array before the next binding
            // is evaluated, enabling left-to-right chaining.
            for (int index = 0; index < _letExpressions.Length; index++)
            {
                augmentedValues[_sourceFieldCount + index] =
                    evaluator.Evaluate(_letExpressions[index], augmentedRow);
            }

            // Evaluate output slots against the augmented row.
            DataValue[] values = pool.RentOwned(_slots.Length);
            for (int index = 0; index < _slots.Length; index++)
            {
                ProjectionSlot slot = _slots[index];
                values[index] = slot.SourceOrdinal >= 0
                    ? augmentedRow[slot.SourceOrdinal]
                    : evaluator.Evaluate(slot.Expression!, augmentedRow);
            }

            return new Row(_names, values, _nameIndex);
        }
    }

    /// <summary>
    /// Describes how to populate a single output column: either copy from a
    /// source ordinal or evaluate an expression.
    /// </summary>
    private readonly struct ProjectionSlot
    {
        /// <summary>
        /// Source ordinal to copy from, or -1 if <see cref="Expression"/> should be evaluated.
        /// </summary>
        internal int SourceOrdinal { get; private init; }

        /// <summary>
        /// Expression to evaluate, or null when copying by ordinal.
        /// </summary>
        internal Expression? Expression { get; private init; }

        internal static ProjectionSlot CopyOrdinal(int ordinal) =>
            new() { SourceOrdinal = ordinal };

        internal static ProjectionSlot Evaluate(Expression expression) =>
            new() { SourceOrdinal = -1, Expression = expression };
    }

    /// <summary>
    /// Checks whether a column name appears in the exclusion list of a
    /// <c>SELECT * EXCEPT (...)</c> or <c>SELECT table.* EXCEPT (...)</c> clause.
    /// </summary>
    /// <param name="columnName">The full column name (may be qualified as <c>table.col</c>).</param>
    /// <param name="excludedColumns">The exclusion list, or <see langword="null"/> if no exclusion was specified.</param>
    /// <param name="qualifierPrefix">
    /// When non-null, the <c>table.</c> prefix stripped from <paramref name="columnName"/> before
    /// matching against <paramref name="excludedColumns"/>. Used for <c>SELECT table.* EXCEPT (...)</c>
    /// where the user specifies unqualified names in the exclusion list.
    /// </param>
    internal static bool IsExcluded(
        string columnName,
        IReadOnlyList<string>? excludedColumns,
        string? qualifierPrefix = null)
    {
        if (excludedColumns is null || excludedColumns.Count == 0)
            return false;

        // For unqualified wildcard (SELECT *), match the full column name
        // or just the unqualified portion after the dot.
        foreach (string excluded in excludedColumns)
        {
            if (columnName.Equals(excluded, StringComparison.OrdinalIgnoreCase))
                return true;

            // Match unqualified exclusion against qualified column: "id" matches "orders.id".
            if (qualifierPrefix is null)
            {
                int dotIndex = columnName.IndexOf('.');
                if (dotIndex >= 0
                    && columnName.AsSpan(dotIndex + 1).Equals(excluded.AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else
            {
                // For table.* EXCEPT (col), strip the prefix and match.
                if (columnName.StartsWith(qualifierPrefix, StringComparison.OrdinalIgnoreCase)
                    && columnName.AsSpan(qualifierPrefix.Length).Equals(excluded.AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Finds a replacement expression for a column name in a
    /// <c>SELECT * REPLACE (...)</c> or <c>SELECT table.* REPLACE (...)</c> clause.
    /// Uses the same qualified/unqualified matching logic as <see cref="IsExcluded"/>.
    /// </summary>
    /// <param name="columnName">The full column name (may be qualified as <c>table.col</c>).</param>
    /// <param name="replacedColumns">The replacement list, or <see langword="null"/> if no replacements were specified.</param>
    /// <param name="qualifierPrefix">
    /// When non-null, the <c>table.</c> prefix stripped from <paramref name="columnName"/> before
    /// matching against replacement column names.
    /// </param>
    /// <returns>The replacement expression if matched, or <see langword="null"/>.</returns>
    internal static Expression? FindReplacement(
        string columnName,
        IReadOnlyList<ColumnReplacement>? replacedColumns,
        string? qualifierPrefix = null)
    {
        if (replacedColumns is null || replacedColumns.Count == 0)
            return null;

        foreach (ColumnReplacement replaced in replacedColumns)
        {
            if (columnName.Equals(replaced.ColumnName, StringComparison.OrdinalIgnoreCase))
                return replaced.Expression;

            if (qualifierPrefix is null)
            {
                int dotIndex = columnName.IndexOf('.');
                if (dotIndex >= 0
                    && columnName.AsSpan(dotIndex + 1).Equals(replaced.ColumnName.AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    return replaced.Expression;
                }
            }
            else
            {
                if (columnName.StartsWith(qualifierPrefix, StringComparison.OrdinalIgnoreCase)
                    && columnName.AsSpan(qualifierPrefix.Length).Equals(replaced.ColumnName.AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    return replaced.Expression;
                }
            }
        }

        return null;
    }
}
