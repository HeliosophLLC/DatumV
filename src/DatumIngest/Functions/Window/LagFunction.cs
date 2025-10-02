using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Functions.Window;

/// <summary>
/// Implements <c>LAG(expression [, offset [, default]]) OVER (...)</c>, which
/// returns the value of an expression evaluated at a preceding row within the
/// partition. The default offset is 1 (previous row). Returns
/// the default value (or NULL) when the offset falls outside the partition.
/// </summary>
public sealed class LagFunction : IWindowFunction
{
    /// <inheritdoc/>
    public string Name => "LAG";

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is 0 or > 3)
        {
            throw new ArgumentException("LAG() requires 1 to 3 arguments: (expression [, offset [, default]]).");
        }

        if (argumentKinds.Length >= 2 && !DataValue.IsIntegerKind(argumentKinds[1]))
        {
            throw new ArgumentException("LAG() offset argument must be a numeric scalar.");
        }

        return argumentKinds[0];
    }

    /// <inheritdoc/>
    public IWindowComputation CreateComputation() => new LagComputation();

    private sealed class LagComputation : IWindowComputation
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
            // Determine offset (default 1).
            int offset = 1;
            if (argumentExpressions.Count >= 2)
            {
                DataValue offsetValue = evaluator.Evaluate(argumentExpressions[1], partitionRows[0]);
                offset = WindowFunctionHelper.ToInt(offsetValue);
            }

            // Determine default value (default: typed NULL matching the source expression).
            DataValue defaultValue = DataValue.Null(
                evaluator.Evaluate(argumentExpressions[0], partitionRows[0]).Kind);
            if (argumentExpressions.Count >= 3)
            {
                defaultValue = evaluator.Evaluate(argumentExpressions[2], partitionRows[0]);
            }

            for (int i = 0; i < partitionRows.Count; i++)
            {
                int sourceIndex = i - offset;
                if (sourceIndex >= 0 && sourceIndex < partitionRows.Count)
                {
                    results[i] = evaluator.Evaluate(argumentExpressions[0], partitionRows[sourceIndex]);
                }
                else
                {
                    results[i] = defaultValue;
                }
            }
        }
    }
}
