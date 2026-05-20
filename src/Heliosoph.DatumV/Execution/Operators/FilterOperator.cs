using System.Buffers;
using Heliosoph.DatumV.Execution.Operators.BatchPredicates;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution.Operators;

/// <summary>
/// Filters rows from a child operator by evaluating a WHERE expression.
/// Only rows where the expression evaluates to true are emitted.
/// </summary>
public sealed class FilterOperator : QueryOperator
{

    /// <summary>
    /// Creates a filter operator.
    /// </summary>
    /// <param name="source">The child operator producing rows.</param>
    /// <param name="predicate">The WHERE predicate expression.</param>
    public FilterOperator(QueryOperator source, Expression predicate)
    {
        Source = source;
        Predicate = predicate;
    }

    /// <summary>The child operator producing rows.</summary>
    public QueryOperator Source { get; }

    /// <summary>The filter predicate expression.</summary>
    public Expression Predicate { get; }

    /// <inheritdoc/>
    public override QueryOperator RewriteExpressions(Func<Expression, Expression> rewriter) =>
        new FilterOperator(Source.RewriteExpressions(rewriter), rewriter(Predicate));

    /// <inheritdoc/>
    protected override OperatorPlanDescription DescribeForExplainImpl()
    {
        List<string> warnings = [];

        if (QueryExplainer.ContainsPatternMatch(Predicate))
        {
            warnings.Add("LIKE/ILIKE pattern match — may scan all rows");
        }

        return new OperatorPlanDescription("Filter")
        {
            Properties = new Dictionary<string, string>
            {
                ["predicate"] = QueryExplainer.FormatExpression(Predicate),
            },
            Children = [(Source, null)],
            Warnings = warnings,
        };
    }

    /// <inheritdoc/>
    protected override async IAsyncEnumerable<RowBatch> ExecuteAsyncImpl(ExecutionContext context)
    {
        ExpressionEvaluator evaluator = context.CreateEvaluator();
        RowCopyOutputWriter writer = new(context);

        // Compile state. On the first non-empty batch we attempt to compile
        // Predicate into an IBatchPredicate using the batch's schema. Three
        // possible states once initialised:
        //   - batchPredicate != null  → fast path, mask the whole batch
        //   - batchPredicate == null AND compileAttempted → fall back per-row
        //   - !compileAttempted       → haven't seen a row yet
        // Compilation is cheap (AST walk + ordinal lookup) and pays off after
        // the first ~10 rows in a batch.
        IBatchPredicate? batchPredicate = null;
        bool compileAttempted = false;
        // Reused across input batches to hold output batches that filled during
        // the input's processing. Cleared after each yield-drain. Capacity 4
        // covers the typical 1024-in / 1024-out selectivity range; List grows
        // if needed.
        List<RowBatch> readyBatches = new(4);

        try
        {
            await foreach (RowBatch inputBatch in Source.ExecuteAsync(context).ConfigureAwait(false))
            {
                try
                {
                    if (inputBatch.Count == 0) continue;

                    if (!compileAttempted)
                    {
                        batchPredicate = BatchPredicateCompiler.TryCompile(Predicate, inputBatch[0]);
                        compileAttempted = true;
                    }

                    if (batchPredicate is not null)
                    {
                        // Fast path: predicate compiled to a tight per-batch loop.
                        // Full output batches that fall out of writer.Add are
                        // captured into readyBatches and yielded by the outer
                        // loop after the input batch has been returned.
                        EvaluateBatchFastPath(inputBatch, batchPredicate, writer, readyBatches);
                    }
                    else
                    {
                        // Fallback: per-row evaluator path. Identical to the
                        // pre-batch-eval version.
                        Arena sourceArena = inputBatch.Arena;
                        Arena targetArena = context.Store;

                        for (int index = 0, count = inputBatch.Count; index < count; index++)
                        {
                            Row row = inputBatch[index];
                            EvaluationFrame frame = new(row, sourceArena, targetArena, context, context.OuterRow);

                            if (!await evaluator.EvaluateAsBooleanAsync(Predicate, frame, context.CancellationToken).ConfigureAwait(false)) continue;

                            if (writer.Add(inputBatch, index) is RowBatch full)
                            {
                                readyBatches.Add(full);
                            }
                        }
                    }
                }
                finally
                {
                    context.ReturnRowBatch(inputBatch);
                }

                // Yield any full output batches the input produced. Done outside
                // the try/finally so the input batch is already returned to the
                // pool before downstream consumers process the output.
                for (int i = 0; i < readyBatches.Count; i++)
                {
                    yield return readyBatches[i];
                }
                readyBatches.Clear();
            }

            RowBatch? trailing = writer.Flush();
            if (trailing is not null) yield return trailing;
        }
        finally
        {
            RowBatch? leftover = writer.Flush();
            if (leftover is not null) context.ReturnRowBatch(leftover);
        }
    }

    /// <summary>
    /// Fast-path filter for a single input batch when the predicate compiled
    /// to an <see cref="IBatchPredicate"/>. Evaluates the predicate over the
    /// whole batch in one monomorphic loop, then copies passing rows through
    /// <paramref name="writer"/>. Full output batches that fall out of
    /// <c>writer.Add</c> are collected into <paramref name="readyBatches"/>
    /// for the caller to yield once the input batch has been returned.
    /// </summary>
    /// <remarks>
    /// The mask is rented from <see cref="ArrayPool{T}"/> so we don't allocate
    /// per batch — 1 KB recycled for a 1024-row batch. The inner loop is
    /// branchless: a single sequential pass over the mask + writer add.
    /// </remarks>
    private static void EvaluateBatchFastPath(
        RowBatch inputBatch,
        IBatchPredicate predicate,
        RowCopyOutputWriter writer,
        List<RowBatch> readyBatches)
    {
        bool[] maskBuffer = ArrayPool<bool>.Shared.Rent(inputBatch.Count);
        try
        {
            Span<bool> mask = maskBuffer.AsSpan(0, inputBatch.Count);
            predicate.Evaluate(inputBatch, mask);

            for (int i = 0, n = inputBatch.Count; i < n; i++)
            {
                if (!mask[i]) continue;
                if (writer.Add(inputBatch, i) is RowBatch full)
                {
                    readyBatches.Add(full);
                }
            }
        }
        finally
        {
            ArrayPool<bool>.Shared.Return(maskBuffer);
        }
    }
}
