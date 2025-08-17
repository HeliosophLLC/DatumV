using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Functions.Window;

/// <summary>
/// Implements <c>LEAD(expression [, offset [, default]]) OVER (...)</c>, which
/// returns the value of an expression evaluated at a following row within the
/// partition. The default offset is 1 (next row). Returns
/// the default value (or NULL) when the offset falls outside the partition.
/// </summary>
public sealed class LeadFunction : IWindowFunction
{
    /// <inheritdoc/>
    public string Name => "LEAD";

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is 0 or > 3)
        {
            throw new ArgumentException("LEAD() requires 1 to 3 arguments: (expression [, offset [, default]]).");
        }

        if (argumentKinds.Length >= 2 && argumentKinds[1] != DataKind.Float32)
        {
            throw new ArgumentException("LEAD() offset argument must be a numeric scalar.");
        }

        return argumentKinds[0];
    }

    /// <inheritdoc/>
    public IWindowComputation CreateComputation() => new LeadComputation();

    private sealed class LeadComputation : IWindowComputation
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
                offset = (int)offsetValue.AsFloat32();
            }

            // Determine default value (default NULL).
            DataValue defaultValue = DataValue.Null(DataKind.Float32);
            if (argumentExpressions.Count >= 3)
            {
                defaultValue = evaluator.Evaluate(argumentExpressions[2], partitionRows[0]);
            }

            for (int i = 0; i < partitionRows.Count; i++)
            {
                int sourceIndex = i + offset;
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
