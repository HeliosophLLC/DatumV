using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Functions.Window;

/// <summary>
/// Implements <c>RANK() OVER (...)</c>, which assigns a rank to each row
/// within a partition. Rows with equal ORDER BY values receive the same rank,
/// with gaps after ties (e.g. 1, 1, 3, 4).
/// </summary>
public sealed class RankFunction : IWindowFunction
{
    /// <inheritdoc/>
    public string Name => "RANK";

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 0)
        {
            throw new ArgumentException("RANK() accepts no arguments.");
        }

        return DataKind.Float32;
    }

    /// <inheritdoc/>
    public IWindowComputation CreateComputation() => new RankComputation();

    private sealed class RankComputation : IWindowComputation
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

            results[0] = DataValue.FromFloat32(1);

            for (int i = 1; i < partitionRows.Count; i++)
            {
                bool isTie = WindowFunctionHelper.AreOrderByValuesEqual(
                    partitionRows[i - 1], partitionRows[i], orderByItems, evaluator);
                results[i] = isTie ? results[i - 1] : DataValue.FromFloat32(i + 1);
            }
        }
    }
}
