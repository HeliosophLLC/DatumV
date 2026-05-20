using System.Diagnostics;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Execution.Operators;

/// <summary>
/// A decorator that wraps any <see cref="QueryOperator"/> and collects
/// runtime metrics: row counts and elapsed time. Used by EXPLAIN ANALYZE.
/// </summary>
public sealed class InstrumentedOperator : QueryOperator
{
    private readonly QueryOperator _inner;
    private readonly Stopwatch _stopwatch = new();
    private long _rowsProduced;

    /// <summary>
    /// Creates an instrumented wrapper around the given operator.
    /// </summary>
    /// <param name="inner">The operator to instrument.</param>
    public InstrumentedOperator(QueryOperator inner) : base(false)
    {
        _inner = inner;
    }

    /// <summary>The wrapped operator.</summary>
    public QueryOperator Inner => _inner;

    /// <summary>Number of rows this operator has produced.</summary>
    public long RowsProduced => _rowsProduced;

    /// <summary>Total elapsed time (inclusive of children).</summary>
    public TimeSpan TotalElapsed => _stopwatch.Elapsed;

    /// <summary>
    /// Self time = total time minus the sum of children's total times.
    /// </summary>
    public TimeSpan SelfElapsed
    {
        get
        {
            TimeSpan childrenTime = TimeSpan.Zero;
            foreach (InstrumentedOperator child in GetInstrumentedChildren())
            {
                childrenTime += child.TotalElapsed;
            }

            TimeSpan self = TotalElapsed - childrenTime;
            return self < TimeSpan.Zero ? TimeSpan.Zero : self;
        }
    }

    /// <summary>
    /// Returns the direct instrumented children of this operator.
    /// </summary>
    public IEnumerable<InstrumentedOperator> GetInstrumentedChildren()
    {
        return GetDirectChildren(_inner)
            .Select(child => child is InstrumentedOperator instrumented ? instrumented : null)
            .Where(child => child is not null)!;
    }

    /// <inheritdoc/>
    protected override OperatorPlanDescription DescribeForExplainImpl()
    {
        return _inner.DescribeForExplain();
    }

    /// <inheritdoc/>
    protected override async IAsyncEnumerable<RowBatch> ExecuteAsyncImpl(ExecutionContext context)
    {
        _stopwatch.Start();

        await foreach (RowBatch batch in _inner.ExecuteAsync(context).ConfigureAwait(false))
        {
            _stopwatch.Stop();
            _rowsProduced += batch.Count;
            yield return batch;
            _stopwatch.Start();
        }

        _stopwatch.Stop();
    }

    /// <summary>
    /// Recursively wraps the entire operator tree with <see cref="InstrumentedOperator"/>
    /// decorators, returning the instrumented root.
    /// </summary>
    /// <param name="root">The root operator to instrument.</param>
    /// <returns>An instrumented operator wrapping the entire tree.</returns>
    public static InstrumentedOperator InstrumentTree(QueryOperator root)
    {
        QueryOperator instrumented = InstrumentRecursive(root);
        return (InstrumentedOperator)instrumented;
    }

    private static QueryOperator InstrumentRecursive(QueryOperator op)
    {
        // Instrument children first, then wrap the operator itself.
        QueryOperator wrapped = op switch
        {
            Operators.FilterOperator filter => new Operators.FilterOperator(
                InstrumentRecursive(filter.Source), filter.Predicate),
            Operators.ProjectOperator project => new Operators.ProjectOperator(
                InstrumentRecursive(project.Source), project.Columns, project.LetBindings, project.Assertions),
            Operators.JoinOperator join => new Operators.JoinOperator(
                InstrumentRecursive(join.Left),
                InstrumentRecursive(join.Right),
                join.Type,
                join.OnCondition),
            Operators.OrderByOperator orderBy => new Operators.OrderByOperator(
                InstrumentRecursive(orderBy.Source), orderBy.OrderByItems),
            Operators.LimitOperator limit => new Operators.LimitOperator(
                InstrumentRecursive(limit.Source), limit.LimitExpression, limit.OffsetExpression),
            Operators.AliasOperator alias => new Operators.AliasOperator(
                InstrumentRecursive(alias.Source), alias.Alias),
            Operators.SubqueryOperator subquery => new Operators.SubqueryOperator(
                InstrumentRecursive(subquery.InnerOperator), subquery.Alias),
            // ScanOperator and unknown operators: wrap directly, no children to instrument.
            _ => op,
        };

        return new InstrumentedOperator(wrapped);
    }

    /// <summary>
    /// Populates the given <see cref="ExplainPlanNode"/> tree with runtime
    /// metrics from the instrumented operator tree.
    /// </summary>
    /// <param name="planNode">The static explain plan node to populate.</param>
    /// <param name="instrumentedOp">The instrumented operator tree (after execution).</param>
    public static void PopulateMetrics(ExplainPlanNode planNode, InstrumentedOperator instrumentedOp)
    {
        planNode.RowsProduced = instrumentedOp.RowsProduced;
        planNode.TotalTime = instrumentedOp.TotalElapsed;
        planNode.SelfTime = instrumentedOp.SelfElapsed;
        
        // Add pruning statistics for scan operators backed by filterable providers.
        if (instrumentedOp.Inner is Operators.ScanOperator scan)
        {
            int total = scan.TotalIndexChunks;
            int pruned = scan.PrunedIndexChunks;
            double percentage = (double)pruned / total * 100.0;
            planNode.RuntimeAnnotations.Add(
                $"row groups: {total} total, {pruned} pruned ({percentage:F0}%)");

            // Surface the exact-row-seek count so EXPLAIN ANALYZE — and
            // tests — can distinguish "took the index seek path" from
            // "fell through to a chunked scan". Null when the seek path
            // was not used.
            planNode.ExactSeekRowsFetched = scan.ExactSeekRowsFetched;
            if (scan.ExactSeekRowsFetched is int seekRows)
            {
                planNode.RuntimeAnnotations.Add($"exact seek: {seekRows} rows");
            }

            // Composite-index path contribution. Set when at least one
            // composite index produced positions, regardless of which
            // strategy won the planner's fewest-positions tiebreak.
            planNode.CompositeIndexSeekHits = scan.CompositeIndexSeekHits;
            if (scan.CompositeIndexSeekHits is int compositeHits)
            {
                planNode.RuntimeAnnotations.Add($"composite index hits: {compositeHits}");
            }
        }

        // Compute rows consumed from children.
        List<InstrumentedOperator> instrumentedChildren = instrumentedOp.GetInstrumentedChildren().ToList();

        if (instrumentedChildren.Count == 1)
        {
            planNode.RowsConsumed = instrumentedChildren[0].RowsProduced;
        }

        // Recursively populate children.
        int childIndex = 0;
        foreach (ExplainPlanNode childPlan in planNode.Children)
        {
            if (childIndex < instrumentedChildren.Count)
            {
                PopulateMetrics(childPlan, instrumentedChildren[childIndex]);
                childIndex++;
            }
        }
    }

    private static IEnumerable<QueryOperator> GetDirectChildren(QueryOperator op)
    {
        return op.DescribeForExplain().Children.Select(child => child.Child);
    }
}
