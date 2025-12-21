using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Functions.Window;

/// <summary>
/// Implements <c>NTH_VALUE(expression, n) [FROM FIRST | FROM LAST]
/// [IGNORE NULLS | RESPECT NULLS] OVER (...)</c>, which returns the value
/// of the expression evaluated at the Nth row within the current window
/// frame. <c>n</c> is 1-based. With <c>FROM LAST</c>, counting starts
/// from the end of the frame. Returns NULL when <c>n</c> exceeds the
/// frame size. With <c>IGNORE NULLS</c>, only non-null values are counted.
/// </summary>
public sealed class NthValueFunction : IWindowFunction
{
    /// <inheritdoc/>
    public string Name => "NTH_VALUE";

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("NTH_VALUE() requires exactly 2 arguments: (expression, n).");
        }

        if (!DataValue.IsIntegerKind(argumentKinds[1]))
        {
            throw new ArgumentException("NTH_VALUE() second argument (n) must be a numeric scalar.");
        }

        return argumentKinds[0];
    }

    /// <inheritdoc/>
    public IWindowComputation CreateComputation() => new NthValueComputation();

    private sealed class NthValueComputation : IWindowComputation
    {
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

            // Evaluate n (1-based) from the first row — it is a constant expression.
            DataValue nValue = await evaluator.EvaluateAsync(argumentExpressions[1], partitionRows[0], cancellationToken).ConfigureAwait(false);
            int n = WindowFunctionHelper.ToInt(nValue);

            if (n <= 0)
            {
                throw new ArgumentException("NTH_VALUE() requires n >= 1.");
            }

            // Derive the null kind from the source expression so empty-frame
            // and no-match nulls carry the correct type.
            DataValue sample = await evaluator.EvaluateAsync(argumentExpressions[0], partitionRows[0], cancellationToken).ConfigureAwait(false);
            DataValue typedNull = DataValue.Null(sample.Kind);

            for (int i = 0; i < partitionRows.Count; i++)
            {
                (int start, int end) = WindowFunctionHelper.ResolveFrameBounds(frame, i, partitionRows.Count);

                if (start > end)
                {
                    results[i] = typedNull;
                    continue;
                }

                results[i] = fromLast
                    ? await FindNthFromLastAsync(partitionRows, argumentExpressions, evaluator, start, end, n, nullHandling, typedNull, cancellationToken).ConfigureAwait(false)
                    : await FindNthFromFirstAsync(partitionRows, argumentExpressions, evaluator, start, end, n, nullHandling, typedNull, cancellationToken).ConfigureAwait(false);
            }
        }

        private static async ValueTask<DataValue> FindNthFromFirstAsync(
            IReadOnlyList<Row> partitionRows,
            IReadOnlyList<Expression> argumentExpressions,
            ExpressionEvaluator evaluator,
            int start,
            int end,
            int n,
            NullHandling nullHandling,
            DataValue typedNull,
            CancellationToken cancellationToken)
        {
            int count = 0;
            for (int j = start; j <= end; j++)
            {
                DataValue value = await evaluator.EvaluateAsync(argumentExpressions[0], partitionRows[j], cancellationToken).ConfigureAwait(false);

                if (nullHandling == NullHandling.IgnoreNulls && value.IsNull)
                {
                    continue;
                }

                count++;
                if (count == n)
                {
                    return value;
                }
            }

            return typedNull;
        }

        private static async ValueTask<DataValue> FindNthFromLastAsync(
            IReadOnlyList<Row> partitionRows,
            IReadOnlyList<Expression> argumentExpressions,
            ExpressionEvaluator evaluator,
            int start,
            int end,
            int n,
            NullHandling nullHandling,
            DataValue typedNull,
            CancellationToken cancellationToken)
        {
            int count = 0;
            for (int j = end; j >= start; j--)
            {
                DataValue value = await evaluator.EvaluateAsync(argumentExpressions[0], partitionRows[j], cancellationToken).ConfigureAwait(false);

                if (nullHandling == NullHandling.IgnoreNulls && value.IsNull)
                {
                    continue;
                }

                count++;
                if (count == n)
                {
                    return value;
                }
            }

            return typedNull;
        }
    }
}
