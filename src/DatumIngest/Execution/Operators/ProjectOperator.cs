using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Evaluates SELECT column expressions against each incoming row,
/// producing output rows with the projected column set.
/// Expression results are wrapped in <see cref="LazyDataValue"/> thunks
/// so computation chains through nested SELECTs without eagerly materializing.
/// </summary>
public sealed class ProjectOperator : QueryOperator
{
    private readonly QueryOperator _source;
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
        QueryOperator source,
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
    public QueryOperator Source => _source;

    /// <summary>The projected SELECT columns.</summary>
    public IReadOnlyList<SelectColumn> Columns => _columns;

    /// <summary>The LET bindings evaluated before projection, or null if none.</summary>
    public IReadOnlyList<LetBinding>? LetBindings => _letBindings;

    /// <summary>The ASSERT clauses evaluated after LET bindings, or null if none.</summary>
    public IReadOnlyList<AssertClause>? Assertions => _assertions;

    /// <inheritdoc/>
    public override QueryOperator RewriteExpressions(Func<Expression, Expression> rewriter)
    {
        IReadOnlyList<SelectColumn> columns = _columns
            .Select(c => c with { Expression = rewriter(c.Expression) })
            .ToList();

        IReadOnlyList<LetBinding>? letBindings = _letBindings?
            .Select(b => b with { Expression = rewriter(b.Expression) })
            .ToList();

        IReadOnlyList<AssertClause>? assertions = _assertions?
            .Select(a => a with
            {
                Predicate = rewriter(a.Predicate),
                Message = a.Message is null ? null : rewriter(a.Message),
            })
            .ToList();

        return new ProjectOperator(
            _source.RewriteExpressions(rewriter),
            columns,
            letBindings,
            assertions,
            _sourceSchema);
    }

    /// <inheritdoc/>
    protected override OperatorPlanDescription DescribeForExplainImpl()
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
    protected override async IAsyncEnumerable<RowBatch> ExecuteAsyncImpl(ExecutionContext context)
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

        ExpressionEvaluator evaluator = context.CreateEvaluator(letBindingExpressions: letBindingExpressions);
        ProjectionSchema? schema = null;
        Pool pool = context.Pool;
        AssertionDiagnostics? assertionDiagnostics = context.AssertionDiagnostics;

        // Invariant: outputBatch != null ⟺ producer still owns it. Yielding transfers
        // ownership, so we null the local *before* yield. The outer finally cleans up
        // only the not-yet-yielded leftover, subsuming the previous bespoke per-row catch
        // (which only covered exceptions inside the projection block — an upstream throw
        // during the next MoveNextAsync would still have leaked the in-flight outputBatch).
        RowBatch? outputBatch = null;

        try
        {
            await foreach (RowBatch inputBatch in _source.ExecuteAsync(context).ConfigureAwait(false))
            {
                try
                {
                    // Source arena: where the input row's non-inline values live (input batch).
                    // Target arena: the OUTPUT batch's arena. Projected expression results
                    // (e.g. blur(image), substr(s, 1, 5)) materialize here so their offsets
                    // resolve against the same arena consumers see when they read batch.Arena.
                    // The output batch's lifetime extends until the downstream consumer is done
                    // with it, which is exactly the lifetime projected values need — using
                    // context.Store instead would make values outlive the batch they're in but
                    // would also strand consumers that resolve via batch.Arena (which is what
                    // FormatValue / GetImageHandle / AsByteSpan do).
                    IValueStore sourceArena = inputBatch.Arena;

                    for (int index = 0; index < inputBatch.Count; index++)
                    {
                        Row row = inputBatch[index];

                        schema ??= ProjectionSchema.Build(_columns, _letBindings, _assertions, row);
                        outputBatch ??= context.RentRowBatch(ProjectionSchema.BuildColumnLookup(schema));

                        EvaluationFrame frame = new(row, sourceArena, outputBatch.Arena, context, context.OuterRow);

                        DataValue[]? projected = await schema.ProjectAsync(
                            frame,
                            evaluator,
                            pool,
                            inputBatch.Arena,
                            outputBatch.Arena,
                            assertionDiagnostics,
                            context.CancellationToken).ConfigureAwait(false);

                        if (projected is null)
                            continue; // Row was skipped due to ASSERT … ON FAIL SKIP

                        outputBatch.Add(projected);

                        if (outputBatch.IsFull)
                        {
                            RowBatch toYield = outputBatch;
                            outputBatch = null;
                            yield return toYield;
                        }
                    }
                }
                finally
                {
                    context.ReturnRowBatch(inputBatch);
                }
            }

            if (outputBatch is not null)
            {
                RowBatch toYield = outputBatch;
                outputBatch = null;
                yield return toYield;
            }
        }
        finally
        {
            if (outputBatch is not null)
            {
                context.ReturnRowBatch(outputBatch);
            }
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
        // _augmentedLookup is null when there are no LET bindings; otherwise it covers
        // [source columns..., LET binding names...] and is used to construct the
        // augmented Row passed to the evaluator.
        private readonly ColumnLookup? _augmentedLookup;
        private readonly Expression[]? _letExpressions;
        private readonly int _sourceFieldCount;

        // ASSERT clause support: extracted from AssertClause records at build time.
        private readonly IReadOnlyList<AssertClause>? _assertions;

        private ProjectionSchema(
            string[] names,
            Dictionary<string, int> nameIndex,
            ProjectionSlot[] slots,
            ColumnLookup? augmentedLookup = null,
            Expression[]? letExpressions = null,
            int sourceFieldCount = 0,
            IReadOnlyList<AssertClause>? assertions = null)
        {
            _names = names;
            _nameIndex = nameIndex;
            _slots = slots;
            _augmentedLookup = augmentedLookup;
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
            ColumnLookup? augmentedLookup = null;
            Expression[]? letExpressions = null;

            if (letCount > 0)
            {
                string[] augmentedNames = new string[firstRow.FieldCount + letCount];
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

                augmentedLookup = new ColumnLookup(augmentedNames);
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
                            // Hidden planner-synthetic columns (LET/MIO hoists, etc.) carry a `__`
                            // prefix and must not surface through `*`. Without this filter,
                            // `SELECT *, models.foo(x)` re-emits the hoisted column once via `*`
                            // and a second time via the explicit projection.
                            if (IsHiddenColumnName(firstRow.ColumnNames[index]))
                                continue;
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
                                if (IsHiddenColumnName(columnName))
                                    continue;
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

                        // ColumnReference passthrough: resolve to a CopyOrdinal slot so the
                        // value flows through DataValueRetention.Stabilize at projection time
                        // (transferring arena-backed bytes into the output batch's arena).
                        // The Evaluate slot path would return the DataValue as-is — its
                        // _p0/_p1 still pointing into the input batch's arena — and silently
                        // produce a stale-pointer hazard once the value lands in an output
                        // batch whose arena owns none of those bytes.
                        if (column.Expression is ColumnReference colRef
                            && TryResolveColumnOrdinal(colRef, firstRow, out int columnOrdinal))
                        {
                            slots.Add(ProjectionSlot.CopyOrdinal(columnOrdinal));
                        }
                        else
                        {
                            slots.Add(ProjectionSlot.Evaluate(column.Expression));
                        }
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
                augmentedLookup, letExpressions, firstRow.FieldCount,
                assertions);
        }

        /// <summary>
        /// Builds a column lookup from the schema's output column names and name index.
        /// </summary>
        /// <param name="schema">The projection schema.</param>
        /// <returns>The column lookup.</returns>
        internal static ColumnLookup BuildColumnLookup(ProjectionSchema schema) =>
            new(schema._names, schema._nameIndex);

        /// <summary>
        /// Projects a source row using the pre-computed schema. When LET bindings
        /// are present, builds an augmented row with memoized LET values before
        /// evaluating projection expressions. The output <see cref="DataValue"/>
        /// array is rented from <paramref name="pool"/> and owned for the query
        /// lifetime — the row can be safely held across iterations.
        /// Returns <see langword="null"/> when an <c>ASSERT … ON FAIL SKIP</c>
        /// clause fails, signalling the caller to discard the row.
        /// </summary>
        internal async ValueTask<DataValue[]?> ProjectAsync(
            EvaluationFrame sourceFrame,
            ExpressionEvaluator evaluator,
            Pool pool,
            Arena sourceArena,
            Arena destinationArena,
            AssertionDiagnostics? diagnostics,
            CancellationToken cancellationToken)
        {
            if (_letExpressions is not null && _letExpressions.Length > 0)
            {
                return await ProjectWithLetBindingsAsync(
                    sourceFrame,
                    evaluator,
                    pool,
                    sourceArena,
                    destinationArena,
                    diagnostics,
                    cancellationToken).ConfigureAwait(false);
            }

            DataValue[]? values = null;

            try
            {
                Row sourceRow = sourceFrame.Row;
                values = pool.RentDataValues(_slots.Length);

                for (int index = 0; index < _slots.Length; index++)
                {
                    ProjectionSlot slot = _slots[index];

                    if (slot.SourceOrdinal >= 0)
                    {
                        values[index] = DataValueRetention.Stabilize(sourceRow[slot.SourceOrdinal], sourceArena, destinationArena);
                    }
                    else if (slot.Expression is not null)
                    {
                        values[index] = await evaluator.EvaluateAsync(slot.Expression, sourceFrame, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        throw new InvalidOperationException("Invalid projection slot: must copy from source ordinal or evaluate expression.");
                    }
                }

                if (_assertions is not null)
                {
                    foreach (AssertClause assertClause in _assertions)
                    {
                        bool passed = await evaluator.EvaluateAsBooleanAsync(assertClause.Predicate, sourceFrame, cancellationToken).ConfigureAwait(false);
                        if (!passed)
                        {
                            string? message = assertClause.Message is not null
                                ? (await evaluator.EvaluateAsValueRefAsync(assertClause.Message, sourceFrame, cancellationToken).ConfigureAwait(false)).AsString()
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

                return values;
            }
            catch
            {
                // Ensure that any rented values are returned to the pool on exceptions.
                if (values is not null)
                {
                    pool.ReturnDataValues(values);
                }

                throw;
            }
        }

        /// <summary>
        /// Projects a source row with LET bindings. Builds a temporary augmented
        /// row containing source columns plus LET binding values, evaluates each
        /// LET expression sequentially (so later bindings can reference earlier
        /// ones), then evaluates ASSERT clauses and projection expressions against
        /// the augmented row. Returns <see langword="null"/> when a SKIP assertion
        /// fails, signalling the caller to discard the row.
        /// </summary>
        private async ValueTask<DataValue[]?> ProjectWithLetBindingsAsync(
            EvaluationFrame sourceFrame,
            ExpressionEvaluator evaluator,
            Pool pool,
            Arena sourceArena,
            Arena destinationArena,
            AssertionDiagnostics? diagnostics,
            CancellationToken cancellationToken)
        {
            DataValue[] augmentedValues = new DataValue[_sourceFieldCount + _letExpressions!.Length];
            DataValue[]? values = null;
            Row sourceRow = sourceFrame.Row;

            try
            {
                // Build augmented values: source columns + LET binding slots.
                for (int index = 0; index < _sourceFieldCount; index++)
                {
                    augmentedValues[index] = sourceRow[index];
                }

                // The Row constructor stores the array by reference, so mutations
                // to augmentedValues are visible through the Row's indexers.
                Row augmentedRow = new(_augmentedLookup!, augmentedValues);
                EvaluationFrame augmentedFrame = sourceFrame.WithRow(augmentedRow);

                // Evaluate each LET binding sequentially. Each binding's result
                // is written into the augmented array before the next binding
                // is evaluated, enabling left-to-right chaining.
                for (int index = 0; index < _letExpressions.Length; index++)
                {
                    augmentedValues[_sourceFieldCount + index] =
                        await evaluator.EvaluateAsync(_letExpressions[index], augmentedFrame, cancellationToken).ConfigureAwait(false);
                }

                // Evaluate ASSERT clauses against the augmented row (source + LET values).
                if (_assertions is not null)
                {
                    foreach (AssertClause assertClause in _assertions)
                    {
                        bool passed = await evaluator.EvaluateAsBooleanAsync(assertClause.Predicate, augmentedFrame, cancellationToken).ConfigureAwait(false);
                        if (!passed)
                        {
                            string? message = assertClause.Message is not null
                                ? (await evaluator.EvaluateAsync(assertClause.Message, augmentedFrame, cancellationToken).ConfigureAwait(false)).ToString()
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
                values = pool.RentDataValues(_slots.Length);

                for (int index = 0; index < _slots.Length; index++)
                {
                    ProjectionSlot slot = _slots[index];

                    if (slot.SourceOrdinal >= 0)
                    {
                        values[index] = DataValueRetention.Stabilize(augmentedRow[slot.SourceOrdinal], sourceArena, destinationArena);
                    }
                    else if (slot.Expression is not null)
                    {
                        values[index] = await evaluator.EvaluateAsync(slot.Expression, augmentedFrame, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        throw new InvalidOperationException("Invalid projection slot: must copy from source ordinal or evaluate expression.");
                    }
                }

                return values;
            }
            catch
            {
                if (values is not null)
                {
                    // Return the augmented array to the pool if it was rented.
                    pool.ReturnDataValues(values);
                }

                throw;
            }
            finally
            {
                if (augmentedValues is not null)
                {
                    pool.ReturnDataValues(augmentedValues);
                }
            }
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
    /// Resolves a <see cref="ColumnReference"/> to its ordinal in <paramref name="row"/>.
    /// Tries the qualified name first (when present), then the unqualified column name —
    /// mirroring <c>ExpressionEvaluator.EvaluateColumn</c> so the projection plan stays
    /// in step with how the evaluator would resolve the same reference.
    /// </summary>
    /// <param name="column">The column reference being resolved.</param>
    /// <param name="row">A representative row whose schema defines the available column ordinals.</param>
    /// <param name="ordinal">On success, the matching column ordinal; on failure, <c>-1</c>.</param>
    /// <returns><see langword="true"/> when the reference resolves to a column in
    /// <paramref name="row"/>'s schema; <see langword="false"/> when no match is found
    /// (e.g. correlated outer reference or a name that only resolves at evaluation time).</returns>
    internal static bool TryResolveColumnOrdinal(
        ColumnReference column,
        Row row,
        out int ordinal)
    {
        IReadOnlyDictionary<string, int> nameIndex = row.ColumnLookup.NameIndex;

        if (column.QualifiedName is not null
            && nameIndex.TryGetValue(column.QualifiedName, out ordinal))
        {
            return true;
        }

        if (nameIndex.TryGetValue(column.ColumnName, out ordinal))
        {
            return true;
        }

        ordinal = -1;
        return false;
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
    /// True for planner-synthetic column names (LET hoists, model invocation outputs, etc.) that
    /// must not surface through wildcard expansion. The convention is a leading <c>__</c> on the
    /// unqualified portion of the name; explicit projections may still reference these names.
    /// </summary>
    internal static bool IsHiddenColumnName(string columnName)
    {
        int dotIndex = columnName.IndexOf('.');
        ReadOnlySpan<char> unqualified = dotIndex >= 0
            ? columnName.AsSpan(dotIndex + 1)
            : columnName.AsSpan();
        return unqualified.StartsWith("__");
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
