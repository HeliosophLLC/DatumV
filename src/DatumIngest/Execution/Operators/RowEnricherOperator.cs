using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Operator that evaluates a fixed set of expressions per row and appends
/// the results as additional columns on the output <see cref="RowBatch"/>.
/// The columns ride on the batch's <see cref="ColumnLookup"/>, so downstream
/// operators reference them by name like any source column.
/// </summary>
/// <remarks>
/// <para>
/// This is the runtime side of common-subexpression elimination. The
/// planner identifies subexpressions that occur at multiple call sites
/// (within a projection, or — once the cross-clause pass lands — across
/// WHERE/SELECT/ORDER BY) and inserts a <see cref="RowEnricherOperator"/>
/// upstream of all references. Each duplicate site becomes a
/// <see cref="ColumnReference"/> to the hidden column. One evaluation per
/// row covers every reference downstream.
/// </para>
/// <para>
/// The shape mirrors <see cref="ModelInvocationOperator"/>: build the
/// augmented <see cref="ColumnLookup"/> on first batch, rent an output
/// batch, stabilise source columns into the new arena, write the
/// enriched values into the appended slots, yield. Same arena lifetime
/// model — values written to <see cref="RowBatch.Arena"/> survive the
/// source batch returning to the pool.
/// </para>
/// </remarks>
public sealed class RowEnricherOperator : IQueryOperator
{
    private readonly IQueryOperator _source;
    private readonly IReadOnlyList<RowEnrichment> _enrichments;

    /// <summary>
    /// Creates a row-enricher operator.
    /// </summary>
    /// <param name="source">Upstream operator providing input rows.</param>
    /// <param name="enrichments">
    /// One enrichment per hidden column. Order matters — appended in the
    /// order given. Earlier enrichments are <em>not</em> visible to later
    /// enrichments in the same operator (they share the source row's frame);
    /// stack a second <see cref="RowEnricherOperator"/> when you need that
    /// kind of dependent computation.
    /// </param>
    public RowEnricherOperator(
        IQueryOperator source,
        IReadOnlyList<RowEnrichment> enrichments)
    {
        if (enrichments.Count == 0)
        {
            throw new ArgumentException(
                "RowEnricherOperator requires at least one enrichment; an empty list is a planner bug.",
                nameof(enrichments));
        }
        _source = source;
        _enrichments = enrichments;
    }

    /// <summary>The upstream source operator.</summary>
    public IQueryOperator Source => _source;

    /// <summary>The expressions and their target hidden column names.</summary>
    public IReadOnlyList<RowEnrichment> Enrichments => _enrichments;

    /// <inheritdoc/>
    public IQueryOperator RewriteExpressions(Func<Expression, Expression> rewriter)
    {
        RowEnrichment[] rewritten = new RowEnrichment[_enrichments.Count];
        for (int i = 0; i < _enrichments.Count; i++)
        {
            rewritten[i] = _enrichments[i] with
            {
                Expression = rewriter(_enrichments[i].Expression),
            };
        }
        return new RowEnricherOperator(
            _source.RewriteExpressions(rewriter),
            rewritten);
    }

    /// <inheritdoc/>
    public OperatorPlanDescription DescribeForExplain()
    {
        Dictionary<string, string> properties = new()
        {
            ["enrichments"] = string.Join(
                ", ",
                _enrichments.Select(e => $"{e.ColumnName} = {QueryExplainer.FormatExpression(e.Expression)}")),
        };

        return new OperatorPlanDescription("Row Enricher")
        {
            Properties = properties,
            Children = [(Source, null)],
        };
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(ExecutionContext context)
    {
        CancellationToken cancellationToken = context.CancellationToken;
        ExpressionEvaluator evaluator = new(context);
        Pool pool = context.Pool;
        ColumnLookup? outputLookup = null;
        int[]? sourceCopySlots = null;

        await foreach (RowBatch sourceBatch in _source.ExecuteAsync(context).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (sourceBatch.Count == 0)
            {
                context.ReturnRowBatch(sourceBatch);
                continue;
            }

            // First batch: build the augmented column lookup once. Hidden columns
            // append after the source columns; planner-generated names are
            // collision-free (`__cse_N` prefix) so they don't shadow user columns.
            if (outputLookup is null)
            {
                ColumnLookup sourceLookup = sourceBatch.ColumnLookup;
                int sourceCount = sourceLookup.Count;
                string[] outputNames = new string[sourceCount + _enrichments.Count];
                for (int i = 0; i < sourceCount; i++)
                {
                    outputNames[i] = sourceLookup.ColumnNames[i];
                }
                for (int i = 0; i < _enrichments.Count; i++)
                {
                    outputNames[sourceCount + i] = _enrichments[i].ColumnName;
                }
                outputLookup = new ColumnLookup(outputNames);

                sourceCopySlots = new int[sourceCount];
                for (int i = 0; i < sourceCopySlots.Length; i++)
                {
                    sourceCopySlots[i] = i;
                }
            }

            int rowsThisBatch = sourceBatch.Count;
            RowBatch outputBatch = context.RentRowBatch(outputLookup, rowsThisBatch);

            try
            {
                for (int rowIdx = 0; rowIdx < rowsThisBatch; rowIdx++)
                {
                    Row sourceRow = sourceBatch[rowIdx];
                    EvaluationFrame frame = new(
                        sourceRow,
                        sourceBatch.Arena,
                        outputBatch.Arena,
                        context.OuterRow,
                        context.SidecarRegistry);

                    DataValue[] outValues = pool.RentDataValues(outputLookup.Count);

                    // Copy source columns, stabilising arena-backed payloads
                    // into the output batch's arena so they survive the source
                    // batch returning to the pool.
                    for (int slot = 0; slot < sourceCopySlots!.Length; slot++)
                    {
                        outValues[slot] = DataValueRetention.Stabilize(
                            sourceRow[sourceCopySlots[slot]],
                            sourceBatch.Arena,
                            outputBatch.Arena);
                    }

                    // Append the enrichment results. Each expression evaluates
                    // against the source row; results write into the output
                    // arena via the evaluator's normal target-store routing.
                    int hiddenSlotBase = sourceCopySlots.Length;
                    for (int i = 0; i < _enrichments.Count; i++)
                    {
                        outValues[hiddenSlotBase + i] = evaluator.Evaluate(
                            _enrichments[i].Expression, frame);
                    }

                    outputBatch.Add(outValues);
                }
            }
            catch
            {
                context.ReturnRowBatch(outputBatch);
                context.ReturnRowBatch(sourceBatch);
                throw;
            }

            context.ReturnRowBatch(sourceBatch);
            yield return outputBatch;
        }
    }
}

/// <summary>
/// One hidden-column enrichment: an expression to evaluate per row and the
/// column name to attach the result under. Used by <see cref="RowEnricherOperator"/>.
/// </summary>
/// <param name="ColumnName">
/// The hidden column name. Planner-allocated names use the <c>__cse_</c>
/// prefix to avoid collision with user-visible columns.
/// </param>
/// <param name="Expression">The expression evaluated against each source row.</param>
public readonly record struct RowEnrichment(string ColumnName, Expression Expression);
