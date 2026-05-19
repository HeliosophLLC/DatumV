using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Functions.Window;

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
            // Derive the null kind from the source expression so empty-frame
            // and IGNORE NULLS nulls carry the correct type.
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

                if (nullHandling == NullHandling.IgnoreNulls)
                {
                    DataValue found = typedNull;
                    for (int j = end; j >= start; j--)
                    {
                        DataValue candidate = await evaluator.EvaluateAsync(argumentExpressions[0], partitionRows[j], cancellationToken).ConfigureAwait(false);
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
                    results[i] = await evaluator.EvaluateAsync(argumentExpressions[0], partitionRows[end], cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }
}
