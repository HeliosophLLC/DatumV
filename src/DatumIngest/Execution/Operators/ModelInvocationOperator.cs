using SkiaSharp;

using DatumIngest.Diagnostics;
using DatumIngest.Functions;
using DatumIngest.Functions.Image;
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
/// <c>IModel.InferBatchAsync</c> call. Sub-batching for GPU memory limits
/// is the model implementation's responsibility, not the operator's.
/// </para>
/// <para>
/// <strong>Arena routing</strong> — the operator stabilises both the source
/// columns and the model's results into the output batch's arena, so consumers
/// reading via <c>batch.Arena</c> resolve everything without arena-mismatch.
/// Same pattern as <c>ProjectOperator</c>.
/// </para>
/// </remarks>
public sealed class ModelInvocationOperator : QueryOperator
{
    private readonly QueryOperator _source;
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
        QueryOperator source,
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
    public QueryOperator Source => _source;

    /// <summary>The unqualified model name resolved against <see cref="ExecutionContext.Models"/>.</summary>
    public string ModelName => _modelName;

    /// <summary>The expressions evaluated to produce per-row required inputs for the model.</summary>
    public IReadOnlyList<Expression> InputExpressions => _inputExpressions;

    /// <summary>The expressions evaluated to produce per-call hyperparameter overrides.</summary>
    public IReadOnlyList<Expression> OptionalExpressions => _optionalExpressions;

    /// <summary>The column name attached to model outputs.</summary>
    public string OutputColumnName => _outputColumnName;

    /// <inheritdoc/>
    public override QueryOperator RewriteExpressions(Func<Expression, Expression> rewriter)
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
    protected override OperatorPlanDescription DescribeForExplainImpl()
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
    protected override async IAsyncEnumerable<RowBatch> ExecuteAsyncImpl(ExecutionContext context)
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

        // Intern the model's element struct schema and (when the per-row value
        // is an array) the wrapping Array<Struct> shape. OutputFields describes
        // the *element* struct's fields; the per-row value may be that struct
        // (e.g. MobileNetV2 → Struct{label, score}) OR an array of it (e.g.
        // SCRFD → Array<Struct{score, x, y, w, h, landmarks}>). We don't know
        // which until we see the first ValueRef out of the model, so the
        // array TypeId is interned lazily inside the scatter loop.
        ushort elementStructTypeId = 0;
        if (model.OutputFields is { } outputFields)
        {
            elementStructTypeId = (ushort)context.Types.InternStructFromColumnInfoFields(outputFields);
        }
        // Lazy intern of Array<Struct> TypeId on first sight of an array result.
        // Cached so the scatter loop only computes it once per operator instance.
        ushort arrayOfStructTypeId = 0;

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

            // Sub-batching is driven by the catalog's IBatchSizePolicy.
            // Default policy (DoublingBatchSizePolicy) ramps from batch=1
            // and grows by powers of two until VRAM pressure settles it;
            // each chunk's size is determined fresh so the ramp can adapt
            // across the upstream rows. Static policies (tests, hosts
            // without a probe) return a constant size that works the same
            // as the prior `model.PreferredBatchSize ?? rowsThisBatch`
            // shape.
            IBatchSizePolicy policy = context.Catalog.Models?.BatchSizePolicy
                ?? StaticBatchSizePolicy.Instance;

            int chunkStart = 0;
            while (chunkStart < rowsThisBatch)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int rowsRemaining = rowsThisBatch - chunkStart;
                int chunkSize = policy.ChooseBatchSize(model, rowsRemaining);
                if (chunkSize <= 0 || chunkSize > rowsRemaining) chunkSize = rowsRemaining;

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
                    EvaluationFrame frame = new(row, sourceBatch.Arena, sourceBatch.Arena, context.Accountant, context.OuterRow, context.SidecarRegistry, context.Types, context.TypeIdTranslations);

                    ValueRef[] rowInputs = new ValueRef[_inputExpressions.Count];
                    for (int argIdx = 0; argIdx < _inputExpressions.Count; argIdx++)
                    {
                        ValueRef raw = await evaluator.EvaluateAsValueRefAsync(_inputExpressions[argIdx], frame, context.CancellationToken).ConfigureAwait(false);
                        rowInputs[argIdx] = CoerceToDeclaredKind(raw, model.InputKinds[argIdx], _modelName, argIdx);
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
                            rowOverrides[i] = await evaluator.EvaluateAsValueRefAsync(_optionalExpressions[i], frame, context.CancellationToken).ConfigureAwait(false);
                        }
                        overrideValues[chunkRowIdx] = rowOverrides;
                    }
                }

                // Step 3: dispatch this chunk. The model returns ValueRefs —
                // managed payloads — and the scatter step below materialises
                // them into outputBatch.Arena via ValueRef.ToDataValue.
                //
                // Tracer hook: notify before/after the call so .trace on
                // (interactive shell) can log per-dispatch shape + timing.
                // Null-checked once per chunk; cost is one branch when
                // tracing is off, an interface call when it's on.
                IModelInvocationTracer? tracer = context.ModelTracer;
                tracer?.OnDispatchStarted(_modelName, chunkSize, inputs, overrideValues);
                long startTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();

                // Pre-dispatch VRAM snapshot for the batch-size policy.
                // -1 sentinel when the probe is unavailable; the policy
                // ignores measurements with either endpoint negative and
                // falls back to its static default.
                long vramBefore = VramProbe.TryGetUsage(
                    out long usedBefore, out _) ? usedBefore : -1;

                IReadOnlyList<ValueRef> modelOutputs;
                try
                {
                    // Pass context.Types so dynamic-shape models (notably
                    // SQL-defined bodies via the procedural adapter) intern
                    // their result struct/array shapes into the caller's
                    // per-query registry. Without this, the TypeIds stamped
                    // inside the body resolve against a foreign registry
                    // and the scatter step below loses struct field names.
                    modelOutputs = await model
                        .InferBatchAsync(inputs, overrideValues, context.Types, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    tracer?.OnDispatchFailed(
                        _modelName,
                        chunkSize,
                        System.Diagnostics.Stopwatch.GetElapsedTime(startTimestamp),
                        ex);
                    throw;
                }

                // Post-dispatch VRAM snapshot + elapsed time — feeds the
                // policy's ramp decision for the next chunk's size. The
                // duration is the load-bearing signal for spill detection:
                // when activations spill into shared GPU memory, NVML still
                // reports dedicated-VRAM at the cap (no growth visible),
                // but per-kernel latency explodes via PCIe.
                long vramAfter = VramProbe.TryGetUsage(
                    out long usedAfter, out _) ? usedAfter : -1;
                double dispatchMs = System.Diagnostics.Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                policy.RecordDispatch(model, chunkSize, vramBefore, vramAfter, dispatchMs);

                tracer?.OnDispatchCompleted(
                    _modelName,
                    chunkSize,
                    System.Diagnostics.Stopwatch.GetElapsedTime(startTimestamp));

                if (modelOutputs.Count != chunkSize)
                {
                    context.ReturnRowBatch(outputBatch);
                    context.ReturnRowBatch(sourceBatch);
                    throw new InvalidOperationException(
                        $"Model '{_modelName}' returned {modelOutputs.Count} outputs for a {chunkSize}-row input chunk.");
                }

                // Step 3.5: parallel pre-encode of managed Image outputs.
                // depth_map_to_image and similar functions return SKBitmap-
                // backed ValueRefs; the default ToDataValue path for those
                // PNG-encodes during the sequential scatter loop below,
                // which serializes ~20ms-per-image of CPU work and was
                // measured at 635ms / 32-row chunk in profiling. Pre-
                // encoding here amortises the PNG/JPEG step across CPU
                // cores; the scatter then writes already-encoded byte[]s
                // into the arena via a cheap memcpy. Cost when no Image
                // outputs are present is a single linear scan (the
                // null-check), no allocations.
                modelOutputs = await PreEncodeImageOutputsAsync(
                    modelOutputs, cancellationToken).ConfigureAwait(false);

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
                    // Pass context.Types so ToDataValue can look up field TypeIds from
                    // the output struct's descriptor and propagate them to nested struct
                    // fields (e.g. SCRFD's `landmarks: Array<Struct{x, y}>` keypoints
                    // get stamped TypeIds, not just the outer detection struct).
                    //
                    // For Array<Struct> outputs the per-row TypeId must be the *array*
                    // shape, not the element struct — `ToDataValue` looks up
                    // `desc.ElementTypeId` on the array's descriptor to propagate field
                    // TypeIds to the inner rows. Stamping the element TypeId directly
                    // on the array's DataValue would short-circuit that hop and rerun
                    // the f0..fN regression. Intern the array TypeId lazily on first
                    // sight of an array result.
                    ValueRef rowValue = modelOutputs[chunkRowIdx];
                    ushort rowTypeId;
                    if (rowValue.IsArray && elementStructTypeId != 0)
                    {
                        if (arrayOfStructTypeId == 0)
                        {
                            arrayOfStructTypeId = (ushort)context.Types.InternArrayType(
                                DataKind.Struct, elementTypeId: elementStructTypeId);
                        }
                        rowTypeId = arrayOfStructTypeId;
                    }
                    else
                    {
                        // No static schema (model.OutputFields is null) — the
                        // model is a dynamic-shape producer like a SQL-defined
                        // body via ProceduralModelAdapter. ToDataValue's
                        // BuildStructArray path falls back to reading the
                        // first element's TypeId when this rowTypeId is 0, so
                        // dynamic-shape Array<Struct> outputs still get their
                        // per-element struct stamps. Primitive outputs have
                        // TypeId 0 anyway and don't lose anything.
                        //
                        // For scalar struct outputs (e.g. `RETURNS ScoredLabel`
                        // from a SQL-defined classifier), the body already
                        // stamped a TypeId on the inline carrier — prefer
                        // that over elementStructTypeId so the cell's
                        // shape resolves through the per-query registry.
                        rowTypeId = elementStructTypeId != 0
                            ? elementStructTypeId
                            : rowValue.TypeId;
                    }
                    outValues[^1] = rowValue.ToDataValue(outputBatch.Arena, rowTypeId, context.Types);
                    outputBatch.Add(outValues);
                }

                yieldedRows += chunkSize;
                chunkStart += chunkSize;
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

    /// <summary>
    /// If the freshly-evaluated argument's kind doesn't match what the model
    /// declared, widen numeric scalars to the declared kind. SQL literals
    /// like <c>300</c> bind to the smallest integer kind that fits (Int16
    /// here) — without this step, calling <c>models.X(image, 300, 300)</c>
    /// against a model that declares <c>[Image, Float64, Float64]</c> would
    /// throw "Cannot read Int16 as Float64" deep inside the model's accessor.
    /// Treats every numeric→numeric widening as implicit (mirrors SQL's
    /// usual numeric promotion rules); non-numeric kind mismatches still
    /// throw — those are genuine signature errors, not type-tightening.
    /// </summary>
    /// <summary>
    /// If any model output is an <see cref="SKBitmap"/>-backed Image
    /// ValueRef, encode every such output to PNG bytes in parallel on
    /// the thread pool and return a new array with the encoded
    /// (<c>byte[]</c>-backed) ValueRefs in place of the originals. When
    /// no <see cref="SKBitmap"/> outputs are present, returns the
    /// original array unchanged — single linear scan, no allocations.
    /// </summary>
    /// <remarks>
    /// PNG/JPEG encoding is the expensive step in
    /// <see cref="ValueRef.ToDataValue"/> for SKBitmap-backed Images —
    /// measured at ~20 ms per row, serialized across the scatter loop.
    /// Parallelising the encode amortises that 20 ms × N work across
    /// CPU cores; the subsequent sequential scatter just memcpy's
    /// the already-encoded bytes into the arena.
    /// </remarks>
    private static async ValueTask<IReadOnlyList<ValueRef>> PreEncodeImageOutputsAsync(
        IReadOnlyList<ValueRef> outputs, CancellationToken cancellationToken)
    {
        // Fast path: scan for any SKBitmap-backed output. None → return
        // the original list with zero allocation, zero parallel-task
        // overhead.
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
