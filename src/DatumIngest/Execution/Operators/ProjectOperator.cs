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
    private readonly IReadOnlyList<AssertClause>? _assertions;
    private readonly Schema? _sourceSchema;

    /// <summary>
    /// Creates a project operator.
    /// </summary>
    /// <param name="source">The child operator producing rows.</param>
    /// <param name="columns">The SELECT columns to project.</param>
    /// <param name="letBindings">Optional LET bindings to evaluate before projection.</param>
    /// <param name="assertions">Optional ASSERT clauses to evaluate after LET bindings.</param>
    /// <param name="sourceSchema">Optional source schema for star expansion.</param>
    public ProjectOperator(
        IQueryOperator source,
        IReadOnlyList<SelectColumn> columns,
        IReadOnlyList<LetBinding>? letBindings = null,
        IReadOnlyList<AssertClause>? assertions = null,
        Schema? sourceSchema = null)
    {
        _source = source;
        _columns = columns;
        _letBindings = letBindings;
        _assertions = assertions;
        _sourceSchema = sourceSchema;
    }

    /// <summary>The child operator producing rows.</summary>
    public IQueryOperator Source => _source;

    /// <summary>The projected SELECT columns.</summary>
    public IReadOnlyList<SelectColumn> Columns => _columns;

    /// <summary>The LET bindings evaluated before projection, or null if none.</summary>
    public IReadOnlyList<LetBinding>? LetBindings => _letBindings;

    /// <summary>The ASSERT clauses evaluated after LET bindings, or null if none.</summary>
    public IReadOnlyList<AssertClause>? Assertions => _assertions;

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

        if (_assertions is { Count: > 0 })
        {
            properties["assert"] = _assertions.Count.ToString();
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
        // Build a name→expression map for LET bindings so the evaluator can recover struct
        // field metadata from binding expressions (e.g., hidden __destructure_N bindings
        // produced by named destructuring desugaring whose original RHS is a struct literal).
        IReadOnlyDictionary<string, Expression>? letBindingExpressions = null;
        if (_letBindings is not null)
        {
            Dictionary<string, Expression> map = new(_letBindings.Count, StringComparer.OrdinalIgnoreCase);
            foreach (LetBinding b in _letBindings)
            {
                map[b.Name] = b.Expression;
            }
            letBindingExpressions = map;
        }

        ExpressionEvaluator evaluator = new(context.FunctionRegistry, context.QueryMeter, context.OuterRow, letBindingExpressions: letBindingExpressions, store: context.Store);
        ProjectionSchema? schema = null;
        LocalBufferPool pool = context.LocalBufferPool;
        AssertionDiagnostics? assertionDiagnostics = context.AssertionDiagnostics;

        await foreach (RowBatch inputBatch in _source.ExecuteAsync(context).ConfigureAwait(false))
        {
            RowBatch outputBatch = pool.RentBatch(inputBatch.Count);

            for (int index = 0; index < inputBatch.Count; index++)
            {
                Row row = inputBatch[index];
                schema ??= ProjectionSchema.Build(_columns, _letBindings, _assertions, row);
                Row? projected = schema.Project(row, evaluator, pool, assertionDiagnostics);
                if (projected.HasValue)
                {
                    outputBatch.Add(projected.Value);
                }
            }

            pool.ReturnBatch(inputBatch);
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

        // ASSERT clause support: extracted from AssertClause records at build time.
        private readonly IReadOnlyList<AssertClause>? _assertions;

        private ProjectionSchema(
            string[] names,
            Dictionary<string, int> nameIndex,
            ProjectionSlot[] slots,
            string[]? augmentedNames = null,
            Dictionary<string, int>? augmentedNameIndex = null,
            Expression[]? letExpressions = null,
            int sourceFieldCount = 0,
            IReadOnlyList<AssertClause>? assertions = null)
        {
            _names = names;
            _nameIndex = nameIndex;
            _slots = slots;
            _augmentedNames = augmentedNames;
            _augmentedNameIndex = augmentedNameIndex;
            _letExpressions = letExpressions;
            _sourceFieldCount = sourceFieldCount;
            _assertions = assertions;
        }

        /// <summary>
        /// Builds the projection schema from the column list, optional LET bindings,
        /// optional ASSERT clauses, and the first source row. Star and table-star columns
        /// are expanded using the source row's schema; named expressions record their
        /// expression for per-row evaluation. LET bindings produce an augmented row layout
        /// so their values are memoized and accessible by name during projection.
        /// </summary>
        internal static ProjectionSchema Build(
            IReadOnlyList<SelectColumn> columns,
            IReadOnlyList<LetBinding>? letBindings,
            IReadOnlyList<AssertClause>? assertions,
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
                                // User-written SELECT t.* strips the qualifier so output names are
                                // unqualified (standard SQL semantics). QualifyOutput is set by
                                // the query planner when expanding SELECT * in multi-join contexts
                                // where qualified names are required for disambiguation.
                                string outputName = !tableColumns.QualifyOutput
                                    && columnName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                    ? columnName[prefix.Length..]
                                    : columnName;
                                names.Add(outputName);
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
                augmentedNames, augmentedNameIndex, letExpressions, firstRow.FieldCount,
                assertions);
        }

        /// <summary>
        /// Projects a source row using the pre-computed schema. When LET bindings
        /// are present, builds an augmented row with memoized LET values before
        /// evaluating projection expressions. The output <see cref="DataValue"/>
        /// array is rented from <paramref name="pool"/> and owned for the query
        /// lifetime — the row can be safely held across iterations.
        /// Returns <see langword="null"/> when an <c>ASSERT … ON FAIL SKIP</c>
        /// clause fails, signalling the caller to discard the row.
        /// </summary>
        internal Row? Project(Row sourceRow, ExpressionEvaluator evaluator, LocalBufferPool pool, AssertionDiagnostics? diagnostics)
        {
            if (_letExpressions is not null && _letExpressions.Length > 0)
            {
                return ProjectWithLetBindings(sourceRow, evaluator, pool, diagnostics);
            }

            DataValue[] values = pool.Rent(_slots.Length);

            for (int index = 0; index < _slots.Length; index++)
            {
                ProjectionSlot slot = _slots[index];
                values[index] = slot.SourceOrdinal >= 0
                    ? sourceRow[slot.SourceOrdinal]
                    : evaluator.Evaluate(slot.Expression!, sourceRow);
            }

            if (_assertions is not null)
            {
                foreach (AssertClause assertClause in _assertions)
                {
                    bool passed = evaluator.EvaluateAsBoolean(assertClause.Predicate, sourceRow);
                    if (!passed)
                    {
                        string? message = assertClause.Message is not null
                            ? evaluator.Evaluate(assertClause.Message, sourceRow).ToString()
                            : null;
                        switch (assertClause.FailureMode)
                        {
                            case AssertFailureMode.Skip:
                                diagnostics?.RecordSkip(message);
                                return null;
                            case AssertFailureMode.Warn:
                                diagnostics?.RecordWarn(message);
                                break;
                            default:
                                throw new AssertionAbortException(message, assertClause.Span);
                        }
                    }
                }
            }

            return new Row(_names, values, _nameIndex);
        }

        /// <summary>
        /// Projects a source row with LET bindings. Builds a temporary augmented
        /// row containing source columns plus LET binding values, evaluates each
        /// LET expression sequentially (so later bindings can reference earlier
        /// ones), then evaluates ASSERT clauses and projection expressions against
        /// the augmented row. Returns <see langword="null"/> when a SKIP assertion
        /// fails, signalling the caller to discard the row.
        /// </summary>
        private Row? ProjectWithLetBindings(Row sourceRow, ExpressionEvaluator evaluator, LocalBufferPool pool, AssertionDiagnostics? diagnostics)
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

            // Evaluate ASSERT clauses against the augmented row (source + LET values).
            if (_assertions is not null)
            {
                foreach (AssertClause assertClause in _assertions)
                {
                    bool passed = evaluator.EvaluateAsBoolean(assertClause.Predicate, augmentedRow);
                    if (!passed)
                    {
                        string? message = assertClause.Message is not null
                            ? evaluator.Evaluate(assertClause.Message, augmentedRow).ToString()
                            : null;
                        switch (assertClause.FailureMode)
                        {
                            case AssertFailureMode.Skip:
                                diagnostics?.RecordSkip(message);
                                return null;
                            case AssertFailureMode.Warn:
                                diagnostics?.RecordWarn(message);
                                break;
                            default:
                                throw new AssertionAbortException(message, assertClause.Span);
                        }
                    }
                }
            }

            // Evaluate output slots against the augmented row.
            DataValue[] values = pool.Rent(_slots.Length);
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
