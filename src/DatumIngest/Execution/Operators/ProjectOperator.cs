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
                    case SelectAllColumns:
                        for (int index = 0; index < firstRow.FieldCount; index++)
                        {
                            names.Add(firstRow.ColumnNames[index]);
                            slots.Add(ProjectionSlot.CopyOrdinal(index));
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
                                names.Add(columnName);
                                slots.Add(ProjectionSlot.CopyOrdinal(index));
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
}
