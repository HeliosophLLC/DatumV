using System.Runtime.CompilerServices;
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
    /// Unqualified model name (the <c>"classify"</c> in <c>"models.classify"</c>) —
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
                pool.ReturnRowBatch(sourceBatch);
                continue;
            }

            if (rowLimit.HasValue && yieldedRows >= rowLimit.Value)
            {
                pool.ReturnRowBatch(sourceBatch);
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

            // Step 1: rent the output batch sized to rowsThisBatch (capped by
            // RowLimit when set). Inputs and outputs share its arena.
            RowBatch outputBatch = pool.RentRowBatch(outputLookup, rowsThisBatch);

            // Step 2: evaluate the input expressions against every source row, then
            // stabilise each non-inline value into outputBatch.Arena so the model
            // gets a single coherent inputStore. Inputs come from one of three
            // arenas depending on the expression type:
            //   - column reference  → sourceBatch.Arena
            //   - LiteralExpression → frame.Target (= sourceBatch.Arena, since we
            //     set it that way below; the evaluator writes here on the fly)
            //   - LiteralValueExpression (hoisted literal) → context.Store
            //     (LiteralHoister runs at plan time and writes payloads into the
            //     plan-scoped hoist store, which QueryPlan plumbs as context.Store)
            // StabilizeInput tries each candidate arena until it finds one whose
            // capacity covers the value's offsets, then copies the bytes into
            // outputBatch.Arena.
            // Evaluate inputs and per-row hyperparameter overrides together so
            // each row sees its own EvaluationFrame and overrides can vary by
            // column reference (e.g. a `temp` column drives temperature). When
            // there are no optional expressions, overrideValues stays a shared
            // empty array — no per-row allocation cost in the common case.
            DataValue[][] inputs = new DataValue[rowsThisBatch][];
            DataValue[][] overrideValues = new DataValue[rowsThisBatch][];
            DataValue[] emptyOverrideRow = [];

            for (int rowIdx = 0; rowIdx < rowsThisBatch; rowIdx++)
            {
                Row row = sourceBatch[rowIdx];
                EvaluationFrame frame = new(row, sourceBatch.Arena, sourceBatch.Arena, context.OuterRow, context.SidecarRegistry);

                DataValue[] rowInputs = new DataValue[_inputExpressions.Count];
                for (int argIdx = 0; argIdx < _inputExpressions.Count; argIdx++)
                {
                    DataValue raw = evaluator.Evaluate(_inputExpressions[argIdx], frame);
                    rowInputs[argIdx] = StabilizeInput(raw, sourceBatch.Arena, context.Store, outputBatch.Arena);
                }
                inputs[rowIdx] = rowInputs;

                if (_optionalExpressions.Count == 0)
                {
                    overrideValues[rowIdx] = emptyOverrideRow;
                }
                else
                {
                    DataValue[] rowOverrides = new DataValue[_optionalExpressions.Count];
                    for (int i = 0; i < _optionalExpressions.Count; i++)
                    {
                        DataValue raw = evaluator.Evaluate(_optionalExpressions[i], frame);
                        rowOverrides[i] = StabilizeInput(raw, sourceBatch.Arena, context.Store, outputBatch.Arena);
                    }
                    overrideValues[rowIdx] = rowOverrides;
                }
            }

            // Step 3: dispatch the whole batch in one async call. All input
            // DataValue payloads now live in outputBatch.Arena, so we pass it as
            // the inputStore. The model materialises non-inline outputs into the
            // same arena.
            IReadOnlyList<DataValue> modelOutputs = await model
                .InferBatchAsync(
                    inputs,
                    outputBatch.Arena,
                    context.SidecarRegistry,
                    outputBatch.Arena,
                    overrideValues,
                    cancellationToken)
                .ConfigureAwait(false);

            if (modelOutputs.Count != rowsThisBatch)
            {
                pool.ReturnRowBatch(outputBatch);
                pool.ReturnRowBatch(sourceBatch);
                throw new InvalidOperationException(
                    $"Model '{_modelName}' returned {modelOutputs.Count} outputs for a {rowsThisBatch}-row input batch.");
            }

            // Step 4: scatter — for each source row, copy source columns and append the
            // model output. Source values stabilise from the source batch's arena into
            // the output batch's arena so they survive the source batch returning to the
            // pool below.
            for (int rowIdx = 0; rowIdx < rowsThisBatch; rowIdx++)
            {
                Row sourceRow = sourceBatch[rowIdx];
                DataValue[] outValues = pool.RentDataValues(outputLookup.Count);
                for (int slot = 0; slot < sourceCopySlots!.Length; slot++)
                {
                    outValues[slot] = DataValueRetention.Stabilize(
                        sourceRow[sourceCopySlots[slot]], sourceBatch.Arena, outputBatch.Arena);
                }
                outValues[^1] = DataValueRetention.Stabilize(
                    modelOutputs[rowIdx], outputBatch.Arena, outputBatch.Arena);
                outputBatch.Add(outValues);
            }

            pool.ReturnRowBatch(sourceBatch);
            yieldedRows += rowsThisBatch;
            yield return outputBatch;

            if (rowLimit.HasValue && yieldedRows >= rowLimit.Value)
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// Stabilises an input <see cref="DataValue"/> into <paramref name="target"/>,
    /// routing the source-store lookup by the value's flag bits. Three cases:
    /// <list type="bullet">
    /// <item><description>
    /// <strong>Inline / sidecar / inline-array / null</strong> — self-contained,
    /// no store dereference needed. Pass-through (<see cref="Arena"/> argument is
    /// only used by <see cref="DataValueRetention.Stabilize"/> for sidecar
    /// resolution if applicable).
    /// </description></item>
    /// <item><description>
    /// <strong>In context store</strong> (<see cref="DataValue.IsInContextStore"/>) —
    /// the dual-flag pattern set by <see cref="LiteralHoister"/>. Read via
    /// <paramref name="contextStore"/>, the plan-scoped persistent hoist arena
    /// plumbed as <c>ExecutionContext.Store</c>.
    /// </description></item>
    /// <item><description>
    /// <strong>Arena-backed</strong> — read via <paramref name="primarySource"/>,
    /// the source batch's arena (where column-reference values live).
    /// </description></item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// The flag-based dispatch replaces an earlier <see cref="Arena.BytesWritten"/>
    /// heuristic that probed which arena had bytes. The heuristic was fragile
    /// against pool-recycled arenas: a freshly rented arena's
    /// <see cref="Arena.Capacity"/> persists from a prior use even when nothing
    /// has been written this rental, so heuristics on capacity returned stale
    /// bytes from the previous tenant.
    /// </remarks>
    private static DataValue StabilizeInput(
        DataValue value,
        Arena primarySource,
        IValueStore contextStore,
        IValueStore target)
    {
        if (value.IsNull || value.IsInline || value.IsInSidecar || value.IsInlineArray)
        {
            return DataValueRetention.Stabilize(value, primarySource, target);
        }

        IValueStore source = value.IsInContextStore ? contextStore : primarySource;
        return DataValueRetention.Stabilize(value, source, target);
    }
}
