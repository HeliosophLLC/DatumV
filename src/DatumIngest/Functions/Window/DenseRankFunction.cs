using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Functions.Window;

/// <summary>
/// Implements <c>DENSE_RANK() OVER (...)</c>, which assigns a rank to each row
/// within a partition. Rows with equal ORDER BY values receive the same rank,
/// without gaps (e.g. 1, 1, 2, 3 instead of 1, 1, 3, 4).
/// </summary>
public sealed class DenseRankFunction : IWindowFunction
{
    /// <inheritdoc/>
    public string Name => "DENSE_RANK";

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 0)
        {
            throw new ArgumentException("DENSE_RANK() accepts no arguments.");
        }

        return DataKind.Float32;
    }

    /// <inheritdoc/>
    public IWindowComputation CreateComputation() => new DenseRankComputation();

    private sealed class DenseRankComputation : IWindowComputation
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

            int currentRank = 1;
            results[0] = DataValue.FromFloat32(currentRank);

            for (int i = 1; i < partitionRows.Count; i++)
            {
                bool isTie = await WindowFunctionHelper.AreOrderByValuesEqualAsync(
                    partitionRows[i - 1], partitionRows[i], orderByItems, evaluator, cancellationToken).ConfigureAwait(false);

                if (!isTie)
                {
                    currentRank++;
                }

                results[i] = DataValue.FromFloat32(currentRank);
            }
        }
    }
}
