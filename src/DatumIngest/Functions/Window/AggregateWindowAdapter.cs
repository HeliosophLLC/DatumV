using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Functions.Window;

/// <summary>
/// Adapts an <see cref="IAggregateFunction"/> to behave as an <see cref="IWindowFunction"/>,
/// enabling aggregate functions (COUNT, SUM, AVG, MIN, MAX) to be used with OVER clauses.
/// <para>
/// For frames starting at UNBOUNDED PRECEDING, uses a single advancing accumulator
/// (running aggregate) for O(n) computation. For arbitrary ROWS frames, recomputes
/// the aggregate per row over the frame window.
/// </para>
/// </summary>
public sealed class AggregateWindowAdapter : IWindowFunction
{
    private readonly IAggregateFunction _aggregate;

    /// <summary>
    /// Creates a window function adapter for the given aggregate function.
    /// </summary>
    /// <param name="aggregate">The aggregate function to wrap.</param>
    public AggregateWindowAdapter(IAggregateFunction aggregate)
    {
        _aggregate = aggregate;
    }

    /// <inheritdoc/>
    public string Name => _aggregate.Name;

    /// <inheritdoc/>
    public int QueryUnitCost => _aggregate.QueryUnitCost;

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        return _aggregate.ValidateArguments(argumentKinds);
    }

    /// <inheritdoc/>
    public IWindowComputation CreateComputation() => new AggregateWindowComputation(_aggregate);

    private sealed class AggregateWindowComputation : IWindowComputation
    {
        private readonly IAggregateFunction _aggregate;

        internal AggregateWindowComputation(IAggregateFunction aggregate)
        {
            _aggregate = aggregate;
        }

        public void Compute(
            IReadOnlyList<Row> partitionRows,
            IReadOnlyList<Expression> argumentExpressions,
            ExpressionEvaluator evaluator,
            IReadOnlyList<OrderByItem>? orderByItems,
            WindowFrame? frame,
            DataValue[] results)
        {
            if (partitionRows.Count == 0)
            {
                return;
            }

            // Optimization: running aggregate for UNBOUNDED PRECEDING start frames.
            if (frame is null || frame.Start is UnboundedPrecedingBound)
            {
                ComputeRunningAggregate(partitionRows, argumentExpressions, evaluator, frame, results);
            }
            else
            {
                ComputeSlidingAggregate(partitionRows, argumentExpressions, evaluator, frame, results);
            }
        }

        /// <summary>
        /// Fast path: single accumulator advancing forward for frames starting
        /// at UNBOUNDED PRECEDING (running SUM, running COUNT, etc.).
        /// </summary>
        private void ComputeRunningAggregate(
            IReadOnlyList<Row> partitionRows,
            IReadOnlyList<Expression> argumentExpressions,
            ExpressionEvaluator evaluator,
            WindowFrame? frame,
            DataValue[] results)
        {
            // When no frame is specified and no ORDER BY, the whole partition
            // is the frame for every row — use whole-partition aggregate.
            if (frame is null && partitionRows.Count > 0)
            {
                IAggregateAccumulator wholeAccumulator = _aggregate.CreateAccumulator();
                DataValue[] argumentBuffer = new DataValue[argumentExpressions.Count];

                for (int i = 0; i < partitionRows.Count; i++)
                {
                    EvaluateArguments(argumentExpressions, evaluator, partitionRows[i], argumentBuffer);
                    wholeAccumulator.Accumulate(argumentBuffer);
                }

                DataValue wholeResult = wholeAccumulator.Result;
                for (int i = 0; i < partitionRows.Count; i++)
                {
                    results[i] = wholeResult;
                }

                return;
            }

            // Frame with UNBOUNDED PRECEDING start: accumulate row by row,
            // snapshot result when the current row is within the frame end.
            IAggregateAccumulator accumulator = _aggregate.CreateAccumulator();
            DataValue[] arguments = new DataValue[argumentExpressions.Count];

            for (int i = 0; i < partitionRows.Count; i++)
            {
                EvaluateArguments(argumentExpressions, evaluator, partitionRows[i], arguments);
                accumulator.Accumulate(arguments);

                (int _, int end) = WindowFunctionHelper.ResolveFrameBounds(frame, i, partitionRows.Count);

                // For UNBOUNDED PRECEDING + X FOLLOWING or CURRENT ROW end,
                // the accumulator already contains rows 0..i. If end >= i, output is valid.
                // If end < i, the frame has shrunk past the accumulator — fall back to recompute.
                if (end >= i)
                {
                    results[i] = accumulator.Result;
                }
                else
                {
                    // Degenerate case: end < current index. Recompute from scratch.
                    results[i] = RecomputeForRow(partitionRows, argumentExpressions, evaluator, frame, i);
                }
            }
        }

        /// <summary>
        /// Slow path: recompute the aggregate for each row over its frame window.
        /// Used when the frame start is not UNBOUNDED PRECEDING.
        /// </summary>
        private void ComputeSlidingAggregate(
            IReadOnlyList<Row> partitionRows,
            IReadOnlyList<Expression> argumentExpressions,
            ExpressionEvaluator evaluator,
            WindowFrame frame,
            DataValue[] results)
        {
            for (int i = 0; i < partitionRows.Count; i++)
            {
                results[i] = RecomputeForRow(partitionRows, argumentExpressions, evaluator, frame, i);
            }
        }

        private DataValue RecomputeForRow(
            IReadOnlyList<Row> partitionRows,
            IReadOnlyList<Expression> argumentExpressions,
            ExpressionEvaluator evaluator,
            WindowFrame? frame,
            int currentIndex)
        {
            (int start, int end) = WindowFunctionHelper.ResolveFrameBounds(frame, currentIndex, partitionRows.Count);
            IAggregateAccumulator accumulator = _aggregate.CreateAccumulator();

            DataValue[] arguments = new DataValue[argumentExpressions.Count];

            for (int j = start; j <= end; j++)
            {
                EvaluateArguments(argumentExpressions, evaluator, partitionRows[j], arguments);
                accumulator.Accumulate(arguments);
            }

            return accumulator.Result;
        }

        private static void EvaluateArguments(
            IReadOnlyList<Expression> argumentExpressions,
            ExpressionEvaluator evaluator,
            Row row,
            Span<DataValue> buffer)
        {
            for (int i = 0; i < argumentExpressions.Count; i++)
            {
                buffer[i] = evaluator.Evaluate(argumentExpressions[i], row);
            }
        }
    }
}
