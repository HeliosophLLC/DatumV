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

        if (argumentKinds[1] != DataKind.Float32)
        {
            throw new ArgumentException("NTH_VALUE() second argument (n) must be a numeric scalar.");
        }

        return argumentKinds[0];
    }

    /// <inheritdoc/>
    public IWindowComputation CreateComputation() => new NthValueComputation();

    private sealed class NthValueComputation : IWindowComputation
    {
        public void Compute(
            IReadOnlyList<Row> partitionRows,
            IReadOnlyList<Expression> argumentExpressions,
            ExpressionEvaluator evaluator,
            IReadOnlyList<OrderByItem>? orderByItems,
            WindowFrame? frame,
            DataValue[] results,
            NullHandling nullHandling = NullHandling.RespectNulls,
            bool fromLast = false)
        {
            if (partitionRows.Count == 0)
            {
                return;
            }

            // Evaluate n (1-based) from the first row — it is a constant expression.
            DataValue nValue = evaluator.Evaluate(argumentExpressions[1], partitionRows[0]);
            int n = WindowFunctionHelper.ToInt(nValue);

            if (n <= 0)
            {
                throw new ArgumentException("NTH_VALUE() requires n >= 1.");
            }

            for (int i = 0; i < partitionRows.Count; i++)
            {
                (int start, int end) = WindowFunctionHelper.ResolveFrameBounds(frame, i, partitionRows.Count);

                if (start > end)
                {
                    results[i] = DataValue.Null(DataKind.Float32);
                    continue;
                }

                results[i] = fromLast
                    ? FindNthFromLast(partitionRows, argumentExpressions, evaluator, start, end, n, nullHandling)
                    : FindNthFromFirst(partitionRows, argumentExpressions, evaluator, start, end, n, nullHandling);
            }
        }

        private static DataValue FindNthFromFirst(
            IReadOnlyList<Row> partitionRows,
            IReadOnlyList<Expression> argumentExpressions,
            ExpressionEvaluator evaluator,
            int start,
            int end,
            int n,
            NullHandling nullHandling)
        {
            int count = 0;
            for (int j = start; j <= end; j++)
            {
                DataValue value = evaluator.Evaluate(argumentExpressions[0], partitionRows[j]);

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

            return DataValue.Null(DataKind.Float32);
        }

        private static DataValue FindNthFromLast(
            IReadOnlyList<Row> partitionRows,
            IReadOnlyList<Expression> argumentExpressions,
            ExpressionEvaluator evaluator,
            int start,
            int end,
            int n,
            NullHandling nullHandling)
        {
            int count = 0;
            for (int j = end; j >= start; j--)
            {
                DataValue value = evaluator.Evaluate(argumentExpressions[0], partitionRows[j]);

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

            return DataValue.Null(DataKind.Float32);
        }
    }
}
