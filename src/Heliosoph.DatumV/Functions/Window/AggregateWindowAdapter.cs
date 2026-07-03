using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Functions.Window;

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

        public async ValueTask ComputeAsync(
            IReadOnlyList<Row> partitionRows,
            IReadOnlyList<Expression> argumentExpressions,
            ExpressionEvaluator evaluator,
            IReadOnlyList<OrderByItem>? orderByItems,
            WindowFrame? frame,
            DataValue[] results,
            NullHandling nullHandling = NullHandling.RespectNulls,
            bool fromLast = false,
            CancellationToken cancellationToken = default)
        {
            if (partitionRows.Count == 0)
            {
                return;
            }

            // Optimization: running aggregate for UNBOUNDED PRECEDING start frames.
            if (frame is null || frame.Start is UnboundedPrecedingBound)
            {
                await ComputeRunningAggregateAsync(partitionRows, argumentExpressions, evaluator, frame, results, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await ComputeSlidingAggregateAsync(partitionRows, argumentExpressions, evaluator, frame, results, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Fast path: single accumulator advancing forward for frames starting
        /// at UNBOUNDED PRECEDING (running SUM, running COUNT, etc.).
        /// </summary>
        private async ValueTask ComputeRunningAggregateAsync(
            IReadOnlyList<Row> partitionRows,
            IReadOnlyList<Expression> argumentExpressions,
            ExpressionEvaluator evaluator,
            WindowFrame? frame,
            DataValue[] results,
            CancellationToken cancellationToken)
        {
            // The evaluator's store doubles as both Source and Target for window-aggregate
            // accumulator state. The window operator buffers its partition rows in memory
            // before calling Compute, so a single per-query store keeps things simple
            // without per-batch arena bookkeeping.
            InvocationFrame frameInv = BuildFrame(evaluator);

            // When no frame is specified and no ORDER BY, the whole partition
            // is the frame for every row — use whole-partition aggregate.
            if (frame is null && partitionRows.Count > 0)
            {
                IAggregateAccumulator wholeAccumulator = _aggregate.CreateAccumulator();
                DataValue[] argumentBuffer = new DataValue[argumentExpressions.Count];

                for (int i = 0; i < partitionRows.Count; i++)
                {
                    await EvaluateArgumentsAsync(argumentExpressions, evaluator, partitionRows[i], argumentBuffer, cancellationToken).ConfigureAwait(false);
                    wholeAccumulator.Accumulate(argumentBuffer, in frameInv);
                }

                DataValue wholeResult = await wholeAccumulator.ResultAsync(frameInv).ConfigureAwait(false);
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
                await EvaluateArgumentsAsync(argumentExpressions, evaluator, partitionRows[i], arguments, cancellationToken).ConfigureAwait(false);
                accumulator.Accumulate(arguments, in frameInv);

                (int _, int end) = WindowFunctionHelper.ResolveFrameBounds(frame, i, partitionRows.Count);

                // For UNBOUNDED PRECEDING + X FOLLOWING or CURRENT ROW end,
                // the accumulator already contains rows 0..i. If end >= i, output is valid.
                // If end < i, the frame has shrunk past the accumulator — fall back to recompute.
                if (end >= i)
                {
                    results[i] = await accumulator.ResultAsync(frameInv).ConfigureAwait(false);
                }
                else
                {
                    // Degenerate case: end < current index. Recompute from scratch.
                    results[i] = await RecomputeForRowAsync(partitionRows, argumentExpressions, evaluator, frame, i, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Slow path: recompute the aggregate for each row over its frame window.
        /// Used when the frame start is not UNBOUNDED PRECEDING.
        /// </summary>
        private async ValueTask ComputeSlidingAggregateAsync(
            IReadOnlyList<Row> partitionRows,
            IReadOnlyList<Expression> argumentExpressions,
            ExpressionEvaluator evaluator,
            WindowFrame frame,
            DataValue[] results,
            CancellationToken cancellationToken)
        {
            for (int i = 0; i < partitionRows.Count; i++)
            {
                results[i] = await RecomputeForRowAsync(partitionRows, argumentExpressions, evaluator, frame, i, cancellationToken).ConfigureAwait(false);
            }
        }

        private async ValueTask<DataValue> RecomputeForRowAsync(
            IReadOnlyList<Row> partitionRows,
            IReadOnlyList<Expression> argumentExpressions,
            ExpressionEvaluator evaluator,
            WindowFrame? frame,
            int currentIndex,
            CancellationToken cancellationToken)
        {
            (int start, int end) = WindowFunctionHelper.ResolveFrameBounds(frame, currentIndex, partitionRows.Count);
            InvocationFrame frameInv = BuildFrame(evaluator);
            IAggregateAccumulator accumulator = _aggregate.CreateAccumulator();

            DataValue[] arguments = new DataValue[argumentExpressions.Count];

            for (int j = start; j <= end; j++)
            {
                await EvaluateArgumentsAsync(argumentExpressions, evaluator, partitionRows[j], arguments, cancellationToken).ConfigureAwait(false);
                accumulator.Accumulate(arguments, in frameInv);
            }

            return await accumulator.ResultAsync(frameInv).ConfigureAwait(false);
        }

        private static async ValueTask EvaluateArgumentsAsync(
            IReadOnlyList<Expression> argumentExpressions,
            ExpressionEvaluator evaluator,
            Row row,
            DataValue[] buffer,
            CancellationToken cancellationToken)
        {
            for (int i = 0; i < argumentExpressions.Count; i++)
            {
                buffer[i] = await evaluator.EvaluateAsync(argumentExpressions[i], row, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Pulls the evaluator's per-query store and builds a symmetric
        /// <see cref="InvocationFrame"/>. The window operator buffers all partition
        /// rows in memory before computation, so a single store works for both reading
        /// arena-backed argument values and writing accumulator state.
        /// </summary>
        private static InvocationFrame BuildFrame(ExpressionEvaluator evaluator)
        {
            IValueStore store = evaluator.Store
                ?? throw new InvalidOperationException(
                    "AggregateWindowAdapter requires the evaluator to be constructed with an IValueStore.");
            return InvocationFrame.Symmetric(store, evaluator.Context.SidecarRegistry, evaluator.Types);
        }
    }
}
