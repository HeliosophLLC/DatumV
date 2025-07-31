using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Hash-based aggregation operator that groups input rows by one or more
/// key expressions and computes aggregate function results per group.
/// This is a blocking operator: all input rows must be consumed before
/// any output rows are emitted.
/// </summary>
public sealed class GroupByOperator : IQueryOperator
{
    private readonly IQueryOperator _source;
    private readonly IReadOnlyList<Expression> _groupByExpressions;
    private readonly IReadOnlyList<AggregateColumn> _aggregateColumns;

    /// <summary>
    /// Creates a GROUP BY operator.
    /// </summary>
    /// <param name="source">The child operator producing rows.</param>
    /// <param name="groupByExpressions">
    /// The GROUP BY key expressions. May be empty for global aggregation
    /// (e.g. <c>SELECT COUNT(*) FROM t</c>).
    /// </param>
    /// <param name="aggregateColumns">
    /// The aggregate function calls with their output column names.
    /// </param>
    public GroupByOperator(
        IQueryOperator source,
        IReadOnlyList<Expression> groupByExpressions,
        IReadOnlyList<AggregateColumn> aggregateColumns)
    {
        _source = source;
        _groupByExpressions = groupByExpressions;
        _aggregateColumns = aggregateColumns;
    }

    /// <summary>The child operator producing rows.</summary>
    public IQueryOperator Source => _source;

    /// <summary>The GROUP BY key expressions.</summary>
    public IReadOnlyList<Expression> GroupByExpressions => _groupByExpressions;

    /// <summary>The aggregate columns being computed.</summary>
    public IReadOnlyList<AggregateColumn> AggregateColumns => _aggregateColumns;

    /// <inheritdoc/>
    public async IAsyncEnumerable<Row> ExecuteAsync(ExecutionContext context)
    {
        ExpressionEvaluator evaluator = new(context.FunctionRegistry, context.QueryMeter, context.OuterRow);

        bool useSingleKey = _groupByExpressions.Count == 1;
        bool isGlobalAggregation = _groupByExpressions.Count == 0;

        // Each group maps to accumulators for all aggregate functions.
        Dictionary<DataValue, GroupState>? singleKeyGroups =
            useSingleKey ? new() : null;
        Dictionary<CompositeKey, GroupState>? compositeKeyGroups =
            !useSingleKey && !isGlobalAggregation ? new() : null;

        // For global aggregation (no GROUP BY), use a single group.
        GroupState? globalGroup = isGlobalAggregation ? CreateGroupState() : null;

        // Shared column schema for output rows (built on first output).
        string[]? outputNames = null;
        Dictionary<string, int>? outputNameIndex = null;

        await foreach (Row row in _source.ExecuteAsync(context).ConfigureAwait(false))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            context.QueryMeter?.ThrowIfExceeded();

            GroupState group;

            if (isGlobalAggregation)
            {
                group = globalGroup!;
            }
            else if (useSingleKey)
            {
                DataValue key = evaluator.Evaluate(_groupByExpressions[0], row);

                if (!singleKeyGroups!.TryGetValue(key, out group!))
                {
                    group = CreateGroupState();
                    group.KeyValues = [key];
                    singleKeyGroups[key] = group;
                }
            }
            else
            {
                DataValue[] parts = new DataValue[_groupByExpressions.Count];
                for (int index = 0; index < _groupByExpressions.Count; index++)
                {
                    parts[index] = evaluator.Evaluate(_groupByExpressions[index], row);
                }

                CompositeKey compositeKey = new(parts);

                if (!compositeKeyGroups!.TryGetValue(compositeKey, out group!))
                {
                    group = CreateGroupState();
                    group.KeyValues = parts;
                    compositeKeyGroups[compositeKey] = group;
                }
            }

            // Accumulate each aggregate's arguments for this row.
            for (int aggregateIndex = 0; aggregateIndex < _aggregateColumns.Count; aggregateIndex++)
            {
                AggregateColumn aggregateColumn = _aggregateColumns[aggregateIndex];

                if (aggregateColumn.IsCountStar)
                {
                    // COUNT(*) — no arguments to evaluate.
                    group.Accumulators[aggregateIndex].Accumulate(ReadOnlySpan<DataValue>.Empty);
                }
                else
                {
                    DataValue[] arguments = new DataValue[aggregateColumn.ArgumentExpressions.Count];
                    for (int argumentIndex = 0; argumentIndex < aggregateColumn.ArgumentExpressions.Count; argumentIndex++)
                    {
                        arguments[argumentIndex] = evaluator.Evaluate(
                            aggregateColumn.ArgumentExpressions[argumentIndex], row);
                    }
                    group.Accumulators[aggregateIndex].Accumulate(arguments);
                }
            }
        }

        // Emit one row per group.
        IEnumerable<GroupState> allGroups = isGlobalAggregation
            ? [globalGroup!]
            : useSingleKey
                ? singleKeyGroups!.Values
                : compositeKeyGroups!.Values;

        foreach (GroupState group in allGroups)
        {
            int outputFieldCount = _groupByExpressions.Count + _aggregateColumns.Count;
            DataValue[] values = new DataValue[outputFieldCount];

            // Build output column names once.
            if (outputNames is null)
            {
                outputNames = new string[outputFieldCount];

                for (int index = 0; index < _groupByExpressions.Count; index++)
                {
                    outputNames[index] = QueryExplainer.FormatExpression(_groupByExpressions[index]);
                }

                for (int index = 0; index < _aggregateColumns.Count; index++)
                {
                    outputNames[_groupByExpressions.Count + index] = _aggregateColumns[index].OutputName;
                }

                outputNameIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int index = 0; index < outputNames.Length; index++)
                {
                    outputNameIndex[outputNames[index]] = index;
                }
            }

            // GROUP BY key values.
            if (!isGlobalAggregation)
            {
                for (int index = 0; index < _groupByExpressions.Count; index++)
                {
                    values[index] = group.KeyValues![index];
                }
            }

            // Aggregate results.
            for (int index = 0; index < _aggregateColumns.Count; index++)
            {
                values[_groupByExpressions.Count + index] = group.Accumulators[index].Result;
            }

            yield return new Row(outputNames, values, outputNameIndex!);
        }
    }

    private GroupState CreateGroupState()
    {
        IAggregateAccumulator[] accumulators = new IAggregateAccumulator[_aggregateColumns.Count];
        for (int index = 0; index < _aggregateColumns.Count; index++)
        {
            accumulators[index] = _aggregateColumns[index].Function.CreateAccumulator();
        }
        return new GroupState { Accumulators = accumulators };
    }

    /// <summary>
    /// Mutable state for a single group during hash aggregation.
    /// </summary>
    private sealed class GroupState
    {
        /// <summary>The GROUP BY key values for this group (null for global aggregation).</summary>
        public DataValue[]? KeyValues;

        /// <summary>One accumulator per aggregate column.</summary>
        public IAggregateAccumulator[] Accumulators = [];
    }
}

/// <summary>
/// Describes a single aggregate function call in a GROUP BY query,
/// including the function, its argument expressions, and the output column name.
/// </summary>
/// <param name="Function">The aggregate function implementation.</param>
/// <param name="ArgumentExpressions">
/// The expressions to evaluate per row as arguments to the aggregate.
/// Empty for <c>COUNT(*)</c>.
/// </param>
/// <param name="OutputName">The output column name (e.g. <c>COUNT(*)</c>, <c>SUM(price)</c>).</param>
/// <param name="IsCountStar">Whether this is a <c>COUNT(*)</c> invocation with no arguments.</param>
public sealed record AggregateColumn(
    IAggregateFunction Function,
    IReadOnlyList<Expression> ArgumentExpressions,
    string OutputName,
    bool IsCountStar = false);
