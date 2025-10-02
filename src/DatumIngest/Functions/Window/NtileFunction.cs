using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Functions.Window;

/// <summary>
/// Implements <c>NTILE(n) OVER (...)</c>, which distributes the rows in a
/// partition into <c>n</c> approximately equal-sized buckets numbered 1
/// through <c>n</c>. If the row count is not evenly divisible by <c>n</c>,
/// the first remainder buckets receive one extra row.
/// </summary>
public sealed class NtileFunction : IWindowFunction
{
    /// <inheritdoc/>
    public string Name => "NTILE";

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("NTILE() requires exactly one argument (the number of buckets).");
        }

        if (!DataValue.IsIntegerKind(argumentKinds[0]))
        {
            throw new ArgumentException("NTILE() argument must be a numeric scalar.");
        }

        return DataKind.Float32;
    }

    /// <inheritdoc/>
    public IWindowComputation CreateComputation() => new NtileComputation();

    private sealed class NtileComputation : IWindowComputation
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

            // Evaluate the bucket count from the first row (constant expression).
            DataValue bucketValue = evaluator.Evaluate(argumentExpressions[0], partitionRows[0]);
            int bucketCount = WindowFunctionHelper.ToInt(bucketValue);

            if (bucketCount <= 0)
            {
                throw new InvalidOperationException("NTILE() bucket count must be a positive integer.");
            }

            int rowCount = partitionRows.Count;
            int baseSize = rowCount / bucketCount;
            int remainder = rowCount % bucketCount;

            // First 'remainder' buckets get baseSize + 1 rows,
            // remaining buckets get baseSize rows.
            int rowIndex = 0;
            for (int bucket = 1; bucket <= bucketCount && rowIndex < rowCount; bucket++)
            {
                int bucketSize = bucket <= remainder ? baseSize + 1 : baseSize;

                for (int j = 0; j < bucketSize && rowIndex < rowCount; j++)
                {
                    results[rowIndex] = DataValue.FromFloat32(bucket);
                    rowIndex++;
                }
            }
        }
    }
}
