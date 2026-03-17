using DatumIngest.Catalog.Registries;
using DatumIngest.Diagnostics;
using DatumIngest.Functions;
using DatumIngest.Inference;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Dispatches an <see cref="IInferenceSession"/> over every row in each
/// incoming <see cref="RowBatch"/>, packing rows into one batched
/// <c>Session.Run</c> when the session and inputs allow it, and falling
/// back to per-row dispatch otherwise. Produced by
/// <see cref="ModelBodyLowerer"/> when a SQL-defined model's body is
/// lowered into a column pipeline — replaces the body's <c>infer()</c>
/// call with a dedicated operator so cross-row batching becomes the
/// natural shape instead of an after-the-fact optimisation.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Batching strategy.</strong> A session is batchable when its
/// declared input shape has rank ≥ 2 with a leading dynamic dim — the
/// canonical <c>[batch, feature_dims...]</c> shape ONNX classifiers /
/// embedders / recognizers use. For those, when every row in a batch
/// shares the same feature-element count, the operator packs them into
/// a <c>[B, ...]</c> tensor and runs one <c>Session.Run</c>. When the
/// counts differ (variable-shape detectors) or the session shape doesn't
/// admit a batch dim (rank-1 dynamic, fully concrete shapes), it falls
/// back to per-row dispatch — still inside the operator boundary, so
/// the residency lease and cancellation token apply.
/// </para>
/// <para>
/// <strong>Element kinds.</strong> v1 supports <c>Float32</c> for both
/// input and output tensors — the kind every shipping SQL-defined model
/// uses today. <c>Int32</c> and <c>Int64</c> are mechanical follow-ups
/// (mirror <see cref="DatumIngest.Functions.InferFunction"/>'s switch).
/// </para>
/// </remarks>
public sealed class InferOperator : QueryOperator
{
    private readonly QueryOperator _source;
    private readonly ModelDescriptor _descriptor;
    private readonly string _sessionName;
    private readonly string _inputColumnName;
    private readonly string? _shapeColumnName;
    private readonly string _outputColumnName;

    /// <summary>
    /// Creates an InferOperator.
    /// </summary>
    /// <param name="source">Upstream operator providing the input tensor column.</param>
    /// <param name="descriptor">SQL-defined model descriptor holding the bound session.</param>
    /// <param name="sessionName">Session name within the descriptor's <c>BoundSessions</c>. v1 always uses <c>"default"</c>.</param>
    /// <param name="inputColumnName">Source column carrying the input tensor (Float32[] per row).</param>
    /// <param name="shapeColumnName">
    /// Optional source column carrying the explicit input tensor shape as <c>Int32[]</c>
    /// per row. Required when the session's input spec has multiple dynamic dimensions
    /// (e.g. PP-OCR-det's <c>[-1, 3, -1, -1]</c>) — the shape resolver can't pick a
    /// unique answer from the array length alone. When null, the shape is resolved
    /// from the spec (works for ≤1 dynamic dim).
    /// </param>
    /// <param name="outputColumnName">Column to append on the output batch carrying the model's per-row result.</param>
    public InferOperator(
        QueryOperator source,
        ModelDescriptor descriptor,
        string sessionName,
        string inputColumnName,
        string outputColumnName,
        string? shapeColumnName = null)
    {
        _source = source;
        _descriptor = descriptor;
        _sessionName = sessionName;
        _inputColumnName = inputColumnName;
        _shapeColumnName = shapeColumnName;
        _outputColumnName = outputColumnName;
    }

    /// <summary>The upstream source operator.</summary>
    public QueryOperator Source => _source;

    /// <summary>The model descriptor whose session this operator dispatches to.</summary>
    public ModelDescriptor Descriptor => _descriptor;

    /// <summary>The session name within the descriptor's bound sessions.</summary>
    public string SessionName => _sessionName;

    /// <summary>The source column carrying the per-row input tensor.</summary>
    public string InputColumnName => _inputColumnName;

    /// <summary>
    /// The optional source column carrying the per-row explicit shape
    /// (Int32[]). Null means "resolve shape from the spec." See the
    /// constructor parameter doc for when this is required.
    /// </summary>
    public string? ShapeColumnName => _shapeColumnName;

    /// <summary>The output column the operator appends carrying the model's result.</summary>
    public string OutputColumnName => _outputColumnName;

    /// <inheritdoc/>
    public override QueryOperator RewriteExpressions(Func<Expression, Expression> rewriter) =>
        new InferOperator(
            _source.RewriteExpressions(rewriter),
            _descriptor,
            _sessionName,
            _inputColumnName,
            _outputColumnName,
            _shapeColumnName);

    /// <inheritdoc/>
    protected override OperatorPlanDescription DescribeForExplainImpl()
    {
        Dictionary<string, string> properties = new()
        {
            ["model"] = _descriptor.QualifiedName.ToString(),
            ["session"] = _sessionName,
            ["input"] = _inputColumnName,
            ["output"] = _outputColumnName,
        };
        if (_shapeColumnName is not null)
        {
            properties["shape"] = _shapeColumnName;
        }
        return new OperatorPlanDescription("Infer")
        {
            Properties = properties,
            Children = [(_source, null)],
        };
    }

    /// <inheritdoc/>
    protected override async IAsyncEnumerable<RowBatch> ExecuteAsyncImpl(ExecutionContext context)
    {
        System.Threading.CancellationToken ct = context.CancellationToken;
        if (!_descriptor.BoundSessions.TryGetValue(_sessionName, out IInferenceSession? session))
        {
            throw new InvalidOperationException(
                $"Model '{_descriptor.QualifiedName}': InferOperator can't find session '{_sessionName}'. " +
                "Sessions are bound at CREATE MODEL time; missing here means the descriptor and operator went out of sync.");
        }
        if (session.Inputs.Count != 1)
        {
            throw new InvalidOperationException(
                $"Model '{_descriptor.QualifiedName}': InferOperator v1 supports single-input sessions " +
                $"but the bound session declares {session.Inputs.Count} input(s). " +
                "Multi-input bodies bail to MIO + ProceduralModelAdapter via the lowerer's struct-DECLARE check.");
        }

        TensorSpec inputSpec = session.Inputs[0];
        // Multi-output sessions are allowed; we pick the first declared
        // output, matching the documented v1 behavior (`infer()` returns
        // the primary output, by HuggingFace optimum convention listed
        // first) and the scalar `InferFunction`'s read path. U²-Net's
        // seven deep-supervision tensors d0..d6 are the canonical case —
        // d0 is the final fused saliency map and the only one downstream
        // consumers care about.
        TensorSpec outputSpec = session.Outputs[0];
        // When the body supplies explicit shapes per row, force per-row
        // dispatch — cross-row packing isn't safe when each row may have
        // a different declared shape (the typical case: variable-shape
        // detectors like PP-OCR-det where each image is resized to its
        // own aspect-preserving dims).
        bool batchable = _shapeColumnName is null && IsBatchableShape(inputSpec);

        Pool pool = context.Pool;
        ColumnLookup? outputLookup = null;
        int inputColIdx = -1;
        int shapeColIdx = -1;
        int[]? sourceCopySlots = null;

        await foreach (RowBatch sourceBatch in _source.ExecuteAsync(context).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (sourceBatch.Count == 0)
            {
                context.ReturnRowBatch(sourceBatch);
                continue;
            }

            if (outputLookup is null)
            {
                ColumnLookup sourceLookup = sourceBatch.ColumnLookup;
                if (!sourceLookup.TryGetColumnOrdinal(_inputColumnName, out inputColIdx))
                {
                    context.ReturnRowBatch(sourceBatch);
                    throw new InvalidOperationException(
                        $"InferOperator: input column '{_inputColumnName}' not found in upstream batch. " +
                        $"Available: [{string.Join(", ", sourceLookup.ColumnNames)}].");
                }
                if (_shapeColumnName is not null
                    && !sourceLookup.TryGetColumnOrdinal(_shapeColumnName, out shapeColIdx))
                {
                    context.ReturnRowBatch(sourceBatch);
                    throw new InvalidOperationException(
                        $"InferOperator: shape column '{_shapeColumnName}' not found in upstream batch. " +
                        $"Available: [{string.Join(", ", sourceLookup.ColumnNames)}].");
                }
                string[] outputNames = new string[sourceLookup.Count + 1];
                for (int i = 0; i < sourceLookup.Count; i++) outputNames[i] = sourceLookup.ColumnNames[i];
                outputNames[^1] = _outputColumnName;
                outputLookup = new ColumnLookup(outputNames);

                sourceCopySlots = new int[sourceLookup.Count];
                for (int i = 0; i < sourceCopySlots.Length; i++) sourceCopySlots[i] = i;
            }

            ValueRef[] perRowOutputs = await DispatchBatchAsync(
                session, inputSpec, outputSpec, sourceBatch, inputColIdx, shapeColIdx, batchable, ct).ConfigureAwait(false);

            RowBatch outputBatch = context.RentRowBatch(outputLookup, sourceBatch.Count);
            for (int rowIdx = 0; rowIdx < sourceBatch.Count; rowIdx++)
            {
                Row sourceRow = sourceBatch[rowIdx];
                DataValue[] outValues = pool.RentDataValues(outputLookup.Count);
                for (int slot = 0; slot < sourceCopySlots!.Length; slot++)
                {
                    outValues[slot] = DataValueRetention.Stabilize(
                        sourceRow[sourceCopySlots[slot]], sourceBatch.Arena, outputBatch.Arena);
                }
                outValues[^1] = perRowOutputs[rowIdx].ToDataValue(
                    outputBatch.Arena, perRowOutputs[rowIdx].TypeId, context.Types);
                outputBatch.Add(outValues);
            }

            context.ReturnRowBatch(sourceBatch);
            yield return outputBatch;
        }
    }

    /// <summary>
    /// Returns whether the session's input shape admits cross-row batching:
    /// rank ≥ 2 with a dynamic leading dim, the canonical
    /// <c>[batch, feature_dims...]</c> shape. Anything else (rank 1, all-
    /// concrete shapes, multi-dynamic shapes) falls back to per-row.
    /// </summary>
    private static bool IsBatchableShape(TensorSpec spec)
    {
        if (spec.Shape.Count < 2) return false;
        if (spec.Shape[0] is not null) return false; // leading dim must be dynamic
        // Every non-leading dim must be concrete for batched packing —
        // a second dynamic dim would mean per-row feature shapes can vary,
        // which the batched path can't absorb.
        for (int i = 1; i < spec.Shape.Count; i++)
        {
            if (spec.Shape[i] is null) return false;
        }
        return true;
    }

    /// <summary>
    /// Dispatches one source batch: picks the batched packed path when the
    /// session admits it AND every row in the batch has the same input
    /// element count, falls back to per-row otherwise. Returns one
    /// <see cref="ValueRef"/> per row in source order.
    /// </summary>
    private async Task<ValueRef[]> DispatchBatchAsync(
        IInferenceSession session,
        TensorSpec inputSpec,
        TensorSpec outputSpec,
        RowBatch sourceBatch,
        int inputColIdx,
        int shapeColIdx,
        bool batchable,
        System.Threading.CancellationToken ct)
    {
        int rowCount = sourceBatch.Count;
        ValueRef[] results = new ValueRef[rowCount];

        // Pull every row's input array. We need them as managed float[]
        // anyway for both code paths (the batched one packs them into one
        // contiguous buffer; the per-row one calls AddInputTensor below).
        // ValueRef.Materialized is the existing managed payload for
        // primitive arrays (Step 1's no-arena DECLARE chains keep this
        // managed end-to-end), so no arena reads.
        float[][] inputs = new float[rowCount][];
        int[]?[]? perRowShapes = _shapeColumnName is null ? null : new int[]?[rowCount];
        for (int rowIdx = 0; rowIdx < rowCount; rowIdx++)
        {
            inputs[rowIdx] = ExtractFloat32Array(sourceBatch[rowIdx][inputColIdx], sourceBatch.Arena);
            if (perRowShapes is not null)
            {
                perRowShapes[rowIdx] = ExtractInt32Shape(sourceBatch[rowIdx][shapeColIdx], sourceBatch.Arena);
            }
        }

        bool sameLength = rowCount <= 1 || AllSameLength(inputs);
        ExecutionTracer.Write(
            $"[infer-op] {_descriptor.Name}: dispatch rowCount={rowCount} batchable={batchable} sameLength={sameLength} perRowLen={inputs[0].Length}");
        if (batchable && sameLength && rowCount > 1)
        {
            ExecutionTracer.Write(
                $"[infer-op] {_descriptor.Name}: batched-path pre-Run B={rowCount} bytes={(long)rowCount * inputs[0].Length * 4}");
            await BatchedDispatchAsync(session, inputSpec, outputSpec, inputs, results, ct).ConfigureAwait(false);
            ExecutionTracer.Write($"[infer-op] {_descriptor.Name}: batched-path post-Run B={rowCount}");

        }
        else
        {
            ExecutionTracer.Write($"[infer-op] {_descriptor.Name}: per-row-path rowCount={rowCount}");
            for (int rowIdx = 0; rowIdx < rowCount; rowIdx++)
            {
                ct.ThrowIfCancellationRequested();
                int[]? explicitShape = perRowShapes?[rowIdx];
                results[rowIdx] = await SingleRowDispatchAsync(
                    session, inputSpec, outputSpec, inputs[rowIdx], explicitShape, ct).ConfigureAwait(false);
                if ((rowIdx & 0x1f) == 0)
                    ExecutionTracer.Write($"[infer-op] {_descriptor.Name}: per-row {rowIdx + 1}/{rowCount} done");
            }
        }
        return results;
    }

    private static bool AllSameLength(float[][] inputs)
    {
        int len0 = inputs[0].Length;
        for (int i = 1; i < inputs.Length; i++)
        {
            if (inputs[i].Length != len0) return false;
        }
        return true;
    }

    /// <summary>
    /// Packs <paramref name="inputs"/> into a single <c>[B, feature_dims...]</c>
    /// tensor and runs one <c>Session.Run</c>. Distributes the output back
    /// to per-row <see cref="ValueRef"/>s by splitting the flat output span
    /// into equal-sized slices.
    /// </summary>
    private static async Task BatchedDispatchAsync(
        IInferenceSession session,
        TensorSpec inputSpec,
        TensorSpec outputSpec,
        float[][] inputs,
        ValueRef[] results,
        System.Threading.CancellationToken ct)
    {
        int rowCount = inputs.Length;
        int perRowLen = inputs[0].Length;
        int totalFloats = rowCount * perRowLen;

        float[] packed = new float[totalFloats];
        for (int rowIdx = 0; rowIdx < rowCount; rowIdx++)
        {
            Buffer.BlockCopy(inputs[rowIdx], 0, packed, rowIdx * perRowLen * sizeof(float), perRowLen * sizeof(float));
        }

        // Shape: [B, feature_dims...]. The IsBatchableShape gate guarantees
        // shape[0] is dynamic and shape[1..] are all concrete, so we can
        // build the resolved shape directly without solving for a hidden
        // dynamic dim.
        int[] shape = new int[inputSpec.Shape.Count];
        shape[0] = rowCount;
        for (int i = 1; i < inputSpec.Shape.Count; i++)
        {
            shape[i] = inputSpec.Shape[i]!.Value;
        }

        TensorBag inputBag = session.CreateInputBag();
        TensorBag? outputBag = null;
        try
        {
            inputBag.Add<float>(inputSpec.Name, DataKind.Float32, shape, packed);
            ExecutionTracer.Write($"[infer-op] BatchedDispatch: pre-RunAsync shape=[{string.Join(",", shape)}] floats={packed.Length}");
            outputBag = await session.RunAsync(inputBag, ct).ConfigureAwait(false);
            ExecutionTracer.Write($"[infer-op] BatchedDispatch: post-RunAsync");

            if (!outputBag.TryGet(outputSpec.Name, out IInferenceTensor outputTensor))
            {
                throw new InvalidOperationException(
                    $"InferOperator: session output bag missing declared output '{outputSpec.Name}'.");
            }

            ReadOnlySpan<float> outputSpan = outputTensor.AsSpan<float>();
            if (outputSpan.Length % rowCount != 0)
            {
                throw new InvalidOperationException(
                    $"InferOperator: output element count {outputSpan.Length} is not divisible by row count {rowCount}; " +
                    "expected B rows × M output elements after batched dispatch.");
            }
            int outputPerRow = outputSpan.Length / rowCount;
            for (int rowIdx = 0; rowIdx < rowCount; rowIdx++)
            {
                float[] rowOutput = outputSpan.Slice(rowIdx * outputPerRow, outputPerRow).ToArray();
                results[rowIdx] = outputPerRow == 1
                    ? ValueRef.FromFloat32(rowOutput[0])
                    : ValueRef.FromPrimitiveArray(rowOutput, DataKind.Float32);
            }
        }
        finally
        {
            inputBag.Dispose();
            outputBag?.Dispose();
        }
    }

    /// <summary>
    /// Single-row fallback when the session isn't batchable or rows have
    /// non-uniform sizes. Mirrors <see cref="DatumIngest.Functions.InferFunction"/>'s
    /// per-call dispatch path — same shape-resolution + output-extraction
    /// rules, just driven from a row's input array instead of a
    /// <see cref="ValueRef"/> argument.
    /// </summary>
    private static async Task<ValueRef> SingleRowDispatchAsync(
        IInferenceSession session,
        TensorSpec inputSpec,
        TensorSpec outputSpec,
        float[] input,
        int[]? explicitShape,
        System.Threading.CancellationToken ct)
    {
        TensorBag inputBag = session.CreateInputBag();
        TensorBag? outputBag = null;
        try
        {
            // Explicit shape wins when supplied — required for sessions
            // with multiple dynamic dims (PP-OCR-det's [-1, 3, -1, -1]).
            // Otherwise resolve from the spec.
            int[] shape = explicitShape ?? ResolveSingleShape(inputSpec, input.Length);
            // Sanity check: the shape's product must match the input
            // element count regardless of whether it came from the spec
            // or the body. Catches the typical "wrong shape literal in
            // the SQL" bug at the operator boundary, not deep in ORT.
            long product = 1;
            foreach (int dim in shape) product *= dim;
            if (product != input.Length)
            {
                throw new InvalidOperationException(
                    $"InferOperator: shape [{string.Join(", ", shape)}] (product {product}) "
                    + $"doesn't match the Float32[] input's element count {input.Length}.");
            }
            inputBag.Add<float>(inputSpec.Name, DataKind.Float32, shape, input);
            ExecutionTracer.Write($"[infer-op] SingleRow: pre-RunAsync shape=[{string.Join(",", shape)}] floats={input.Length}");
            outputBag = await session.RunAsync(inputBag, ct).ConfigureAwait(false);
            ExecutionTracer.Write($"[infer-op] SingleRow: post-RunAsync");

            if (!outputBag.TryGet(outputSpec.Name, out IInferenceTensor outputTensor))
            {
                throw new InvalidOperationException(
                    $"InferOperator: session output bag missing declared output '{outputSpec.Name}'.");
            }
            ReadOnlySpan<float> f32 = outputTensor.AsSpan<float>();
            long outputProduct = 1;
            foreach (int dim in outputTensor.Shape) outputProduct *= dim;
            return outputProduct == 1
                ? ValueRef.FromFloat32(f32[0])
                : ValueRef.FromPrimitiveArray(f32.ToArray(), DataKind.Float32);
        }
        finally
        {
            inputBag.Dispose();
            outputBag?.Dispose();
        }
    }

    /// <summary>
    /// Resolves <paramref name="spec"/>'s shape against
    /// <paramref name="elementCount"/> for the single-row path. Mirrors
    /// <see cref="DatumIngest.Functions.InferFunction"/>'s ResolveShape: one
    /// dynamic dim absorbs whatever's left over after dividing by the
    /// concrete dims; multi-dynamic shapes are rejected.
    /// </summary>
    private static int[] ResolveSingleShape(TensorSpec spec, int elementCount)
    {
        IReadOnlyList<int?> declared = spec.Shape;
        if (declared.Count == 0) return Array.Empty<int>();

        int[] resolved = new int[declared.Count];
        long product = 1;
        int dynamicCount = 0;
        int dynamicIndex = -1;
        for (int i = 0; i < declared.Count; i++)
        {
            if (declared[i] is int fixedDim)
            {
                resolved[i] = fixedDim;
                product *= fixedDim;
            }
            else
            {
                dynamicCount++;
                dynamicIndex = i;
            }
        }
        if (dynamicCount == 0) return resolved;
        if (dynamicCount == 1)
        {
            if (product == 0 || elementCount % product != 0)
            {
                throw new InvalidOperationException(
                    $"InferOperator: cannot fit {elementCount} elements into shape " +
                    $"[{string.Join(", ", declared.Select(d => d?.ToString() ?? "?"))}].");
            }
            resolved[dynamicIndex] = (int)(elementCount / product);
            return resolved;
        }
        throw new NotSupportedException(
            $"InferOperator: input shape with multiple dynamic dims is not supported in single-row dispatch; " +
            $"got {dynamicCount} dynamic dims in [{string.Join(", ", declared.Select(d => d?.ToString() ?? "?"))}].");
    }

    /// <summary>
    /// Pulls a managed Float32[] out of a DataValue cell. Scalar Float32
    /// values wrap into a single-element array so the session marshaller
    /// gets a uniform shape. Fast path for arrays: the cell's underlying
    /// ValueRef materialised payload IS already a <see cref="float"/>[]
    /// (the chained-managed-payload invariant from step 1). Slow path:
    /// arena-backed value, read its span and copy.
    /// </summary>
    private static float[] ExtractFloat32Array(DataValue cell, Arena arena)
    {
        if (cell.IsNull)
        {
            throw new InvalidOperationException(
                "InferOperator: input column cell is null. The body's preprocessing must produce a non-null tensor for every row.");
        }
        if (cell.Kind != DataKind.Float32)
        {
            throw new InvalidOperationException(
                $"InferOperator: input column expected Float32{(cell.IsArray ? "[]" : "")} but the body produced {cell.Kind}{(cell.IsArray ? "[]" : "")}. " +
                "v1 lowers infer() only when both input and output are Float32; other element kinds stay on the row-at-a-time adapter path.");
        }
        if (cell.IsArray)
        {
            return cell.AsArraySpan<float>(arena).ToArray();
        }
        // Scalar Float32 — wrap into a length-1 array. The session's input
        // marshaller picks up the row's element count and resolves the
        // shape accordingly (rank-1 dynamic, leading-batch-dim with rank-1
        // feature, etc.).
        return [cell.AsFloat32()];
    }

    /// <summary>
    /// Pulls a shape array out of a DataValue cell. Accepts Int32[] or
    /// Int64[] (coerced to Int32). Used when the body supplies an
    /// explicit shape via <c>infer(value, shape)</c>.
    /// </summary>
    private static int[] ExtractInt32Shape(DataValue cell, Arena arena)
    {
        if (cell.IsNull)
        {
            throw new InvalidOperationException(
                "InferOperator: shape column cell is null. The body's shape expression must produce a non-null Int32[] for every row.");
        }
        if (!cell.IsArray)
        {
            throw new InvalidOperationException(
                $"InferOperator: shape column expected Int32[] or Int64[], got {cell.Kind} (not an array).");
        }
        if (cell.Kind == DataKind.Int32)
        {
            return cell.AsArraySpan<int>(arena).ToArray();
        }
        if (cell.Kind == DataKind.Int64)
        {
            ReadOnlySpan<long> src = cell.AsArraySpan<long>(arena);
            int[] result = new int[src.Length];
            for (int i = 0; i < src.Length; i++) result[i] = checked((int)src[i]);
            return result;
        }
        throw new InvalidOperationException(
            $"InferOperator: shape column kind {cell.Kind}[] not supported; use Int32[] or Int64[].");
    }
}
