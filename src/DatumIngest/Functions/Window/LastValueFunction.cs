using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Functions.Window;

/// <summary>
/// Implements <c>LAST_VALUE(expression) [IGNORE NULLS | RESPECT NULLS] OVER (...)</c>,
/// which returns the value of the expression evaluated at the last row within
/// the current window frame. With <c>IGNORE NULLS</c>, the last non-null
/// value in the frame is returned instead.
/// </summary>
public sealed class LastValueFunction : IWindowFunction
{
    /// <inheritdoc/>
    public string Name => "LAST_VALUE";

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("LAST_VALUE() requires exactly 1 argument: (expression).");
        }

        return argumentKinds[0];
    }

    /// <inheritdoc/>
    public IWindowComputation CreateComputation() => new LastValueComputation();

    private sealed class LastValueComputation : IWindowComputation
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
            for (int i = 0; i < partitionRows.Count; i++)
            {
                (int start, int end) = WindowFunctionHelper.ResolveFrameBounds(frame, i, partitionRows.Count);

                if (start > end)
                {
                    results[i] = DataValue.Null(DataKind.Scalar);
                    continue;
                }

                if (nullHandling == NullHandling.IgnoreNulls)
                {
                    DataValue found = DataValue.Null(DataKind.Scalar);
                    for (int j = end; j >= start; j--)
                    {
                        DataValue candidate = evaluator.Evaluate(argumentExpressions[0], partitionRows[j]);
                        if (!candidate.IsNull)
                        {
                            found = candidate;
                            break;
                        }
                    }

                    results[i] = found;
                }
                else
                {
                    results[i] = evaluator.Evaluate(argumentExpressions[0], partitionRows[end]);
                }
            }
        }
    }
}
