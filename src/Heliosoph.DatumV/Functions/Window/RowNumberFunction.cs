using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Functions.Window;

/// <summary>
/// Implements <c>ROW_NUMBER() OVER (...)</c>, which assigns sequential integers
/// 1, 2, 3, ... to each row within a partition according to the window ORDER BY.
/// </summary>
public sealed class RowNumberFunction : IWindowFunction
{
    /// <inheritdoc/>
    public string Name => "ROW_NUMBER";

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 0)
        {
            throw new ArgumentException("ROW_NUMBER() accepts no arguments.");
        }

        return DataKind.Float32;
    }

    /// <inheritdoc/>
    public IWindowComputation CreateComputation() => new RowNumberComputation();

    private sealed class RowNumberComputation : IWindowComputation
    {
        public ValueTask ComputeAsync(
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
            for (int i = 0; i < partitionRows.Count; i++)
            {
                results[i] = DataValue.FromFloat32(i + 1);
            }
            return ValueTask.CompletedTask;
        }
    }
}
