using System.Diagnostics;

using SkiaSharp;

using Heliosoph.DatumV.Diagnostics;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Image;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Models;
using Heliosoph.DatumV.Parsing.Ast;
using Heliosoph.DatumV.Pooling;

namespace Heliosoph.DatumV.Execution.Operators;

/// <summary>
/// Operator that runs one or more registered <see cref="IModel"/>
/// invocations over each upstream <see cref="RowBatch"/>, dispatching each
/// invocation in turn and yielding an augmented batch containing the
/// source columns plus one output column per invocation.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why one operator for both shapes.</strong> A query that calls
/// <c>models.foo(x)</c> once and a query that calls <c>models.foo(models.bar(x))</c>
/// share almost all execution concerns — lease acquisition, calibration
/// trigger, sub-chunking, VRAM probing, tracer hooks, image pre-encode,
/// dynamic-shape TypeId stamping. Splitting the single-invocation case
/// into its own operator class duplicated all of that. Now the
/// invocation count is just a list length: 1 for the hoister-generated
/// shape, N for the collapser-folded chain.
/// </para>
/// <para>
/// <strong>Multi-invocation semantics.</strong> Invocations run in
/// dependency order — the list as supplied. The output batch is rented
/// once per upstream batch with the full output schema (source columns
/// followed by one column per invocation); source columns are stabilised
/// into the output batch's arena up front, invocation columns start as
/// <see cref="DataValue.UnknownNull"/> placeholders, and each invocation's
/// scatter step overwrites its column slot row-by-row. Inputs evaluate
/// against the in-progress output batch, so invocation N+1's input
/// expressions can reference invocation N's output column by name.
/// </para>
/// <para>
/// <strong>Lease lifecycle.</strong> Acquired per invocation, released
/// immediately after that invocation completes. Fast for hot models
/// (refcount bump) and correct for cold cases where the next invocation
/// needs the prior model evicted to make room.
/// </para>
/// <para>
/// <strong>Sub-chunking.</strong> Each invocation drives its own chunk
/// loop via the engine's <see cref="IBatchSizePolicy"/>. The default
/// curve-aware policy uses calibration data to pick the largest chunk
/// that fits; uncalibrated models fall back to batch=1 until the
/// coordinator measures their curve.
/// </para>
/// </remarks>
public sealed class ModelInvocationOperator : QueryOperator
{
    /// <summary>
    /// One model invocation in the operator's list: the SQL identifier of
    /// the model, the per-row required-input expressions, optional
    /// per-call hyperparameter expressions, and the planner-generated
    /// output column name the invocation's result lands under.
    /// </summary>
    /// <param name="ModelName">
    /// Unqualified model name (the <c>"mobilenetv2"</c> in
    /// <c>"models.mobilenetv2"</c>) — resolved via
    /// <see cref="ExecutionContext.Models"/> at execute time.
    /// </param>
    /// <param name="InputExpressions">
    /// One expression per required input the model expects. Evaluated
    /// against the in-progress output batch (so a later invocation can
    /// reference an earlier invocation's output column by name).
    /// </param>
    /// <param name="OptionalExpressions">
    /// Per-call hyperparameter expressions, in the order declared by the
    /// catalog entry's <c>OptionalArgKinds</c>. Length may be shorter than
    /// the declared list — trailing parameters fall back to the model's
    /// defaults. Empty when the call site supplies no overrides.
    /// </param>
    /// <param name="OutputColumnName">
    /// Column name the invocation's result is attached under. The planner
    /// generates a stable synthetic name (e.g. <c>__model_classify_0</c>)
    /// so subsequent expressions can reference the result.
    /// </param>
    public sealed record Invocation(
        string ModelName,
        IReadOnlyList<Expression> InputExpressions,
        IReadOnlyList<Expression> OptionalExpressions,
        string OutputColumnName);

    private readonly QueryOperator _source;
    private readonly IReadOnlyList<Invocation> _invocations;

    /// <summary>
    /// Creates a multi-invocation operator. Caller is responsible for
    /// topo-sorting <paramref name="invocations"/> so any invocation
    /// referencing an earlier invocation's output column appears later in
    /// the list.
    /// </summary>
    public ModelInvocationOperator(QueryOperator source, IReadOnlyList<Invocation> invocations)
    {
        if (invocations.Count == 0)
        {
            throw new ArgumentException(
                "ModelInvocationOperator requires at least one invocation.",
                nameof(invocations));
        }
        _source = source;
        _invocations = invocations;
    }

    /// <summary>
    /// Single-invocation convenience constructor used by the hoister and
    /// CSE. Equivalent to passing a 1-element list to the primary ctor.
    /// </summary>
    public ModelInvocationOperator(
        QueryOperator source,
        string modelName,
        IReadOnlyList<Expression> inputExpressions,
        IReadOnlyList<Expression> optionalExpressions,
        string outputColumnName)
        : this(source, [new Invocation(modelName, inputExpressions, optionalExpressions, outputColumnName)])
    {
    }

    /// <summary>The upstream source operator.</summary>
    public QueryOperator Source => _source;

    /// <summary>The ordered invocations dispatched per upstream batch.</summary>
    public IReadOnlyList<Invocation> Invocations => _invocations;

    /// <summary>
    /// The single invocation's model name. Throws when this operator
    /// carries more than one invocation — callers in that case should
    /// read <see cref="Invocations"/> directly.
    /// </summary>
    public string ModelName => SingleInvocation.ModelName;

    /// <summary>The single invocation's required-input expressions.</summary>
    public IReadOnlyList<Expression> InputExpressions => SingleInvocation.InputExpressions;

    /// <summary>The single invocation's optional hyperparameter expressions.</summary>
    public IReadOnlyList<Expression> OptionalExpressions => SingleInvocation.OptionalExpressions;

    /// <summary>The single invocation's output column name.</summary>
    public string OutputColumnName => SingleInvocation.OutputColumnName;

    private Invocation SingleInvocation => _invocations.Count == 1
        ? _invocations[0]
        : throw new InvalidOperationException(
            $"ModelInvocationOperator single-invocation accessor used on an operator with {_invocations.Count} invocations. Read Invocations directly.");

    /// <inheritdoc/>
    public override QueryOperator RewriteExpressions(Func<Expression, Expression> rewriter)
    {
        Invocation[] rewritten = new Invocation[_invocations.Count];
        for (int i = 0; i < _invocations.Count; i++)
        {
            Invocation orig = _invocations[i];
            Expression[] rewrittenInputs = new Expression[orig.InputExpressions.Count];
            for (int j = 0; j < orig.InputExpressions.Count; j++)
                rewrittenInputs[j] = rewriter(orig.InputExpressions[j]);
            Expression[] rewrittenOptionals = new Expression[orig.OptionalExpressions.Count];
            for (int j = 0; j < orig.OptionalExpressions.Count; j++)
                rewrittenOptionals[j] = rewriter(orig.OptionalExpressions[j]);
            rewritten[i] = new Invocation(orig.ModelName, rewrittenInputs, rewrittenOptionals, orig.OutputColumnName);
        }
        return new ModelInvocationOperator(_source.RewriteExpressions(rewriter), rewritten);
    }

    /// <inheritdoc/>
    protected override OperatorPlanDescription DescribeForExplainImpl()
    {
        if (_invocations.Count == 1)
        {
            Invocation inv = _invocations[0];
            Dictionary<string, string> properties = new()
            {
                ["model"] = inv.ModelName,
                ["inputs"] = string.Join(", ", inv.InputExpressions.Select(QueryExplainer.FormatExpression)),
                ["output"] = inv.OutputColumnName,
            };
            if (inv.OptionalExpressions.Count > 0)
            {
                properties["overrides"] = string.Join(", ", inv.OptionalExpressions.Select(QueryExplainer.FormatExpression));
            }
            return new OperatorPlanDescription("Model Invocation")
            {
                Properties = properties,
                Children = [(Source, null)],
            };
        }

        Dictionary<string, string> multi = new()
        {
            ["models"] = string.Join(" → ", _invocations.Select(i => i.ModelName)),
            ["outputs"] = string.Join(", ", _invocations.Select(i => i.OutputColumnName)),
        };
        return new OperatorPlanDescription("Model Invocation")
        {
            Properties = multi,
            Children = [(_source, null)],
        };
    }

    /// <inheritdoc/>
    protected override async IAsyncEnumerable<RowBatch> ExecuteAsyncImpl(ExecutionContext context)
    {
        CancellationToken cancellationToken = context.CancellationToken;

        if (context.Models is null)
        {
            throw new InvalidOperationException(
                "ModelInvocationOperator requires a ModelCatalog on the ExecutionContext. " +
                "Set ExecutionContext.Models when building the context.");
        }

        ExpressionEvaluator evaluator = context.CreateEvaluator();
        Pool pool = context.Pool;
        ColumnLookup? outputLookup = null;
        int[]? sourceCopySlots = null;
        int[]? invocationOutputSlots = null;
        OutputBatchAccumulator output = new(context);

        // Per-invocation element-struct TypeId cache for dynamic-shape
        // models (notably SQL-defined bodies). The model's OutputFields
        // describes the *element* struct's fields; the per-row value may
        // be either that struct directly or an Array<Struct> of it — we
        // intern the array TypeId lazily on first sight of an array
        // result. Cleared/initialised lazily on the first batch.
        ushort[]? elementStructTypeIds = null;
        ushort[]? arrayOfStructTypeIds = null;

        // RowLimit is set by a downstream LimitOperator (LIMIT + OFFSET)
        // to signal "I'll only consume this many rows total." For
        // expensive operators like model invocation, this is the
        // difference between running each model once per source row
        // (default upstream batch ≥ 1024) vs. once per actually-needed
        // row. Track yielded rows and stop pulling/processing once we hit
        // the cap.
        int yieldedRows = 0;
        int? rowLimit = context.RowLimit;

        IModelInvocationTracer? tracer = context.ModelTracer;

        try
        {
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

                // Trim work to the rows we still need. LIMIT 5 over a 64-row
                // source batch with 3 already produced → process the next 2
                // and drop the rest of this source batch. Saves N expensive
                // dispatches per LIMIT.
                int rowsThisBatch = sourceBatch.Count;
                if (rowLimit.HasValue)
                {
                    int remaining = rowLimit.Value - yieldedRows;
                    if (remaining < rowsThisBatch) rowsThisBatch = remaining;
                }

                // First-batch lazy initialization. Output schema = source
                // columns followed by one column per invocation; slot tables
                // let the per-batch loop copy/write at known indices without
                // name lookups.
                if (outputLookup is null || sourceCopySlots is null || invocationOutputSlots is null ||
                    elementStructTypeIds is null || arrayOfStructTypeIds is null)
                {
                    BuildColumnLookup(
                        sourceBatch.ColumnLookup,
                        out outputLookup,
                        out sourceCopySlots,
                        out invocationOutputSlots,
                        out elementStructTypeIds,
                        out arrayOfStructTypeIds);
                }

                RowBatch outputBatch = InitializeOutputBatch(
                    output,
                    context,
                    outputLookup,
                    sourceBatch,
                    rowsThisBatch,
                    sourceCopySlots,
                    invocationOutputSlots);

                // Stage 2: run each invocation in order, releasing the lease
                // between invocations so the residency manager can evict the
                // prior model when the next needs the VRAM. Inputs evaluate
                // against the in-progress outputBatch so later invocations
                // see earlier ones' outputs as regular columns.
                for (int invIdx = 0; invIdx < _invocations.Count; invIdx++)
                {
                    Invocation invocation = _invocations[invIdx];
                    int outputSlot = invocationOutputSlots![invIdx];

                    using Activity? span = _invocations.Count > 1
                        ? DatumActivity.Operators.StartActivity(
                            $"model-invocation.{invocation.ModelName}(rows={rowsThisBatch})")
                        : null;

                    // Auto-trigger calibration before the first dispatch of
                    // an uncalibrated model. Trigger short-circuits when the
                    // model is already calibrated, no probe is available, or
                    // the registry isn't wired. For invocation N>0, the
                    // working outputBatch already has invocation N-1's
                    // output column populated, so input expressions that
                    // reference it resolve correctly during the sample-input
                    // evaluation step.
                    await Models.Calibration.CalibrationTrigger.EnsureCalibratedAsync(
                        context,
                        invocation.ModelName,
                        invocation.InputExpressions,
                        invocation.OptionalExpressions,
                        sampleBatch: outputBatch,
                        evaluator,
                        cancellationToken).ConfigureAwait(false);

                    using ModelLease lease = await context.Models
                        .AcquireAsync(invocation.ModelName, cancellationToken)
                        .ConfigureAwait(false);
                    IModel model = lease.Model;
                    if (model.InputKinds.Count != invocation.InputExpressions.Count)
                    {
                        // outputBatch returned via outer finally's output.Flush().
                        context.ReturnRowBatch(sourceBatch);
                        throw new InvalidOperationException(
                            $"Model '{invocation.ModelName}' expects {model.InputKinds.Count} input(s) " +
                            $"but invocation supplies {invocation.InputExpressions.Count}.");
                    }

                    // Intern the element-struct TypeId for this invocation's
                    // declared output shape. OutputFields describes the
                    // *element* struct's fields; the per-row value may be
                    // either that struct or an Array<Struct> of it. The
                    // array TypeId is interned lazily on first array sight
                    // inside the scatter loop.
                    if (elementStructTypeIds![invIdx] == 0 && model.OutputFields is { } outputFields)
                    {
                        elementStructTypeIds[invIdx] =
                            (ushort)context.Types.InternStructFromColumnInfoFields(outputFields);
                    }

                    IBatchSizePolicy policy = context.Catalog.Models?.BatchSizePolicy
                        ?? StaticBatchSizePolicy.Instance;

                    ValueRef[] emptyOverrideRow = [];
                    int chunkStart = 0;
                    while (chunkStart < rowsThisBatch)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        int rowsRemaining = rowsThisBatch - chunkStart;
                        int chunkSize = policy.ChooseBatchSize(model, rowsRemaining);
                        if (chunkSize <= 0 || chunkSize > rowsRemaining) chunkSize = rowsRemaining;

                        // Evaluate inputs for THIS chunk against the
                        // in-progress output batch. Reading from outputBatch
                        // (not sourceBatch) is the load-bearing detail —
                        // it's how invocation N+1 sees invocation N's output
                        // column as a regular cell.
                        ValueRef[][] chunkInputs = new ValueRef[chunkSize][];
                        ValueRef[][] chunkOverrides = new ValueRef[chunkSize][];
                        for (int i = 0; i < chunkSize; i++)
                        {
                            int rowIdx = chunkStart + i;
                            Row row = outputBatch[rowIdx];
                            EvaluationFrame frame = new(
                                row, outputBatch.Arena, context, context.OuterRow);

                            ValueRef[] rowInputs = new ValueRef[invocation.InputExpressions.Count];
                            for (int argIdx = 0; argIdx < invocation.InputExpressions.Count; argIdx++)
                            {
                                ValueRef raw = await evaluator
                                    .EvaluateAsValueRefAsync(invocation.InputExpressions[argIdx], frame, cancellationToken)
                                    .ConfigureAwait(false);
                                rowInputs[argIdx] = CoerceToDeclaredKind(
                                    raw, model.InputKinds[argIdx], invocation.ModelName, argIdx);
                            }
                            chunkInputs[i] = rowInputs;

                            if (invocation.OptionalExpressions.Count == 0)
                            {
                                chunkOverrides[i] = emptyOverrideRow;
                            }
                            else
                            {
                                ValueRef[] rowOverrides = new ValueRef[invocation.OptionalExpressions.Count];
                                for (int j = 0; j < invocation.OptionalExpressions.Count; j++)
                                {
                                    rowOverrides[j] = await evaluator
                                        .EvaluateAsValueRefAsync(invocation.OptionalExpressions[j], frame, cancellationToken)
                                        .ConfigureAwait(false);
                                }
                                chunkOverrides[i] = rowOverrides;
                            }
                        }

                        // Tracer hook + VRAM/timing measurements. Null-checked
                        // once per chunk; cost when tracing is off is a single
                        // branch.
                        tracer?.OnDispatchStarted(invocation.ModelName, chunkSize, chunkInputs, chunkOverrides);
                        long vramBefore = VramProbe.TryGetUsage(out long usedBefore, out _) ? usedBefore : -1;
                        long startTimestamp = Stopwatch.GetTimestamp();

                        IReadOnlyList<ValueRef> modelOutputs;
                        try
                        {
                            modelOutputs = await model
                                .InferBatchAsync(chunkInputs, chunkOverrides, context.Types, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            tracer?.OnDispatchFailed(
                                invocation.ModelName,
                                chunkSize,
                                Stopwatch.GetElapsedTime(startTimestamp),
                                ex);
                            throw;
                        }

                        long vramAfter = VramProbe.TryGetUsage(out long usedAfter, out _) ? usedAfter : -1;
                        TimeSpan dispatchElapsed = Stopwatch.GetElapsedTime(startTimestamp);
                        policy.RecordDispatch(model, chunkSize, vramBefore, vramAfter, dispatchElapsed.TotalMilliseconds);

                        tracer?.OnDispatchCompleted(invocation.ModelName, chunkSize, dispatchElapsed);

                        if (modelOutputs.Count != chunkSize)
                        {
                            // outputBatch returned via outer finally's output.Flush().
                            context.ReturnRowBatch(sourceBatch);
                            throw new InvalidOperationException(
                                $"Model '{invocation.ModelName}' returned {modelOutputs.Count} outputs " +
                                $"for a {chunkSize}-row chunk.");
                        }

                        // Parallel pre-encode of managed Image outputs.
                        // depth_map_to_image and similar return SKBitmap-
                        // backed ValueRefs; the default ToDataValue path PNG-
                        // encodes during the sequential scatter below, which
                        // serialises ~20 ms-per-image of CPU work and was
                        // measured at 635 ms / 32-row chunk in profiling. Pre-
                        // encoding here amortises the PNG/JPEG step across
                        // CPU cores; the scatter then memcpy's already-
                        // encoded byte[]s into the arena. When no Image
                        // outputs are present, a single linear scan returns
                        // the list unchanged with no allocations.
                        modelOutputs = await PreEncodeImageOutputsAsync(
                            modelOutputs, cancellationToken).ConfigureAwait(false);

                        // Scatter — for each row in the chunk, stamp the
                        // model output into the invocation's column slot.
                        // For Array<Struct> outputs the per-row TypeId must
                        // be the *array* shape, not the element struct —
                        // ToDataValue looks up `desc.ElementTypeId` on the
                        // array's descriptor to propagate field TypeIds to
                        // inner rows. Stamping the element TypeId directly
                        // on the array's DataValue would short-circuit that
                        // hop and rerun the f0..fN regression. Intern the
                        // array TypeId lazily on first sight of an array
                        // result.
                        ushort elementStructTypeId = elementStructTypeIds[invIdx];
                        for (int i = 0; i < chunkSize; i++)
                        {
                            ValueRef rowValue = modelOutputs[i];
                            ushort rowTypeId;
                            if (rowValue.IsArray && elementStructTypeId != 0)
                            {
                                if (arrayOfStructTypeIds![invIdx] == 0)
                                {
                                    arrayOfStructTypeIds[invIdx] = (ushort)context.Types.InternArrayType(
                                        DataKind.Struct, elementTypeId: elementStructTypeId);
                                }
                                rowTypeId = arrayOfStructTypeIds[invIdx];
                            }
                            else
                            {
                                // No static schema (model.OutputFields is
                                // null) — dynamic-shape producer like a
                                // SQL-defined body via
                                // ProceduralModelAdapter. ToDataValue's
                                // BuildStructArray path falls back to
                                // reading the first element's TypeId when
                                // rowTypeId is 0, so dynamic-shape
                                // Array<Struct> outputs still get their
                                // per-element struct stamps. Primitive
                                // outputs have TypeId 0 anyway and lose
                                // nothing.
                                //
                                // For scalar struct outputs (e.g. RETURNS
                                // ScoredLabel from a SQL-defined classifier)
                                // the body already stamped a TypeId on the
                                // inline carrier — prefer that over
                                // elementStructTypeId so the cell's shape
                                // resolves through the per-query registry.
                                rowTypeId = elementStructTypeId != 0
                                    ? elementStructTypeId
                                    : rowValue.TypeId;
                            }
                            DataValue stamped = rowValue.ToDataValue(
                                outputBatch.Arena, rowTypeId, context.Types);
                            outputBatch[chunkStart + i].RawValues[outputSlot] = stamped;
                        }

                        chunkStart += chunkSize;
                    }
                }

                context.ReturnRowBatch(sourceBatch);
                yieldedRows += rowsThisBatch;
                // Detach the populated batch from the accumulator before yielding so
                // the next source batch's InitializeOutputBatch rents a fresh batch
                // rather than re-touching the just-yielded one.
                RowBatch toYield = output.Flush()
                    ?? throw new InvalidOperationException("ModelInvocationOperator: accumulator missing in-flight batch at yield.");
                yield return toYield;

                if (rowLimit.HasValue && yieldedRows >= rowLimit.Value)
                {
                    yield break;
                }
            }
        }
        finally
        {
            // Catches early-disposal / unhandled-throw paths where a populated
            // batch is still held by the accumulator.
            RowBatch? leftover = output.Flush();
            if (leftover is not null) context.ReturnRowBatch(leftover);
        }
    }

    private void BuildColumnLookup(
        ColumnLookup sourceLookup,
        out ColumnLookup outputLookup,
        out int[] sourceCopySlots,
        out int[] invocationOutputSlots,
        out ushort[] elementStructTypeIds,
        out ushort[] arrayOfStructTypeIds)
    {
        int sourceCount = sourceLookup.Count;
        string[] outputNames = new string[sourceCount + _invocations.Count];
        for (int i = 0; i < sourceCount; i++)
            outputNames[i] = sourceLookup.ColumnNames[i];
        for (int i = 0; i < _invocations.Count; i++)
            outputNames[sourceCount + i] = _invocations[i].OutputColumnName;

        // Carry the source's NameIndex forward so alias-doubled
        // unqualified entries (added by AliasOperator alongside the
        // physical `alias.col` names) survive into MIO output. Without
        // this, downstream Project / chained MIO input evaluation can't
        // resolve unqualified column refs against a row whose physical
        // names are qualified.
        Dictionary<string, int> nameIndex = new(
            sourceLookup.NameIndex.Count + _invocations.Count,
            StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, int> entry in sourceLookup.NameIndex)
            nameIndex[entry.Key] = entry.Value;
        for (int i = 0; i < _invocations.Count; i++)
            nameIndex[_invocations[i].OutputColumnName] = sourceCount + i;
        outputLookup = new ColumnLookup(outputNames, nameIndex);

        sourceCopySlots = new int[sourceCount];
        for (int i = 0; i < sourceCount; i++) sourceCopySlots[i] = i;

        invocationOutputSlots = new int[_invocations.Count];
        for (int i = 0; i < _invocations.Count; i++)
            invocationOutputSlots[i] = sourceCount + i;

        elementStructTypeIds = new ushort[_invocations.Count];
        arrayOfStructTypeIds = new ushort[_invocations.Count];
    }

    private RowBatch InitializeOutputBatch(
        OutputBatchAccumulator output,
        ExecutionContext context,
        ColumnLookup outputLookup,
        RowBatch sourceBatch,
        int rowsThisBatch,
        int[] sourceCopySlots,
        int[] invocationOutputSlots)
    {
        // Stabilise source columns into the output batch and
        // initialise invocation columns to UnknownNull placeholders.
        // Doing this up front means every Row in outputBatch has its
        // full DataValue[] populated before any invocation runs, so
        // invocation N's input expressions can reference earlier
        // invocations' output columns by name.
        //
        // UnknownNull (rather than a kind-specific null) is the
        // honest placeholder: we don't know the eventual cell kind
        // until the invocation's scatter overwrites it, and any
        // downstream reader that sees the placeholder mid-dispatch
        // (which shouldn't happen but isn't actively prevented)
        // benefits from typeless-null SQL semantics rather than a
        // wrongly-stamped Int32/String null.
        //
        // The batch is rented through the accumulator (one chokepoint for all
        // RowBatch rentals across the operator pipeline); the capacity hint
        // preserves the one-source-batch-to-one-output-batch sizing the
        // scatter loop assumes.

        RowBatch outputBatch = output.EnsureRentedAndGetCurrent(outputLookup, rowsThisBatch);

        for (int rowIdx = 0; rowIdx < rowsThisBatch; rowIdx++)
        {
            Row sourceRow = sourceBatch[rowIdx];
            DataValue[] outValues = context.Pool.RentDataValues(outputLookup.Count);

            for (int slot = 0; slot < sourceCopySlots.Length; slot++)
            {
                int sourceSlot = sourceCopySlots[slot];

                outValues[slot] = DataValueRetention.Stabilize(
                    sourceRow[sourceSlot],
                    sourceBatch.Arena,
                    outputBatch.Arena);
            }

            for (int slot = 0; slot < invocationOutputSlots.Length; slot++)
            {
                outValues[invocationOutputSlots[slot]] = DataValue.UnknownNull();
            }
            
            outputBatch.Add(outValues);
        }

        return outputBatch;
    }

    /// <summary>
    /// If any model output is an <see cref="SKBitmap"/>-backed Image
    /// ValueRef, encode every such output to PNG bytes in parallel on
    /// the thread pool and return a new array with the encoded
    /// (<c>byte[]</c>-backed) ValueRefs in place of the originals. When
    /// no <see cref="SKBitmap"/> outputs are present, returns the
    /// original list unchanged — single linear scan, no allocations.
    /// </summary>
    /// <remarks>
    /// PNG/JPEG encoding is the expensive step in
    /// <see cref="ValueRef.ToDataValue"/> for SKBitmap-backed Images —
    /// measured at ~20 ms per row, serialised across the scatter loop.
    /// Parallelising the encode amortises that 20 ms × N work across
    /// CPU cores; the subsequent sequential scatter just memcpy's the
    /// already-encoded bytes into the arena.
    /// </remarks>
    private static async ValueTask<IReadOnlyList<ValueRef>> PreEncodeImageOutputsAsync(
        IReadOnlyList<ValueRef> outputs, CancellationToken cancellationToken)
    {
        bool anyBitmap = false;
        for (int i = 0; i < outputs.Count; i++)
        {
            if (outputs[i].Materialized is SKBitmap)
            {
                anyBitmap = true;
                break;
            }
        }
        if (!anyBitmap) return outputs;

        ValueRef[] result = new ValueRef[outputs.Count];
        await Parallel.ForAsync(0, outputs.Count,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Environment.ProcessorCount,
            },
            (i, ct) =>
            {
                ValueRef original = outputs[i];
                if (original.Materialized is SKBitmap bmp)
                {
                    byte[] encoded = ImageEncoder.Encode(bmp, SKEncodedImageFormat.Png, 100);
                    result[i] = ValueRef.FromImage(encoded);
                }
                else
                {
                    result[i] = original;
                }
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// If the freshly-evaluated argument's kind doesn't match what the
    /// model declared, widen numeric scalars to the declared kind. SQL
    /// literals like <c>300</c> bind to the smallest integer kind that
    /// fits (Int16 here) — without this step, calling
    /// <c>models.X(image, 300, 300)</c> against a model that declares
    /// <c>[Image, Float64, Float64]</c> would throw "Cannot read Int16
    /// as Float64" deep inside the model's accessor. Treats every
    /// numeric→numeric widening as implicit (mirrors SQL's usual
    /// numeric promotion rules); non-numeric kind mismatches still
    /// throw — those are genuine signature errors.
    /// </summary>
    private static ValueRef CoerceToDeclaredKind(
        ValueRef value, DataKind declaredKind, string modelName, int argIdx)
    {
        if (value.IsNull) return value;
        if (value.Kind == declaredKind) return value;

        switch (declaredKind)
        {
            case DataKind.Float64 when value.TryToDouble(out double d):
                return ValueRef.FromFloat64(d);
            case DataKind.Float32 when value.TryToFloat(out float f):
                return ValueRef.FromFloat32(f);
            case DataKind.Int64 when value.TryToInt64(out long i64):
                return ValueRef.FromInt64(i64);
            case DataKind.Int32 when value.TryToInt32(out int i32):
                return ValueRef.FromInt32(i32);
        }

        throw new InvalidOperationException(
            $"Model '{modelName}' argument {argIdx} expects {declaredKind} but the call site supplies {value.Kind}; "
            + "no implicit conversion is defined for this kind pair. "
            + "Cast the value explicitly (e.g. CAST(x AS DOUBLE)) or fix the call to pass the right type.");
    }
}
