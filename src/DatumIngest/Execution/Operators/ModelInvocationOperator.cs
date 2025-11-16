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
    /// One expression per input the model expects. Each is evaluated against the
    /// source row before the batched dispatch.
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
        string outputColumnName)
    {
        _source = source;
        _modelName = modelName;
        _inputExpressions = inputExpressions;
        _outputColumnName = outputColumnName;
    }

    /// <summary>The upstream source operator.</summary>
    public IQueryOperator Source => _source;

    /// <summary>The unqualified model name resolved against <see cref="ExecutionContext.Models"/>.</summary>
    public string ModelName => _modelName;

    /// <summary>The expressions evaluated to produce per-row inputs for the model.</summary>
    public IReadOnlyList<Expression> InputExpressions => _inputExpressions;

    /// <summary>The column name attached to model outputs.</summary>
    public string OutputColumnName => _outputColumnName;

    /// <inheritdoc/>
    public IQueryOperator RewriteExpressions(Func<Expression, Expression> rewriter)
    {
        Expression[] rewritten = new Expression[_inputExpressions.Count];
        for (int i = 0; i < _inputExpressions.Count; i++)
        {
            rewritten[i] = rewriter(_inputExpressions[i]);
        }

        return new ModelInvocationOperator(
            _source.RewriteExpressions(rewriter),
            _modelName,
            rewritten,
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

        IModel model = context.Models.GetModel(_modelName);
        if (model.InputKinds.Count != _inputExpressions.Count)
        {
            throw new InvalidOperationException(
                $"Model '{_modelName}' expects {model.InputKinds.Count} input(s) but the call site supplies {_inputExpressions.Count}.");
        }

        ExpressionEvaluator evaluator = new(context);
        Pool pool = context.Pool;
        ColumnLookup? outputLookup = null;
        int[]? sourceCopySlots = null;

        await foreach (RowBatch sourceBatch in _source.ExecuteAsync(context).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (sourceBatch.Count == 0)
            {
                pool.ReturnRowBatch(sourceBatch);
                continue;
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

            // Step 1: evaluate the input expressions against every source row to build
            // the row-major input matrix the model consumes. Inputs share the source
            // batch's arena, which is fine — the model produces fresh DataValues that
            // we'll stabilise into the output arena on scatter.
            DataValue[][] inputs = new DataValue[sourceBatch.Count][];
            for (int rowIdx = 0; rowIdx < sourceBatch.Count; rowIdx++)
            {
                Row row = sourceBatch[rowIdx];
                EvaluationFrame frame = new(row, sourceBatch.Arena, sourceBatch.Arena, context.OuterRow, context.SidecarRegistry);

                DataValue[] rowInputs = new DataValue[_inputExpressions.Count];
                for (int argIdx = 0; argIdx < _inputExpressions.Count; argIdx++)
                {
                    rowInputs[argIdx] = evaluator.Evaluate(_inputExpressions[argIdx], frame);
                }
                inputs[rowIdx] = rowInputs;
            }

            // Step 2: rent the output batch up-front so the model can stabilise non-inline
            // results directly into the consumer-visible arena.
            RowBatch outputBatch = pool.RentRowBatch(outputLookup, sourceBatch.Count);

            // Step 3: dispatch the whole batch in one async call. Inputs reference
            // the source batch's arena (or a sidecar via the registry); the model
            // materialises non-inline outputs into the output batch's arena.
            IReadOnlyList<DataValue> modelOutputs = await model
                .InferBatchAsync(
                    inputs,
                    sourceBatch.Arena,
                    context.SidecarRegistry,
                    outputBatch.Arena,
                    cancellationToken)
                .ConfigureAwait(false);

            if (modelOutputs.Count != sourceBatch.Count)
            {
                pool.ReturnRowBatch(outputBatch);
                pool.ReturnRowBatch(sourceBatch);
                throw new InvalidOperationException(
                    $"Model '{_modelName}' returned {modelOutputs.Count} outputs for a {sourceBatch.Count}-row input batch.");
            }

            // Step 4: scatter — for each source row, copy source columns and append the
            // model output. Source values stabilise from the source batch's arena into
            // the output batch's arena so they survive the source batch returning to the
            // pool below.
            for (int rowIdx = 0; rowIdx < sourceBatch.Count; rowIdx++)
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
            yield return outputBatch;
        }
    }
}
