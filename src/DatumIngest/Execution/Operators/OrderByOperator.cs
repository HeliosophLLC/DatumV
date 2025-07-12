using DatumQuery.Model;
using DatumQuery.Parsing.Ast;

namespace DatumQuery.Execution.Operators;

/// <summary>
/// Sorts the output of a child operator by one or more expressions.
/// Materializes all rows, sorts them, then streams the sorted result.
/// When combined with a <see cref="LimitOperator"/>, uses a bounded
/// priority queue to avoid materializing the full result set.
/// </summary>
public sealed class OrderByOperator : IQueryOperator
{
    private readonly IQueryOperator _source;
    private readonly IReadOnlyList<OrderByItem> _orderByItems;

    /// <summary>
    /// Creates an ORDER BY operator.
    /// </summary>
    /// <param name="source">The child operator producing rows.</param>
    /// <param name="orderByItems">The sort criteria.</param>
    public OrderByOperator(IQueryOperator source, IReadOnlyList<OrderByItem> orderByItems)
    {
        _source = source;
        _orderByItems = orderByItems;
    }

    /// <summary>The child operator producing rows.</summary>
    public IQueryOperator Source => _source;

    /// <summary>The sort criteria.</summary>
    public IReadOnlyList<OrderByItem> OrderByItems => _orderByItems;

    /// <inheritdoc/>
    public async IAsyncEnumerable<Row> ExecuteAsync(ExecutionContext context)
    {
        ExpressionEvaluator evaluator = new(context.FunctionRegistry);

        // Materialize all rows.
        List<Row> rows = new();
        await foreach (Row row in _source.ExecuteAsync(context).ConfigureAwait(false))
        {
            rows.Add(row);
        }

        // Sort using multi-key comparison.
        rows.Sort((left, right) => CompareRows(left, right, evaluator));

        foreach (Row row in rows)
        {
            yield return row;
        }
    }

    private int CompareRows(Row left, Row right, ExpressionEvaluator evaluator)
    {
        foreach (OrderByItem item in _orderByItems)
        {
            DataValue leftValue = evaluator.Evaluate(item.Expression, left);
            DataValue rightValue = evaluator.Evaluate(item.Expression, right);

            int comparison = CompareDataValues(leftValue, rightValue);

            if (item.Direction == SortDirection.Descending)
            {
                comparison = -comparison;
            }

            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    private static int CompareDataValues(DataValue left, DataValue right)
    {
        // Nulls sort last.
        if (left.IsNull && right.IsNull) return 0;
        if (left.IsNull) return 1;
        if (right.IsNull) return -1;

        return left.Kind switch
        {
            DataKind.Scalar => left.AsScalar().CompareTo(right.AsScalar()),
            DataKind.UInt8 => left.AsUInt8().CompareTo(right.AsUInt8()),
            DataKind.String => string.Compare(
                left.AsString(), right.AsString(), StringComparison.Ordinal),
            DataKind.Date => left.AsDate().CompareTo(right.AsDate()),
            DataKind.DateTime => left.AsDateTime().CompareTo(right.AsDateTime()),
            _ => 0,
        };
    }
}
