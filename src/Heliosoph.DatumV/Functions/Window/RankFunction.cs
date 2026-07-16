using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Functions.Window;

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

        return DataKind.Int64;
    }

    /// <inheritdoc/>
    public IWindowComputation CreateComputation() => new RankComputation();

    private sealed class RankComputation : IWindowComputation
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

            results[0] = DataValue.FromInt64(1);

            for (int i = 1; i < partitionRows.Count; i++)
            {
                bool isTie = await WindowFunctionHelper.AreOrderByValuesEqualAsync(
                    partitionRows[i - 1], partitionRows[i], orderByItems, evaluator, cancellationToken).ConfigureAwait(false);
                results[i] = isTie ? results[i - 1] : DataValue.FromInt64(i + 1);
            }
        }
    }
}
