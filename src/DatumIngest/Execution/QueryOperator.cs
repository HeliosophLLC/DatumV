using System.Diagnostics;

using DatumIngest.Diagnostics;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution;

/// <summary>
/// A node in the query execution plan tree. Each operator produces
/// a stream of rows by pulling from its child operators or data sources.
/// </summary>
public abstract class QueryOperator
{
    /// <summary>
    /// Initializes a new <see cref="QueryOperator"/> instance.
    /// </summary>
    public QueryOperator() : this(hasOwnContext: false)
    {
        
    }

    /// <summary>
    /// Initializes a new <see cref="QueryOperator"/> instance.
    /// </summary>
    public QueryOperator(bool hasOwnContext)
    {
        HasOwnContext = hasOwnContext;
    }

    /// <summary>
    /// Gets a value indicating whether this operator has its own execution context.
    /// </summary>
    public bool HasOwnContext { get; }

    /// <summary>
    /// Executes this operator and streams row batches asynchronously. Every
    /// invocation opens an <see cref="Activity"/> on
    /// <see cref="DatumActivity.Operators"/> for the duration of the
    /// iterator — <see cref="Activity.Current"/> nests through the operator
    /// chain (Scan → Filter → Project → …) automatically via the
    /// <see cref="AsyncLocal{T}"/> backing, so
    /// <see cref="DatumActivity.CurrentStack"/> reports the live stack of
    /// in-flight operators at any moment.
    /// </summary>
    /// <param name="context">Execution context with cancellation, functions, and catalog.</param>
    /// <returns>An async stream of row batches.</returns>
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(ExecutionContext context)
    {
        // StartActivity returns null when no ActivityListener is attached, in
        // which case the `using` is a no-op and the call site is effectively
        // a single null check — zero observable overhead when nothing is
        // listening. When a listener is present (RecentActivityLog,
        // dotnet-trace, OpenTelemetry, etc.) the Activity is started here
        // and disposed when the iterator is fully iterated or torn down
        // early (the `using` declaration's scope is the entire iterator).
        string operatorName = GetType().Name;
        using Activity? operatorSpan = DatumActivity.Operators.StartActivity(operatorName);

        // Wrap each *batch* of work in its own span so the timeline shows
        // per-yield latency (how long this operator took to produce one
        // batch) rather than just the iterator's full lifetime. Without
        // these, every operator stays "open" for the entire query and the
        // recent-spans log shows nothing until the consumer disposes —
        // useless for hang investigations because that's exactly when
        // disposal doesn't happen. With them, you see one completed
        // "OperatorName.batch[i]" entry per yielded batch in real time.
        //
        // The [i] suffix on the display name is the per-operator batch
        // ordinal. When you read a recent-activity dump back you can tell
        // ScanOperator.batch[3] from ScanOperator.batch[7] without having
        // to count entries — useful for back-and-forth pull patterns
        // (e.g. a FilterOperator pulling several batches from Scan before
        // it accumulates enough surviving rows to yield upward).
        int batchIndex = 0;
        Activity? batchSpan = StartBatchSpan(operatorName, batchIndex);
        try
        {
            await foreach (RowBatch rowBatch in ExecuteAsyncImpl(context).ConfigureAwait(false))
            {
                // The work that produced THIS batch is now finished. Close
                // the span before yielding so the consumer's pull time isn't
                // attributed to this operator's batch.
                batchSpan?.Dispose();
                batchSpan = null;

                yield return rowBatch;

                // Open a span for the NEXT batch's work. Created lazily here
                // so the consumer's "between pulls" idle time isn't attributed
                // to us either — the span starts exactly when ExecuteAsyncImpl
                // resumes computing.
                batchIndex++;
                batchSpan = StartBatchSpan(operatorName, batchIndex);
            }
        }
        finally
        {
            // The final span never produced a batch (the impl completed
            // without yielding). Close it as trailing work. Also fires on
            // early disposal / exceptions so we never leak an open span.
            batchSpan?.Dispose();
        }
    }

    private static Activity? StartBatchSpan(string operatorName, int index)
    {
        Activity? span = DatumActivity.Operators.StartActivity(operatorName + ".batch");
        if (span is not null)
        {
            span.DisplayName = $"{operatorName}.batch[{index}]";
            span.SetTag("batch.index", index);
        }
        return span;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="context"></param>
    /// <returns></returns>
    protected abstract IAsyncEnumerable<RowBatch> ExecuteAsyncImpl(ExecutionContext context);

    /// <summary>
    /// Returns plan metadata describing this operator for EXPLAIN output.
    /// Every operator implementation must provide meaningful plan metadata
    /// including its name, properties, children, and any warnings or annotations.
    /// </summary>
    /// <returns>A description of this operator for the execution plan.</returns>
    public OperatorPlanDescription DescribeForExplain()
    {
        return DescribeForExplainImpl();
    }


    /// <summary>
    /// Returns plan metadata describing this operator for EXPLAIN output.
    /// Every operator implementation must provide meaningful plan metadata
    /// including its name, properties, children, and any warnings or annotations.
    /// </summary>
    /// <returns>A description of this operator for the execution plan.</returns>
    protected abstract OperatorPlanDescription DescribeForExplainImpl();

    /// <summary>
    /// Returns a copy of this operator with every contained <see cref="Expression"/>
    /// tree passed through <paramref name="rewriter"/> and every child operator
    /// recursively rewritten. Used by <see cref="LiteralHoister"/> to pre-materialize
    /// literal payloads into a query-scoped store.
    /// <para>
    /// Default returns <c>this</c> — correct for leaf operators that hold no
    /// expressions. Operators with an expression or a child operator should override
    /// to forward the rewrite. Operators that don't override still work; they simply
    /// opt out of the optimization (their descendants' literals stay unhoisted).
    /// </para>
    /// </summary>
    public virtual QueryOperator RewriteExpressions(Func<Expression, Expression> rewriter) => this;
}
