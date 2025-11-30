using System.Runtime.CompilerServices;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Models;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Operator that runs a registered <see cref="IModel"/> over each input
/// <see cref="RowBatch"/> as a single batched dispatch and yields an augmented
/// batch containing the source columns plus one output column carrying the
/// model's per-row result.
/// </summary>
/// <remarks>
/// <para>
/// The planner hoists every <c>models.foo(...)</c> call out of expressions into
/// one of these operators (one operator per call site, deduped by AST node so a
/// single call referenced from multiple clauses runs once). Scalar function
/// infrastructure stays sync; async work happens at this operator boundary,
/// absorbed by the engine's <c>IAsyncEnumerable&lt;RowBatch&gt;</c> contract.
/// </para>
/// <para>
/// <strong>Batching</strong> — the model receives <c>batch.Count</c> rows in one
/// <see cref="IModel.InferBatchAsync"/> call. Sub-batching for GPU memory limits
/// is the model implementation's responsibility, not the operator's.
/// </para>
/// <para>
/// <strong>Arena routing</strong> — the operator stabilises both the source
/// columns and the model's results into the output batch's arena, so consumers
/// reading via <c>batch.Arena</c> resolve everything without arena-mismatch.
/// Same pattern as <c>ProjectOperator</c>.
/// </para>
/// </remarks>
public sealed class ModelInvocationOperator : IQueryOperator
{
    private readonly IQueryOperator _source;
    private readonly string _modelName;
    private readonly IReadOnlyList<Expression> _inputExpressions;
    private readonly IReadOnlyList<Expression> _optionalExpressions;
    private readonly string _outputColumnName;

    /// <summary>
    /// Creates a model-invocation operator.
    /// </summary>
    /// <param name="source">Upstream operator providing input rows.</param>
    /// <param name="modelName">
    /// Unqualified model name (the <c>"mobilenetv2"</c> in <c>"models.mobilenetv2"</c>) —
    /// resolved via <see cref="ExecutionContext.Models"/> at execute time.
    /// </param>
    /// <param name="inputExpressions">
    /// One expression per required input the model expects. Each is evaluated against
    /// the source row before the batched dispatch.
    /// </param>
    /// <param name="optionalExpressions">
    /// Per-call hyperparameter expressions, in the order declared by the catalog
    /// entry's <c>OptionalArgKinds</c> (e.g. <c>[temperature, max_tokens]</c>).
    /// Length may be shorter than the declared list — trailing parameters fall
    /// back to the model's defaults. Pass an empty list when the call site
    /// supplies no overrides.
    /// </param>
    /// <param name="outputColumnName">
    /// Column name to attach the model's result under. The planner generates a
    /// stable synthetic name (e.g. <c>__model_classify_0</c>) so subsequent
    /// expressions can reference the result.
    /// </param>
    public ModelInvocationOperator(
        IQueryOperator source,
        string modelName,
        IReadOnlyList<Expression> inputExpressions,
        IReadOnlyList<Expression> optionalExpressions,
        string outputColumnName)
    {
        _source = source;
        _modelName = modelName;
        _inputExpressions = inputExpressions;
        _optionalExpressions = optionalExpressions;
        _outputColumnName = outputColumnName;
    }

    /// <summary>The upstream source operator.</summary>
    public IQueryOperator Source => _source;

    /// <summary>The unqualified model name resolved against <see cref="ExecutionContext.Models"/>.</summary>
    public string ModelName => _modelName;

    /// <summary>The expressions evaluated to produce per-row required inputs for the model.</summary>
    public IReadOnlyList<Expression> InputExpressions => _inputExpressions;

    /// <summary>The expressions evaluated to produce per-call hyperparameter overrides.</summary>
    public IReadOnlyList<Expression> OptionalExpressions => _optionalExpressions;

    /// <summary>The column name attached to model outputs.</summary>
    public string OutputColumnName => _outputColumnName;

    /// <inheritdoc/>
    public IQueryOperator RewriteExpressions(Func<Expression, Expression> rewriter)
    {
        Expression[] rewrittenInputs = new Expression[_inputExpressions.Count];
        for (int i = 0; i < _inputExpressions.Count; i++)
        {
            rewrittenInputs[i] = rewriter(_inputExpressions[i]);
        }

        Expression[] rewrittenOptionals = new Expression[_optionalExpressions.Count];
        for (int i = 0; i < _optionalExpressions.Count; i++)
        {
            rewrittenOptionals[i] = rewriter(_optionalExpressions[i]);
        }

        return new ModelInvocationOperator(
            _source.RewriteExpressions(rewriter),
            _modelName,
            rewrittenInputs,
            rewrittenOptionals,
            _outputColumnName);
    }

    /// <inheritdoc/>
    public OperatorPlanDescription DescribeForExplain()
    {
        Dictionary<string, string> properties = new()
        {
            ["model"] = _modelName,
            ["inputs"] = string.Join(", ", _inputExpressions.Select(QueryExplainer.FormatExpression)),
            ["output"] = _outputColumnName,
        };
        if (_optionalExpressions.Count > 0)
        {
            properties["overrides"] = string.Join(", ", _optionalExpressions.Select(QueryExplainer.FormatExpression));
        }

        return new OperatorPlanDescription("Model Invocation")
        {
            Properties = properties,
            Children = [(Source, null)],
        };
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(ExecutionContext context)
    {
        CancellationToken cancellationToken = context.CancellationToken;

        if (context.Models is null)
        {
            throw new InvalidOperationException(
                $"Model invocation '{_modelName}' has no ModelCatalog on the ExecutionContext. " +
                "Set ExecutionContext.Models when building the context, or remove the models.* call.");
        }

        // Acquire a residency lease for the duration of this operator's
        // execution. The lease holds an active ref on the model so it can't
        // be evicted while we're using it; disposed when the iterator
        // finalises (consumer completes or breaks). Operator-scoped pinning
        // (β semantics from the residency design) — short enough that other
        // queries can ping-pong a different model in/out, long enough that
        // we don't pay a reload mid-batch.
        using ModelLease lease = await context.Models
            .AcquireAsync(_modelName, cancellationToken)
            .ConfigureAwait(false);
        IModel model = lease.Model;
        if (model.InputKinds.Count != _inputExpressions.Count)
        {
            throw new InvalidOperationException(
                $"Model '{_modelName}' expects {model.InputKinds.Count} input(s) but the call site supplies {_inputExpressions.Count}.");
        }

        ExpressionEvaluator evaluator = new(context);
        Pool pool = context.Pool;
        ColumnLookup? outputLookup = null;
        int[]? sourceCopySlots = null;

        // RowLimit is set by a downstream LimitOperator (LIMIT + OFFSET) to
        // signal "I'll only consume this many rows total." For an expensive
        // operator like model invocation, this is the difference between
        // running the LLM once per source row (default batch can be 1024+)
        // vs. once per actually-needed row. Track yielded rows and stop
        // pulling/processing once we hit the cap.
        int yieldedRows = 0;
        int? rowLimit = context.RowLimit;

        await foreach (RowBatch sourceBatch in _source.ExecuteAsync(context).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (sourceBatch.Count == 0)
            {
                context.ReturnRowBatch(sourceBatch);
                continue;
            }

            if (rowLimit.HasValue && yieldedRows >= rowLimit.Value)
            {
                context.ReturnRowBatch(sourceBatch);
                yield break;
            }

            // Trim work to the rows we still need. If LIMIT 5 and the source
            // hands us a 64-row batch when we already produced 3, we only
            // process the next 2 rows — the rest of the source batch is
            // dropped. Saves N expensive model dispatches per LIMIT.
            int rowsThisBatch = sourceBatch.Count;
            if (rowLimit.HasValue)
            {
                int remaining = rowLimit.Value - yieldedRows;
                if (remaining < rowsThisBatch) rowsThisBatch = remaining;
            }

            // First batch: build the augmented column lookup once. The output column is
            // appended after the source columns; the planner-generated name is unique
            // enough that downstream lookups by name resolve to it unambiguously.
            if (outputLookup is null)
            {
                ColumnLookup sourceLookup = sourceBatch.ColumnLookup;
                string[] outputNames = new string[sourceLookup.Count + 1];
                for (int i = 0; i < sourceLookup.Count; i++)
                {
                    outputNames[i] = sourceLookup.ColumnNames[i];
                }
                outputNames[^1] = _outputColumnName;
                outputLookup = new ColumnLookup(outputNames);

                sourceCopySlots = new int[sourceLookup.Count];
                for (int i = 0; i < sourceCopySlots.Length; i++)
                {
                    sourceCopySlots[i] = i;
                }
            }

            // Sub-batching for streaming UX. Models with a non-null
            // PreferredBatchSize stream results back to the user as soon as
            // each chunk completes, rather than waiting for a full 1024-row
            // upstream batch to finish. For LLMs (Llama, Phi, Gemma, …) at
            // ~3s per generation this means first results in seconds, not
            // hours. PreferredBatchSize == null means "process whatever the
            // upstream hands me as one batch" — preserves existing behaviour
            // for cheap models (classifiers, detectors).
            int subBatchSize = model.PreferredBatchSize ?? rowsThisBatch;
            if (subBatchSize <= 0) subBatchSize = rowsThisBatch;

            for (int chunkStart = 0; chunkStart < rowsThisBatch; chunkStart += subBatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int chunkSize = Math.Min(subBatchSize, rowsThisBatch - chunkStart);

                // Step 1: rent an output batch sized to this chunk. Bound to
                // context.Store so it shares the single per-query arena — same
                // arena as inputs and the rest of the operator chain.
                RowBatch outputBatch = context.RentRowBatch(outputLookup, chunkSize);

                // Step 2: evaluate input expressions and per-row hyperparameter
                // overrides for the chunk's rows. The evaluator's ToValueRef
                // handles arena and sidecar resolution at the boundary, so
                // the function chain feeding the model never writes its result
                // to the arena — inputs[r][c] holds a managed payload ready
                // for direct consumption by the model.
                ValueRef[][] inputs = new ValueRef[chunkSize][];
                ValueRef[][] overrideValues = new ValueRef[chunkSize][];
                ValueRef[] emptyOverrideRow = [];

                for (int chunkRowIdx = 0; chunkRowIdx < chunkSize; chunkRowIdx++)
                {
                    Row row = sourceBatch[chunkStart + chunkRowIdx];
                    EvaluationFrame frame = new(row, sourceBatch.Arena, sourceBatch.Arena, context.OuterRow, context.SidecarRegistry);

                    ValueRef[] rowInputs = new ValueRef[_inputExpressions.Count];
                    for (int argIdx = 0; argIdx < _inputExpressions.Count; argIdx++)
                    {
                        rowInputs[argIdx] = evaluator.EvaluateAsValueRef(_inputExpressions[argIdx], frame);
                    }
                    inputs[chunkRowIdx] = rowInputs;

                    if (_optionalExpressions.Count == 0)
                    {
                        overrideValues[chunkRowIdx] = emptyOverrideRow;
                    }
                    else
                    {
                        ValueRef[] rowOverrides = new ValueRef[_optionalExpressions.Count];
                        for (int i = 0; i < _optionalExpressions.Count; i++)
                        {
                            rowOverrides[i] = evaluator.EvaluateAsValueRef(_optionalExpressions[i], frame);
                        }
                        overrideValues[chunkRowIdx] = rowOverrides;
                    }
                }

                // Step 3: dispatch this chunk. The model returns ValueRefs —
                // managed payloads — and the scatter step below materialises
                // them into outputBatch.Arena via ValueRef.ToDataValue.
                IReadOnlyList<ValueRef> modelOutputs = await model
                    .InferBatchAsync(
                        inputs,
                        overrideValues,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (modelOutputs.Count != chunkSize)
                {
                    context.ReturnRowBatch(outputBatch);
                    context.ReturnRowBatch(sourceBatch);
                    throw new InvalidOperationException(
                        $"Model '{_modelName}' returned {modelOutputs.Count} outputs for a {chunkSize}-row input chunk.");
                }

                // Step 4: scatter — for each source row in the chunk, copy
                // source columns and append the model output. Source values
                // stabilise from the source batch's arena into the output
                // batch's arena. The model output's ValueRef is materialised
                // via ToDataValue against outputBatch.Arena — the single
                // boundary write for any nested struct/array payload.
                for (int chunkRowIdx = 0; chunkRowIdx < chunkSize; chunkRowIdx++)
                {
                    Row sourceRow = sourceBatch[chunkStart + chunkRowIdx];
                    DataValue[] outValues = pool.RentDataValues(outputLookup.Count);
                    for (int slot = 0; slot < sourceCopySlots!.Length; slot++)
                    {
                        outValues[slot] = DataValueRetention.Stabilize(
                            sourceRow[sourceCopySlots[slot]], sourceBatch.Arena, outputBatch.Arena);
                    }
                    outValues[^1] = modelOutputs[chunkRowIdx].ToDataValue(outputBatch.Arena);
                    outputBatch.Add(outValues);
                }

                yieldedRows += chunkSize;
                yield return outputBatch;
            }

            // Source batch has been fully consumed (or partially consumed +
            // RowLimit reached). Return it once after all chunks emit.
            context.ReturnRowBatch(sourceBatch);

            if (rowLimit.HasValue && yieldedRows >= rowLimit.Value)
            {
                yield break;
            }
        }
    }

}
