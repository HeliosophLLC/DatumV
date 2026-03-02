using DatumIngest.Catalog.Registries;
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
public sealed class InferOperator : IQueryOperator
{
    private readonly IQueryOperator _source;
    private readonly ModelDescriptor _descriptor;
    private readonly string _sessionName;
    private readonly string _inputColumnName;
    private readonly string _outputColumnName;

    /// <summary>
    /// Creates an InferOperator.
    /// </summary>
    /// <param name="source">Upstream operator providing the input tensor column.</param>
    /// <param name="descriptor">SQL-defined model descriptor holding the bound session.</param>
    /// <param name="sessionName">Session name within the descriptor's <c>BoundSessions</c>. v1 always uses <c>"default"</c>.</param>
    /// <param name="inputColumnName">Source column carrying the input tensor (Float32[] per row).</param>
    /// <param name="outputColumnName">Column to append on the output batch carrying the model's per-row result.</param>
    public InferOperator(
        IQueryOperator source,
        ModelDescriptor descriptor,
        string sessionName,
        string inputColumnName,
        string outputColumnName)
    {
        _source = source;
        _descriptor = descriptor;
        _sessionName = sessionName;
        _inputColumnName = inputColumnName;
        _outputColumnName = outputColumnName;
    }

    /// <summary>The upstream source operator.</summary>
    public IQueryOperator Source => _source;

    /// <summary>The model descriptor whose session this operator dispatches to.</summary>
    public ModelDescriptor Descriptor => _descriptor;

    /// <summary>The session name within the descriptor's bound sessions.</summary>
    public string SessionName => _sessionName;

    /// <summary>The source column carrying the per-row input tensor.</summary>
    public string InputColumnName => _inputColumnName;

    /// <summary>The output column the operator appends carrying the model's result.</summary>
    public string OutputColumnName => _outputColumnName;

    /// <inheritdoc/>
    public IQueryOperator RewriteExpressions(Func<Expression, Expression> rewriter) =>
        new InferOperator(
            _source.RewriteExpressions(rewriter),
            _descriptor,
            _sessionName,
            _inputColumnName,
            _outputColumnName);

    /// <inheritdoc/>
    public OperatorPlanDescription DescribeForExplain() =>
        new OperatorPlanDescription("Infer")
        {
            Properties = new Dictionary<string, string>
            {
                ["model"] = _descriptor.QualifiedName.ToString(),
                ["session"] = _sessionName,
                ["input"] = _inputColumnName,
                ["output"] = _outputColumnName,
            },
            Children = [(_source, null)],
        };

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(ExecutionContext context)
    {
        System.Threading.CancellationToken ct = context.CancellationToken;
        if (!_descriptor.BoundSessions.TryGetValue(_sessionName, out IInferenceSession? session))
        {
            throw new InvalidOperationException(
                $"Model '{_descriptor.QualifiedName}': InferOperator can't find session '{_sessionName}'. " +
                "Sessions are bound at CREATE MODEL time; missing here means the descriptor and operator went out of sync.");
        }
        if (session.Inputs.Count != 1 || session.Outputs.Count != 1)
        {
            throw new InvalidOperationException(
                $"Model '{_descriptor.QualifiedName}': InferOperator v1 supports single-input/single-output sessions " +
                $"but the bound session declares {session.Inputs.Count} input(s) and {session.Outputs.Count} output(s).");
        }

        TensorSpec inputSpec = session.Inputs[0];
        TensorSpec outputSpec = session.Outputs[0];
        bool batchable = IsBatchableShape(inputSpec);

        Pool pool = context.Pool;
        ColumnLookup? outputLookup = null;
        int inputColIdx = -1;
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
                string[] outputNames = new string[sourceLookup.Count + 1];
                for (int i = 0; i < sourceLookup.Count; i++) outputNames[i] = sourceLookup.ColumnNames[i];
                outputNames[^1] = _outputColumnName;
                outputLookup = new ColumnLookup(outputNames);

                sourceCopySlots = new int[sourceLookup.Count];
                for (int i = 0; i < sourceCopySlots.Length; i++) sourceCopySlots[i] = i;
            }

            ValueRef[] perRowOutputs = await DispatchBatchAsync(
                session, inputSpec, outputSpec, sourceBatch, inputColIdx, batchable, ct).ConfigureAwait(false);

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
        for (int rowIdx = 0; rowIdx < rowCount; rowIdx++)
        {
            inputs[rowIdx] = ExtractFloat32Array(sourceBatch[rowIdx][inputColIdx], sourceBatch.Arena);
        }

        bool sameLength = rowCount <= 1 || AllSameLength(inputs);
        if (batchable && sameLength && rowCount > 1)
        {
            await BatchedDispatchAsync(session, inputSpec, outputSpec, inputs, results, ct).ConfigureAwait(false);
        }
        else
        {
            for (int rowIdx = 0; rowIdx < rowCount; rowIdx++)
            {
                ct.ThrowIfCancellationRequested();
                results[rowIdx] = await SingleRowDispatchAsync(
                    session, inputSpec, outputSpec, inputs[rowIdx], ct).ConfigureAwait(false);
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
            outputBag = await session.RunAsync(inputBag, ct).ConfigureAwait(false);

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
        System.Threading.CancellationToken ct)
    {
        TensorBag inputBag = session.CreateInputBag();
        TensorBag? outputBag = null;
        try
        {
            int[] shape = ResolveSingleShape(inputSpec, input.Length);
            inputBag.Add<float>(inputSpec.Name, DataKind.Float32, shape, input);
            outputBag = await session.RunAsync(inputBag, ct).ConfigureAwait(false);

            if (!outputBag.TryGet(outputSpec.Name, out IInferenceTensor outputTensor))
            {
                throw new InvalidOperationException(
                    $"InferOperator: session output bag missing declared output '{outputSpec.Name}'.");
            }
            ReadOnlySpan<float> f32 = outputTensor.AsSpan<float>();
            long product = 1;
            foreach (int dim in outputTensor.Shape) product *= dim;
            return product == 1
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
}
