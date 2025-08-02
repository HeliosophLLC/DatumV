using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Sorts the output of a child operator by one or more expressions.
/// When <see cref="TopNRows"/> is set, uses a bounded max-heap to retain
/// only the top N rows in O(n log N) time and O(N) memory. Otherwise,
/// materializes all rows and sorts them.
/// </summary>
public sealed class OrderByOperator : IQueryOperator
{
    private readonly IQueryOperator _source;
    private readonly IReadOnlyList<OrderByItem> _orderByItems;
    private readonly int? _topNRows;

    /// <summary>
    /// Creates an ORDER BY operator.
    /// </summary>
    /// <param name="source">The child operator producing rows.</param>
    /// <param name="orderByItems">The sort criteria.</param>
    /// <param name="topNRows">
    /// When set, limits the sort to the top N rows using a bounded heap.
    /// Typically <c>LIMIT + OFFSET</c> from the query planner.
    /// </param>
    public OrderByOperator(
        IQueryOperator source,
        IReadOnlyList<OrderByItem> orderByItems,
        int? topNRows = null)
    {
        _source = source;
        _orderByItems = orderByItems;
        _topNRows = topNRows;
    }

    /// <summary>The child operator producing rows.</summary>
    public IQueryOperator Source => _source;

    /// <summary>The sort criteria.</summary>
    public IReadOnlyList<OrderByItem> OrderByItems => _orderByItems;

    /// <summary>
    /// The bounded heap size, or <c>null</c> for unbounded full sort.
    /// </summary>
    public int? TopNRows => _topNRows;

    /// <inheritdoc/>
    public async IAsyncEnumerable<Row> ExecuteAsync(ExecutionContext context)
    {
        ExpressionEvaluator evaluator = new(context.FunctionRegistry, context.QueryMeter, context.OuterRow);

        List<Row> rows;

        if (_topNRows is int topN and > 0)
        {
            rows = await CollectTopNAsync(topN, evaluator, context).ConfigureAwait(false);
        }
        else
        {
            rows = await CollectAllAsync(context).ConfigureAwait(false);
        }

        // Sort the retained rows into final order.
        rows.Sort((left, right) => CompareRows(left, right, evaluator));

        foreach (Row row in rows)
        {
            yield return row;
        }
    }

    /// <summary>
    /// Materializes all rows from the source.
    /// </summary>
    private async Task<List<Row>> CollectAllAsync(ExecutionContext context)
    {
        List<Row> rows = new();

        await foreach (Row row in _source.ExecuteAsync(context).ConfigureAwait(false))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            context.QueryMeter?.ThrowIfExceeded();

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>
    /// Retains only the top N rows using a bounded max-heap. The heap keeps
    /// the "worst" row (last in sort order) at the top so it can be evicted
    /// when a better row arrives. After streaming all source rows, the heap
    /// contains exactly the top N rows (or fewer if the source is smaller).
    /// </summary>
    private async Task<List<Row>> CollectTopNAsync(
        int topN, ExpressionEvaluator evaluator, ExecutionContext context)
    {
        // PriorityQueue is a min-heap. Using reversed comparison makes the
        // "worst" row (last in desired sort order) the one dequeued first,
        // turning it into a max-heap for eviction purposes.
        PriorityQueue<Row, Row> heap = new(
            Comparer<Row>.Create(
                (left, right) => -CompareRows(left, right, evaluator)));

        await foreach (Row row in _source.ExecuteAsync(context).ConfigureAwait(false))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            context.QueryMeter?.ThrowIfExceeded();

            if (heap.Count < topN)
            {
                heap.Enqueue(row, row);
            }
            else
            {
                // EnqueueDequeue adds the new row and immediately removes the
                // worst. If the new row is worse than all current rows, it is
                // the one removed — effectively a no-op.
                heap.EnqueueDequeue(row, row);
            }
        }

        List<Row> rows = new(heap.Count);

        while (heap.Count > 0)
        {
            rows.Add(heap.Dequeue());
        }

        return rows;
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

    /// <summary>
    /// Compares two <see cref="DataValue"/> instances for ordering. Nulls sort last.
    /// </summary>
    internal static int CompareDataValues(DataValue left, DataValue right)
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
