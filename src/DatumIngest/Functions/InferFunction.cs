using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

using DatumIngest.Diagnostics;
using DatumIngest.Execution;
using DatumIngest.Inference;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions;

/// <summary>
/// The runtime bridge from a <c>CREATE MODEL</c> body to its bound
/// <see cref="IInferenceSession"/>. Resolves the current model from the
/// evaluation frame, marshals the call-site argument into a
/// <see cref="TensorBag"/>, dispatches to the session, and converts the
/// first declared output tensor back to a <see cref="ValueRef"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Output.</strong> Surfaces the first declared output as a
/// primitive array (or a scalar when shape product is 1). Multi-output
/// sessions are common — BERT-family encoders emit
/// <c>last_hidden_state</c> + <c>pooler_output</c>, U²-Net emits seven
/// deep-supervision tensors, RT-DETR emits <c>logits</c> + <c>pred_boxes</c>.
/// For these, <c>infer()</c> returns the first by convention (HuggingFace
/// optimum, transformers ONNX export, and most tooling list the primary
/// output first). When you need every output, use
/// <see cref="InferOutputsFunction">infer_outputs()</see>, which returns
/// the full <see cref="DataKind.Struct"/> keyed by ONNX output name.
/// </para>
/// <para>
/// <strong>Multi-input.</strong> Pass a struct argument: <c>infer({a: x, b: y})</c>
/// binds each session input by name (case-insensitive). For multi-input
/// sessions with multiple dynamic dimensions per input — BERT-family
/// transformers where every input is <c>[batch, seq_len]</c> — pass a
/// parallel struct of shape arrays as the second argument:
/// <c>infer({a: x, b: y}, {a: [1, n], b: [1, n]})</c>.
/// </para>
/// <para>
/// <strong>Argument marshalling.</strong>
/// <list type="bullet">
///   <item><description>Numeric scalars are wrapped into a one-element
///   tensor of the session input's declared shape (the shape's null
///   "dynamic" dimensions are replaced with 1).</description></item>
///   <item><description>Primitive arrays passed via
///   <see cref="ValueRef.FromPrimitiveArray{T}"/> flow through directly,
///   preserving the array's flat element count.</description></item>
///   <item><description>Struct arguments unpack by field name into the
///   session's input bag; one field per session input.</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Output marshalling.</strong> The single output tensor is read
/// flat via <see cref="IInferenceTensor.AsSpan{T}"/>. Shape product 1
/// surfaces as a scalar; everything else surfaces as a primitive array
/// of the matching kind.
/// </para>
/// <para>
/// <strong>Purity.</strong> <see cref="IsPure"/> is <see langword="false"/>:
/// model dispatch is expensive and may exhibit non-determinism (sampling
/// LLMs, cuDNN scheduling differences). The planner's CSE pass treats
/// each call site as its own evaluation.
/// </para>
/// </remarks>
public sealed class InferFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "infer";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;

    /// <inheritdoc />
    public static string Description =>
        "Dispatches the currently-bound model session on its input and returns "
        + "the FIRST declared output. Only callable from inside a CREATE MODEL "
        + "body — outside a model frame there is no session to dispatch to. "
        + "infer(value) resolves the tensor shape from the session's input "
        + "spec (works when ≤1 dynamic dim). "
        + "infer(value, shape Int32[]) overrides the shape explicitly — required "
        + "when the input spec has multiple dynamic dims (e.g. PP-OCR-det's "
        + "[-1, 3, -1, -1] where batch + H + W are all dynamic). "
        + "infer({a: x, b: y}) and infer({a: x, b: y}, {a: [..], b: [..]}) "
        + "feed a multi-input session by matching struct field names to session "
        + "input names case-insensitively. For multi-output sessions where you "
        + "need every output, use infer_outputs() instead.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        // ───────── Named-session forms (multi-session bundles) ─────────
        // Every "named" variant takes the session alias as the FIRST String
        // argument and dispatches to BoundSessions[alias]. The alias must
        // have been declared in the CREATE MODEL's USING ... AS clause.
        // Listed BEFORE the implicit-"default" forms so a String first arg
        // routes to the named path even though the implicit forms' "Any"
        // first param would also match.

        // 3-arg named multi-input + shapes form. Same shape as the
        // 2-arg (Struct, Struct) below but prefixed with the alias.
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("session", DataKindMatcher.Exact(DataKind.String), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("inputs",  DataKindMatcher.Exact(DataKind.Struct), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("shapes",  DataKindMatcher.Exact(DataKind.Struct), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.SameAs(1)),
        // 3-arg named single-input + explicit shape form.
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("session", DataKindMatcher.Exact(DataKind.String), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("value",   DataKindMatcher.Any),
                new ParameterSpec("shape",   DataKindMatcher.Family(DataKindFamily.IntegerFamily), IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.SameAs(1)),
        // 2-arg named multi-input form. Listed BEFORE (String, Any) so
        // the matcher prefers the more specific (String, Struct) shape
        // when both apply — same rule as the un-named pair below.
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("session", DataKindMatcher.Exact(DataKind.String), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("inputs",  DataKindMatcher.Exact(DataKind.Struct), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.SameAs(1)),
        // 2-arg named single-input form. The bread-and-butter case for
        // multi-session bundles: `infer('encoder', img_tensor)`.
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("session", DataKindMatcher.Exact(DataKind.String), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("value",   DataKindMatcher.Any),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.SameAs(1)),

        // ───────── Implicit-"default" forms (legacy single-session) ─────────

        // 2-arg multi-input form: struct of tensors + struct of explicit
        // shapes, fields matched by name. Required for BERT-family
        // transformers where every input has multiple dynamic dims
        // ([batch, seq_len]) so the per-input shape can't be inferred
        // from a single 1-d array length. Listed BEFORE the (Any, Int[])
        // form so the matcher prefers the more specific (Struct, Struct)
        // shape when both could apply.
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("inputs", DataKindMatcher.Exact(DataKind.Struct), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("shapes", DataKindMatcher.Exact(DataKind.Struct), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.SameAs(0)),
        // 2-arg single-input form: value + explicit shape array. Required
        // when the session's input shape has multiple dynamic dimensions
        // (e.g. PP-OCR-det's `[-1, 3, -1, -1]` — batch + H + W are all
        // dynamic and can't be disambiguated from the array length alone).
        // The shape array is passed straight through to the session as the
        // tensor's dims.
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Any),
                new ParameterSpec("shape", DataKindMatcher.Family(DataKindFamily.IntegerFamily), IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.SameAs(0)),
        // 1-arg form: any numeric scalar, primitive array, or struct. The
        // return rule is SameAs(0) because v1 echoes the argument kind —
        // the session's actual output kind is decided at run time, and the
        // surrounding model body's RETURN coercion takes care of any
        // mismatch with the declared model return type. For a struct
        // argument, each session input's shape is resolved from its own
        // input spec (works when each spec has ≤1 dynamic dim).
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("value", DataKindMatcher.Any)],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.SameAs(0)),
    ];

    /// <inheritdoc />
    public static BodyScopeRequirement BodyScope => BodyScopeRequirement.ModelBody;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<InferFunction>(argumentKinds);

    /// <inheritdoc />
    public bool IsPure => false;

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
        => DispatchAndReadAsync(
            arguments, frame, "infer",
            (session, bag, _, modelName, arena) => ReadFirstOutput(session, bag, modelName, arena),
            cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Packs the column of per-row Float32 input tensors into one
    /// <c>[B, feature_dims...]</c> tensor and runs <c>Session.Run</c> once,
    /// then splits the flat output back into per-row results. Falls back
    /// to the default per-row loop in any of these cases:
    /// <list type="bullet">
    /// <item><paramref name="rowCount"/> ≤ 1 — no batching win.</item>
    /// <item>Multi-input dispatch (struct argument form) — packing across
    /// rows for struct-shaped inputs is not implemented here; routes back
    /// to per-row.</item>
    /// <item>Explicit shape argument with anything other than a uniform
    /// <c>[1, …]</c> leading-dim across all rows — true per-row shape
    /// variability is the case batching can't absorb. The uniform
    /// <c>[1, …]</c> form is recombined as <c>[N, …]</c> and packed (the
    /// common case for SQL bodies that hardcode a batch-1 shape against
    /// a dynamic-batch ONNX session).</item>
    /// <item>Session input rank &lt; 2 or with non-dynamic leading dim, or
    /// any non-leading dynamic dim — not a batchable shape.</item>
    /// <item>Row inputs have differing element counts — variable-shape
    /// detectors that resize per-image to different dims.</item>
    /// </list>
    /// In every fallback the per-row loop produces results indistinguishable
    /// from the batched path; this method is purely a performance override.
    /// </remarks>
    public async ValueTask<ValueRef[]> ExecuteBatchAsync(
        ReadOnlyMemory<ValueRef>[] argumentColumns,
        int rowCount,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        // The columnar packing optimisation applies to single-input calls
        // against a batchable session — either the 1-arg form or the 2-arg
        // explicit-shape form when every row passes the same `[1, ...]`
        // shape (which is what a SQL body using `infer(tensor, [1, 3, H, W])`
        // produces — uniform across rows because the literal is the same).
        // Multi-input struct dispatch and non-batchable sessions fall through
        // to the per-row default. We have to load the default session here
        // to inspect its input shape; this is the one place outside
        // DispatchAndReadAsync where lazy session loading happens before the
        // SQL body actually calls infer().
        if (rowCount <= 1
            || (argumentColumns.Length != 1 && argumentColumns.Length != 2)
            || frame.CurrentModel is not { } model
            || !model.BoundSessions.ContainsKey("default"))
        {
            DatumActivity.Scalars.Trace(
                $"infer.batch fallback=preconditions rows={rowCount} args={argumentColumns.Length} "
                + $"model={(frame.CurrentModel?.Name ?? "<none>")}");
            return await ScalarFunctionBatchHelpers.DefaultLoop(
                this, argumentColumns, rowCount, frame, cancellationToken).ConfigureAwait(false);
        }

        IInferenceSession session = await model.BoundSessions
            .ResolveAsync("default", cancellationToken)
            .ConfigureAwait(false);
        // Batched packing dispatches over the input element kind — Float32
        // for image-classifier / embedder workloads, Int64 for tokenized
        // text encoders (CLIP, BERT), Int32 for the same shape in older
        // exports. Sessions whose input kind we don't know how to pack fall
        // through to the per-row default loop. Output reading stays Float32
        // (line 433 below uses AsSpan<float>); models with non-Float32
        // outputs fall back to per-row dispatch too.
        DataKind inputKind = session.Inputs.Count > 0 ? session.Inputs[0].ElementKind : DataKind.Float32;
        DataKind outputKind = session.Outputs.Count > 0 ? session.Outputs[0].ElementKind : DataKind.Float32;
        bool packableInput = inputKind is DataKind.Float32 or DataKind.Int64 or DataKind.Int32;
        if (session.Inputs.Count != 1
            || !packableInput
            || outputKind != DataKind.Float32
            || !HasDynamicLeadingDim(session.Inputs[0]))
        {
            DatumActivity.Scalars.Trace(
                $"infer.batch fallback=session-shape model={model.Name} inputs={session.Inputs.Count} "
                + $"inputKind={(session.Inputs.Count > 0 ? session.Inputs[0].ElementKind.ToString() : "n/a")} "
                + $"outputKind={(session.Outputs.Count > 0 ? session.Outputs[0].ElementKind.ToString() : "n/a")} "
                + $"shape0={(session.Inputs.Count > 0 ? (session.Inputs[0].Shape.Count > 0 ? (session.Inputs[0].Shape[0]?.ToString() ?? "null") : "rank0") : "n/a")}");
            return await ScalarFunctionBatchHelpers.DefaultLoop(
                this, argumentColumns, rowCount, frame, cancellationToken).ConfigureAwait(false);
        }

        // Resolve the packed shape's trailing dims. Two sources:
        //   - 1-arg form: the session spec's trailing dims must all be
        //     concrete (no other dim source is available).
        //   - 2-arg form: every row must pass the same `[1, ...]` explicit
        //     shape (uniform leading 1, identical trailing dims). The
        //     trailing dims come from the literal, which lets us batch
        //     even when the session spec leaves them dynamic.
        int[]? packedShape;
        if (argumentColumns.Length == 1)
        {
            if (!TryBuildPackedShapeFromSpec(session.Inputs[0], rowCount, out packedShape))
            {
                DatumActivity.Scalars.Trace(
                    $"infer.batch fallback=spec-trailing-dynamic model={model.Name} "
                    + $"sessionShape=[{string.Join(",", session.Inputs[0].Shape.Select(d => d?.ToString() ?? "?"))}]");
                return await ScalarFunctionBatchHelpers.DefaultLoop(
                    this, argumentColumns, rowCount, frame, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            if (!TryBuildPackedShapeFromExplicit(
                argumentColumns[1], rowCount, model.QualifiedName.ToString(), out packedShape))
            {
                DatumActivity.Scalars.Trace(
                    $"infer.batch fallback=explicit-shape-not-uniform model={model.Name}");
                return await ScalarFunctionBatchHelpers.DefaultLoop(
                    this, argumentColumns, rowCount, frame, cancellationToken).ConfigureAwait(false);
            }
        }

        using Activity? batchedSpan = DatumActivity.Scalars.StartActivity(
            $"infer.batch.run model={model.Name} shape=[{string.Join(",", packedShape)}]");
        return await ExecuteBatchedSingleInputAsync(
            session, argumentColumns[0], rowCount, packedShape,
            model.QualifiedName.ToString(), frame.Source, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Cross-row packing path. Pre-validates every row's input as a
    /// homogeneous typed array (Float32 / Int64 / Int32, matching the
    /// session's declared input element kind), packs them into a single
    /// <c>[B, ...]</c> tensor of the same kind, runs the session once,
    /// splits the output back into per-row results.
    /// </summary>
    /// <remarks>
    /// Input element kind is dispatched at the packing step so embedders
    /// with token-ID inputs (CLIP text, BERT-family) get the same
    /// columnar acceleration as Float32 image classifiers. Output side
    /// is Float32-only — caller gates on output kind so non-Float32-output
    /// sessions fall back to per-row dispatch.
    /// </remarks>
    private static async ValueTask<ValueRef[]> ExecuteBatchedSingleInputAsync(
        IInferenceSession session,
        ReadOnlyMemory<ValueRef> valueColumn,
        int rowCount,
        int[] packedShape,
        string modelName,
        IValueStore? arena,
        CancellationToken cancellationToken)
    {
        TensorSpec inputSpec = session.Inputs[0];
        DataKind inputKind = inputSpec.ElementKind;

        // Materialise every row as a typed array matching the session's
        // declared input kind. ValueRef.Materialized is the typed-payload
        // fast path; rows built via a SQL array literal fall back to the
        // per-element coercion loop. The homogeneous-length check decides
        // whether batching is even legal for this batch.
        Array[] inputs = new Array[rowCount];
        int perRowLen = -1;
        ReadOnlySpan<ValueRef> col = valueColumn.Span;
        for (int row = 0; row < rowCount; row++)
        {
            ValueRef cell = col[row];
            if (cell.IsNull)
            {
                throw new InvalidOperationException(
                    $"Model '{modelName}': infer() input must not be null (row {row}).");
            }
            if (!cell.IsArray || cell.ArrayElementKind != inputKind)
            {
                throw new InvalidOperationException(
                    $"Model '{modelName}': infer() batched dispatch expected {inputKind}[] inputs; "
                    + $"row {row} has {cell.Kind}{(cell.IsArray ? "[]" : "")}.");
            }

            Array rowInput = inputKind switch
            {
                DataKind.Float32 => MaterializeFloat32Row(cell, row, modelName),
                DataKind.Int64   => MaterializeInt64Row(cell, row, modelName),
                DataKind.Int32   => MaterializeInt32Row(cell, row, modelName),
                _ => throw new InvalidOperationException(
                    $"Model '{modelName}': batched packing path doesn't support input kind {inputKind}. "
                    + "Caller's element-kind gate should have routed to per-row dispatch."),
            };
            int rowLen = rowInput.Length;

            if (perRowLen < 0) perRowLen = rowLen;
            else if (rowLen != perRowLen)
            {
                // Variable element counts can't be cross-row packed — bail
                // to per-row dispatch via the static helper. The work done
                // up to this row is wasted; the alternative is a two-pass
                // shape check which costs the same in the homogeneous case.
                return await PerRowFallbackAsync().ConfigureAwait(false);
            }
            inputs[row] = rowInput;
        }

        // Build the packed [B, feature_dims...] tensor using the caller-
        // supplied trailing dims. Caller resolves them from either the
        // session spec's concrete trailing dims (1-arg form) or the
        // uniform explicit-shape literal across rows (2-arg form), so
        // this helper stays agnostic to where the dims came from.
        int[] shape = new int[packedShape.Length];
        shape[0] = rowCount;
        for (int i = 1; i < packedShape.Length; i++)
        {
            shape[i] = packedShape[i];
        }

        // Sanity-check the per-row element count against the packed shape.
        long expectedPerRow = 1;
        for (int i = 1; i < shape.Length; i++) expectedPerRow *= shape[i];
        if (expectedPerRow != perRowLen)
        {
            // Per-row input doesn't match the session's declared per-row
            // shape product — fall back so the per-row path's ResolveShape
            // produces the precise error message.
            return await PerRowFallbackAsync().ConfigureAwait(false);
        }

        TensorSpec outputSpec = session.Outputs[0];
        TensorBag inputBag = session.CreateInputBag();
        TensorBag? outputBag = null;
        try
        {
            // Pack into a single contiguous buffer of the matching kind and
            // add to the input bag. Buffer.BlockCopy handles the per-element
            // byte copy uniformly regardless of unmanaged T.
            switch (inputKind)
            {
                case DataKind.Float32:
                {
                    float[] packed = new float[(long)rowCount * perRowLen];
                    for (int row = 0; row < rowCount; row++)
                    {
                        Buffer.BlockCopy(
                            (float[])inputs[row], 0,
                            packed, row * perRowLen * sizeof(float),
                            perRowLen * sizeof(float));
                    }
                    inputBag.Add<float>(inputSpec.Name, DataKind.Float32, shape, packed);
                    break;
                }
                case DataKind.Int64:
                {
                    long[] packed = new long[(long)rowCount * perRowLen];
                    for (int row = 0; row < rowCount; row++)
                    {
                        Buffer.BlockCopy(
                            (long[])inputs[row], 0,
                            packed, row * perRowLen * sizeof(long),
                            perRowLen * sizeof(long));
                    }
                    inputBag.Add<long>(inputSpec.Name, DataKind.Int64, shape, packed);
                    break;
                }
                case DataKind.Int32:
                {
                    int[] packed = new int[(long)rowCount * perRowLen];
                    for (int row = 0; row < rowCount; row++)
                    {
                        Buffer.BlockCopy(
                            (int[])inputs[row], 0,
                            packed, row * perRowLen * sizeof(int),
                            perRowLen * sizeof(int));
                    }
                    inputBag.Add<int>(inputSpec.Name, DataKind.Int32, shape, packed);
                    break;
                }
                default:
                    throw new InvalidOperationException(
                        $"Model '{modelName}': batched packing path doesn't support input kind {inputKind}.");
            }

            outputBag = await session.RunAsync(inputBag, cancellationToken).ConfigureAwait(false);

            if (!outputBag.TryGet(outputSpec.Name, out IInferenceTensor outputTensor))
            {
                throw new InvalidOperationException(
                    $"Model '{modelName}': infer() batched dispatch session returned no tensor "
                    + $"named '{outputSpec.Name}' (declared first output).");
            }

            ReadOnlySpan<float> outputSpan = outputTensor.AsSpan<float>();
            if (outputSpan.Length % rowCount != 0)
            {
                throw new InvalidOperationException(
                    $"Model '{modelName}': infer() batched output element count "
                    + $"{outputSpan.Length} is not divisible by row count {rowCount}; "
                    + "expected B rows × M output elements after batched dispatch.");
            }
            int outputPerRow = outputSpan.Length / rowCount;
            // The full output shape is [B, ...feature_dims]; the per-row shape is
            // the trailing slice. ndim ≥ 2 per row → emit as a multi-dim ValueRef
            // so bracket access works on depth-map-style outputs. The arena
            // parameter is plumbed for API symmetry but not used directly — the
            // multi-dim DataValue materialises at ToDataValue time against the
            // projection's target arena.
            _ = arena;
            int[]? perRowShape = null;
            if (outputTensor.Shape.Count >= 3)
            {
                perRowShape = new int[outputTensor.Shape.Count - 1];
                for (int i = 0; i < perRowShape.Length; i++) perRowShape[i] = outputTensor.Shape[i + 1];
            }
            ValueRef[] results = new ValueRef[rowCount];
            for (int row = 0; row < rowCount; row++)
            {
                ReadOnlySpan<float> rowSlice = outputSpan.Slice(row * outputPerRow, outputPerRow);
                if (outputPerRow == 1)
                {
                    results[row] = ValueRef.FromFloat32(rowSlice[0]);
                }
                else if (perRowShape is not null)
                {
                    results[row] = ValueRef.FromPrimitiveMultiDimArray(rowSlice.ToArray(), perRowShape, DataKind.Float32);
                }
                else
                {
                    results[row] = ValueRef.FromPrimitiveArray(rowSlice.ToArray(), DataKind.Float32);
                }
            }
            return results;
        }
        finally
        {
            inputBag.Dispose();
            outputBag?.Dispose();
        }

        // Local helper. The captures (session, valueColumn, rowCount,
        // cancellationToken) come from the enclosing method's parameters
        // — no per-row state from the partially-built `inputs` array
        // escapes; the fallback re-evaluates from the original column.
        ValueTask<ValueRef[]> PerRowFallbackAsync()
        {
            return ScalarFunctionBatchHelpers.DefaultLoop(
                Instance, [valueColumn], rowCount, default!, cancellationToken);
        }
    }

    /// <summary>
    /// Per-row materialiser for Float32 array inputs. Fast-paths the
    /// typed <c>float[]</c> payload that <see cref="ValueRef.FromPrimitiveArray{T}"/>
    /// produces; falls back to per-element coercion for SQL-array-literal-built
    /// rows whose payload is <c>ValueRef[]</c>.
    /// </summary>
    internal static float[] MaterializeFloat32Row(ValueRef cell, int row, string modelName)
    {
        if (cell.Materialized is float[] direct) return direct;
        ReadOnlySpan<ValueRef> elements = cell.GetArrayElements();
        float[] copied = new float[elements.Length];
        for (int i = 0; i < elements.Length; i++)
        {
            if (!elements[i].TryToFloat(out float f))
            {
                throw new InvalidOperationException(
                    $"Model '{modelName}': infer() row {row} array element [{i}] is not Float32-coercible.");
            }
            copied[i] = f;
        }
        return copied;
    }

    /// <summary>Int64 sibling of <see cref="MaterializeFloat32Row"/>.</summary>
    internal static long[] MaterializeInt64Row(ValueRef cell, int row, string modelName)
    {
        if (cell.Materialized is long[] direct) return direct;
        ReadOnlySpan<ValueRef> elements = cell.GetArrayElements();
        long[] copied = new long[elements.Length];
        for (int i = 0; i < elements.Length; i++)
        {
            if (!elements[i].TryToInt64(out long v))
            {
                throw new InvalidOperationException(
                    $"Model '{modelName}': infer() row {row} array element [{i}] is not Int64-coercible.");
            }
            copied[i] = v;
        }
        return copied;
    }

    /// <summary>Int32 sibling of <see cref="MaterializeFloat32Row"/>.</summary>
    internal static int[] MaterializeInt32Row(ValueRef cell, int row, string modelName)
    {
        if (cell.Materialized is int[] direct) return direct;
        ReadOnlySpan<ValueRef> elements = cell.GetArrayElements();
        int[] copied = new int[elements.Length];
        for (int i = 0; i < elements.Length; i++)
        {
            if (!elements[i].TryToInt32(out int v))
            {
                throw new InvalidOperationException(
                    $"Model '{modelName}': infer() row {row} array element [{i}] is not Int32-coercible.");
            }
            copied[i] = v;
        }
        return copied;
    }

    /// <summary>
    /// Returns whether the session's input shape admits cross-row batching:
    /// rank ≥ 2 with a dynamic leading dim and concrete non-leading dims —
    /// the canonical <c>[batch, feature_dims...]</c> shape used by ONNX
    /// classifiers / embedders / recognizers. Mirrors the legacy
    /// <c>InferOperator.IsBatchableShape</c> gate.
    /// </summary>
    /// <summary>
    /// Cheap session-level gate: rank ≥ 2 with a dynamic leading dim.
    /// That's the minimum requirement for <em>any</em> cross-row batching.
    /// Whether each row can actually be packed depends on the call-site
    /// shape (the 1-arg form needs concrete trailing dims on the session
    /// spec; the 2-arg form supplies them via the explicit shape literal),
    /// which the per-form <c>TryBuildPackedShape*</c> helpers decide.
    /// </summary>
    internal static bool HasDynamicLeadingDim(TensorSpec spec)
    {
        return spec.Shape.Count >= 2 && spec.Shape[0] is null;
    }

    /// <summary>
    /// 1-arg form: trailing dims come from the session spec. Returns
    /// <see langword="false"/> when any non-leading dim is dynamic on the
    /// session — we have no second source for the dim and can't pack.
    /// </summary>
    internal static bool TryBuildPackedShapeFromSpec(
        TensorSpec spec, int rowCount, [NotNullWhen(true)] out int[]? packedShape)
    {
        for (int i = 1; i < spec.Shape.Count; i++)
        {
            if (spec.Shape[i] is null) { packedShape = null; return false; }
        }
        int[] shape = new int[spec.Shape.Count];
        shape[0] = rowCount;
        for (int i = 1; i < spec.Shape.Count; i++) shape[i] = spec.Shape[i]!.Value;
        packedShape = shape;
        return true;
    }

    /// <summary>
    /// 2-arg form: every row's explicit shape must lead with 1 AND be
    /// identical across rows. The literal supplies the trailing dims,
    /// so this batches even when the session spec leaves trailing dims
    /// dynamic — exactly the depth-anything / glpn case where the session
    /// is fully flexible and the SQL author hardcodes the per-row shape.
    /// Recombines the leading 1 with <paramref name="rowCount"/> to form
    /// the packed shape.
    /// </summary>
    internal static bool TryBuildPackedShapeFromExplicit(
        ReadOnlyMemory<ValueRef> shapeColumn, int rowCount, string modelName,
        [NotNullWhen(true)] out int[]? packedShape)
    {
        ReadOnlySpan<ValueRef> col = shapeColumn.Span;
        int[]? reference = null;
        for (int row = 0; row < rowCount; row++)
        {
            int[] shape = ReadShapeArray(col[row], modelName);
            if (shape.Length == 0 || shape[0] != 1) { packedShape = null; return false; }
            if (reference is null) { reference = shape; continue; }
            if (reference.Length != shape.Length) { packedShape = null; return false; }
            for (int i = 0; i < reference.Length; i++)
            {
                if (reference[i] != shape[i]) { packedShape = null; return false; }
            }
        }
        if (reference is null) { packedShape = null; return false; }
        int[] result = new int[reference.Length];
        result[0] = rowCount;
        for (int i = 1; i < reference.Length; i++) result[i] = reference[i];
        packedShape = result;
        return true;
    }

    /// <summary>
    /// Shared singleton for the fallback closure; <see cref="InferFunction"/>
    /// is stateless so one instance suffices for every dispatch site.
    /// </summary>
    private static readonly InferFunction Instance = new();

    /// <summary>
    /// Reader delegate for the multi-step <see cref="DispatchAndReadAsync"/>
    /// helper — decides how to package the post-Run output bag into a
    /// <see cref="ValueRef"/>. <see cref="InferFunction"/> uses
    /// <see cref="ReadFirstOutput"/>; <see cref="InferOutputsFunction"/>
    /// uses <see cref="ReadAllOutputsAsStruct"/>.
    /// </summary>
    internal delegate ValueRef OutputReader(
        IInferenceSession session,
        TensorBag outputBag,
        TypeRegistry? types,
        string modelName,
        IValueStore? arena);

    /// <summary>
    /// Shared dispatch core for <c>infer()</c> and <c>infer_outputs()</c>.
    /// Resolves the bound session from the frame, marshals arguments into
    /// the input bag (handling the single-input / multi-input / explicit-
    /// shape forms uniformly), runs the session, and hands the output bag
    /// to <paramref name="reader"/> which decides what shape to surface
    /// back to the SQL author.
    /// </summary>
    /// <param name="arguments">Call-site argument list (1 or 2 values).</param>
    /// <param name="frame">Evaluation frame carrying the current model + types registry.</param>
    /// <param name="functionName">
    /// Surface name (<c>"infer"</c> or <c>"infer_outputs"</c>) used in error
    /// messages so a body-scope mismatch points at the actual call site
    /// the user wrote.
    /// </param>
    /// <param name="reader">Output-bag → ValueRef strategy.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    internal static async ValueTask<ValueRef> DispatchAndReadAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        string functionName,
        OutputReader reader,
        CancellationToken cancellationToken)
    {
        if (frame.CurrentModel is not { } model)
        {
            throw new InvalidOperationException(
                $"{functionName}() is only callable from inside a CREATE MODEL body. "
                + "Outside a model frame there is no IInferenceSession bound to "
                + "dispatch to. Move the call into a model body, or use the "
                + "scalar-function form for the underlying computation.");
        }

        int argLength = arguments.Length;
        if (argLength is < 1 or > 3)
        {
            throw new InvalidOperationException(
                $"{functionName}() expects 1-3 arguments (value | session, value [, shape]); got {argLength}.");
        }

        // Named-session form: first argument is a String alias when the
        // caller writes `infer('encoder', value, ...)`. Slice it off and
        // resolve the session by alias; the rest of the dispatch operates
        // on the remaining "logical" arguments against that session,
        // identical to the implicit-default path. Sessions load on first
        // touch — ResolveAsync caches subsequent calls.
        IInferenceSession session;
        string sessionName;
        bool namedForm;
        {
            ReadOnlySpan<ValueRef> probe = arguments.Span;
            namedForm = argLength >= 2 && probe[0].Kind == DataKind.String && !probe[0].IsArray;
        }
        if (namedForm)
        {
            string alias = arguments.Span[0].AsString();
            if (!model.BoundSessions.ContainsKey(alias))
            {
                throw new InvalidOperationException(
                    $"Model '{model.QualifiedName.ToString()}': {functionName}() referenced session "
                    + $"alias '{alias}' which is not bound. Declared sessions: ["
                    + $"{string.Join(", ", model.BoundSessions.Keys)}]. "
                    + "Aliases come from the CREATE MODEL's USING clause "
                    + "(`USING 'path' AS alias`).");
            }
            session = await model.BoundSessions.ResolveAsync(alias, cancellationToken).ConfigureAwait(false);
            sessionName = alias;
        }
        else
        {
            if (!model.BoundSessions.ContainsKey("default"))
            {
                throw new InvalidOperationException(
                    $"Model '{model.QualifiedName.ToString()}' has no 'default' session bound. "
                    + "Multi-session bundles must dispatch by name — use "
                    + $"`{functionName}('alias', value, ...)`. "
                    + "Available sessions: ["
                    + $"{string.Join(", ", model.BoundSessions.Keys)}].");
            }
            session = await model.BoundSessions.ResolveAsync("default", cancellationToken).ConfigureAwait(false);
            sessionName = "default";
        }

        ReadOnlySpan<ValueRef> args = arguments.Span;
        ReadOnlySpan<ValueRef> logicalArgs = namedForm ? arguments.Slice(1).Span : args;

        if (session.Outputs.Count == 0)
        {
            throw new InvalidOperationException(
                $"Model '{model.QualifiedName.ToString()}': {functionName}() bound session declares "
                + "no outputs. The session implementation is misconfigured.");
        }

        if (logicalArgs.Length is < 1 or > 2)
        {
            throw new InvalidOperationException(
                $"{functionName}() expects (value [, shape]) after the optional session-alias; "
                + $"got {logicalArgs.Length} logical arguments.");
        }

        ValueRef arg = logicalArgs[0];

        // Multi-input dispatch: a struct argument unpacks by field name
        // into the session's input bag. Routes here for any (Struct) or
        // (Struct, Struct) call regardless of session arity — a struct arg
        // against a single-input session is allowed but unusual.
        if (arg.Kind == DataKind.Struct && !arg.IsArray)
        {
            ValueRef? shapeStructArg = logicalArgs.Length == 2 ? logicalArgs[1] : null;
            return await ExecuteMultiInputAsync(
                session, arg, shapeStructArg, frame, model.QualifiedName.ToString(),
                sessionName, functionName, reader, cancellationToken)
                .ConfigureAwait(false);
        }

        if (session.Inputs.Count != 1)
        {
            throw new InvalidOperationException(
                $"Model '{model.QualifiedName.ToString()}': scalar {functionName}() requires a "
                + $"single-input session, but the bound session declares "
                + $"{session.Inputs.Count} input(s). Use the struct form "
                + $"{functionName}({{{{field_name := value, ...}}}}) to bind multiple inputs by name.");
        }

        int[]? explicitShape = null;
        if (logicalArgs.Length == 2)
        {
            explicitShape = ReadShapeArray(logicalArgs[1], model.QualifiedName.ToString());
        }
        TensorSpec inputSpec = session.Inputs[0];

        TensorBag inputBag = session.CreateInputBag();
        TensorBag? outputBag = null;
        try
        {
            AddInputTensor(inputBag, inputSpec, arg, model.QualifiedName.ToString(), explicitShape);

            DatumActivity.Scalars.Trace($"[infer] {model.QualifiedName}#{sessionName}.{functionName}: pre-Run single-input '{inputSpec.Name}' kind={inputSpec.ElementKind}");
            outputBag = await session
                .RunAsync(inputBag, cancellationToken)
                .ConfigureAwait(false);
            DatumActivity.Scalars.Trace($"[infer] {model.QualifiedName}#{sessionName}.{functionName}: post-Run outputs={session.Outputs.Count}");

            return reader(session, outputBag, frame.Types, model.QualifiedName.ToString(), frame.Source);
        }
        catch (Exception ex)
        {
            DatumActivity.Scalars.Trace($"[infer] {model.QualifiedName}#{sessionName}.{functionName}: THREW {ex.GetType().Name}: {ex.Message}");
            throw;
        }
        finally
        {
            inputBag.Dispose();
            outputBag?.Dispose();
        }
    }

    /// <summary>
    /// Multi-input dispatch path. Takes a struct value whose field names
    /// match the session's input names case-insensitively, optionally a
    /// parallel struct of explicit shape arrays, and feeds every session
    /// input from the corresponding field. Used for BERT-family transformers
    /// where every input has multiple dynamic dims and per-input shape can't
    /// be inferred from a 1-d array length.
    /// </summary>
    private static async ValueTask<ValueRef> ExecuteMultiInputAsync(
        IInferenceSession session,
        ValueRef inputsStruct,
        ValueRef? shapesStruct,
        EvaluationFrame frame,
        string modelName,
        string sessionName,
        string functionName,
        OutputReader reader,
        CancellationToken cancellationToken)
    {
        if (frame.Types is null)
        {
            throw new InvalidOperationException(
                $"Model '{modelName}': multi-input infer() requires a TypeRegistry on the "
                + "evaluation frame to resolve struct field names against session inputs.");
        }
        if (inputsStruct.IsNull)
        {
            throw new InvalidOperationException(
                $"Model '{modelName}': multi-input infer() input struct must not be null.");
        }

        TypeDescriptor? inputsDesc = frame.Types.GetDescriptor(inputsStruct.TypeId);
        if (inputsDesc?.Fields is null || inputsDesc.Fields.Count == 0)
        {
            throw new InvalidOperationException(
                $"Model '{modelName}': multi-input infer() inputs struct has no field "
                + "descriptors — the struct must be built from a literal with named fields.");
        }

        TypeDescriptor? shapesDesc = null;
        if (shapesStruct is { } shapeArg)
        {
            if (shapeArg.IsNull)
            {
                throw new InvalidOperationException(
                    $"Model '{modelName}': multi-input infer() shapes struct must not be null.");
            }
            if (shapeArg.Kind != DataKind.Struct || shapeArg.IsArray)
            {
                throw new InvalidOperationException(
                    $"Model '{modelName}': multi-input infer() expected a Struct shapes "
                    + $"argument, got {shapeArg.Kind}{(shapeArg.IsArray ? "[]" : "")}.");
            }
            shapesDesc = frame.Types.GetDescriptor(shapeArg.TypeId);
            if (shapesDesc?.Fields is null)
            {
                throw new InvalidOperationException(
                    $"Model '{modelName}': multi-input infer() shapes struct has no field "
                    + "descriptors.");
            }
        }

        // Snapshot field spans into local arrays so we can iterate them
        // across the await without holding a ReadOnlySpan over a struct.
        ReadOnlySpan<ValueRef> inputFieldsSpan = inputsStruct.GetStructFields();
        ValueRef[] inputFields = new ValueRef[inputFieldsSpan.Length];
        inputFieldsSpan.CopyTo(inputFields);

        ValueRef[]? shapeFields = null;
        if (shapesStruct is { } sa)
        {
            ReadOnlySpan<ValueRef> shapeFieldsSpan = sa.GetStructFields();
            shapeFields = new ValueRef[shapeFieldsSpan.Length];
            shapeFieldsSpan.CopyTo(shapeFields);
        }

        TensorBag inputBag = session.CreateInputBag();
        TensorBag? outputBag = null;
        try
        {
            foreach (TensorSpec inputSpec in session.Inputs)
            {
                int fieldIdx = inputsDesc.FindFieldIndex(inputSpec.Name);
                if (fieldIdx < 0)
                {
                    throw new InvalidOperationException(
                        $"Model '{modelName}': multi-input infer() inputs struct has no field "
                        + $"matching session input '{inputSpec.Name}'. Available fields: "
                        + $"{string.Join(", ", inputsDesc.Fields.Select(f => f.Name))}.");
                }

                int[]? explicitShape = null;
                if (shapesDesc is not null)
                {
                    int shapeIdx = shapesDesc.FindFieldIndex(inputSpec.Name);
                    if (shapeIdx < 0)
                    {
                        throw new InvalidOperationException(
                            $"Model '{modelName}': multi-input infer() shapes struct has no field "
                            + $"matching session input '{inputSpec.Name}'. Available fields: "
                            + $"{string.Join(", ", shapesDesc.Fields!.Select(f => f.Name))}.");
                    }
                    explicitShape = ReadShapeArray(shapeFields![shapeIdx], modelName);
                }

                AddInputTensor(inputBag, inputSpec, inputFields[fieldIdx], modelName, explicitShape);
            }

            DatumActivity.Scalars.Trace($"[infer] {modelName}#{sessionName}.{functionName}: pre-Run multi-input inputs={session.Inputs.Count}");
            outputBag = await session
                .RunAsync(inputBag, cancellationToken)
                .ConfigureAwait(false);
            DatumActivity.Scalars.Trace($"[infer] {modelName}#{sessionName}.{functionName}: post-Run outputs={session.Outputs.Count}");

            return reader(session, outputBag, frame.Types, modelName, frame.Source);
        }
        catch (Exception ex)
        {
            DatumActivity.Scalars.Trace($"[infer] {modelName}#{sessionName}.{functionName}: THREW {ex.GetType().Name}: {ex.Message}");
            throw;
        }
        finally
        {
            inputBag.Dispose();
            outputBag?.Dispose();
        }
    }

    /// <summary>
    /// Reads the first declared output tensor from <paramref name="outputBag"/>.
    /// For multi-output sessions the convention HuggingFace optimum, transformers
    /// ONNX export, and most tooling follow is to list the primary output first
    /// (e.g. <c>last_hidden_state</c> ahead of <c>pooler_output</c> for BERT-
    /// family encoders, <c>d0</c> ahead of the deep-supervision aux outputs
    /// for U²-Net). Callers that need every output as a struct use
    /// <see cref="InferOutputsFunction"/> instead.
    /// </summary>
    internal static ValueRef ReadFirstOutput(
        IInferenceSession session, TensorBag outputBag, string modelName, IValueStore? arena)
    {
        TensorSpec outputSpec = session.Outputs[0];
        if (!outputBag.TryGet(outputSpec.Name, out IInferenceTensor outputTensor))
        {
            throw new InvalidOperationException(
                $"Model '{modelName}': session returned no tensor named "
                + $"'{outputSpec.Name}' (declared output). The session implementation "
                + "is misconfigured — its output bag must contain every name in Outputs.");
        }
        return ReadOutputTensor(outputTensor, modelName, arena);
    }

    /// <summary>
    /// Marshals one call-site argument into the input tensor. Scalars
    /// fan out into the input shape's element count (with dynamic
    /// dimensions resolving to 1); primitive arrays flow through with
    /// a 1-d shape matching the array length. When
    /// <paramref name="explicitShape"/> is non-null it bypasses
    /// <see cref="ResolveShape"/> entirely and is used verbatim — the
    /// only way to satisfy session specs with multiple dynamic dims.
    /// </summary>
    private static void AddInputTensor(
        TensorBag bag, TensorSpec spec, ValueRef arg, string modelName, int[]? explicitShape)
    {
        if (arg.IsNull)
        {
            throw new InvalidOperationException(
                $"Model '{modelName}': infer() input must not be null.");
        }

        switch (spec.ElementKind)
        {
            case DataKind.Float32:
                AddFloat32Tensor(bag, spec, arg, modelName, explicitShape);
                break;
            case DataKind.Int64:
                AddInt64Tensor(bag, spec, arg, modelName, explicitShape);
                break;
            case DataKind.Int32:
                AddInt32Tensor(bag, spec, arg, modelName, explicitShape);
                break;
            default:
                throw new NotSupportedException(
                    $"Model '{modelName}': infer() v1 supports input element kinds "
                    + $"Float32, Int32, Int64. Session declares '{spec.Name}' as "
                    + $"{spec.ElementKind}; extend AddInputTensor to add a marshaler.");
        }
    }

    private static void AddFloat32Tensor(
        TensorBag bag, TensorSpec spec, ValueRef arg, string modelName, int[]? explicitShape)
    {
        if (arg.IsArray)
        {
            float[] data = ExtractPrimitiveArray<float>(arg, DataKind.Float32, modelName);
            int[] shape = explicitShape ?? ResolveShape(spec, data.Length);
            ValidateShapeMatchesElements(shape, data.Length, modelName, "Float32");
            bag.Add<float>(spec.Name, DataKind.Float32, shape, data);
        }
        else if (arg.TryToFloat(out float scalar))
        {
            int[] shape = explicitShape ?? ResolveShape(spec, 1);
            bag.Add<float>(spec.Name, DataKind.Float32, shape, [scalar]);
        }
        else
        {
            throw new InvalidOperationException(
                $"Model '{modelName}': infer() expected a numeric scalar or Float32 array "
                + $"for input '{spec.Name}', got {arg.Kind}{(arg.IsArray ? "[]" : "")}.");
        }
    }

    private static void AddInt64Tensor(
        TensorBag bag, TensorSpec spec, ValueRef arg, string modelName, int[]? explicitShape)
    {
        if (arg.IsArray)
        {
            long[] data = ExtractPrimitiveArray<long>(arg, DataKind.Int64, modelName);
            int[] shape = explicitShape ?? ResolveShape(spec, data.Length);
            ValidateShapeMatchesElements(shape, data.Length, modelName, "Int64");
            bag.Add<long>(spec.Name, DataKind.Int64, shape, data);
        }
        else if (arg.TryToInt64(out long scalar))
        {
            int[] shape = explicitShape ?? ResolveShape(spec, 1);
            bag.Add<long>(spec.Name, DataKind.Int64, shape, [scalar]);
        }
        else
        {
            throw new InvalidOperationException(
                $"Model '{modelName}': infer() expected a numeric scalar or Int64 array "
                + $"for input '{spec.Name}', got {arg.Kind}{(arg.IsArray ? "[]" : "")}.");
        }
    }

    private static void AddInt32Tensor(
        TensorBag bag, TensorSpec spec, ValueRef arg, string modelName, int[]? explicitShape)
    {
        if (arg.IsArray)
        {
            int[] data = ExtractPrimitiveArray<int>(arg, DataKind.Int32, modelName);
            int[] shape = explicitShape ?? ResolveShape(spec, data.Length);
            ValidateShapeMatchesElements(shape, data.Length, modelName, "Int32");
            bag.Add<int>(spec.Name, DataKind.Int32, shape, data);
        }
        else if (arg.TryToInt32(out int scalar))
        {
            int[] shape = explicitShape ?? ResolveShape(spec, 1);
            bag.Add<int>(spec.Name, DataKind.Int32, shape, [scalar]);
        }
        else
        {
            throw new InvalidOperationException(
                $"Model '{modelName}': infer() expected a numeric scalar or Int32 array "
                + $"for input '{spec.Name}', got {arg.Kind}{(arg.IsArray ? "[]" : "")}.");
        }
    }

    /// <summary>
    /// Cross-checks that an explicit shape's product matches the input
    /// array's element count. Catches the typical user error of passing
    /// the wrong shape (e.g. forgetting a batch dim, swapping H and W,
    /// passing an Image-dim shape against a flattened tensor).
    /// </summary>
    private static void ValidateShapeMatchesElements(
        int[] shape, int elementCount, string modelName, string elementKindLabel)
    {
        long product = 1;
        foreach (int dim in shape) product *= dim;
        if (product != elementCount)
        {
            throw new InvalidOperationException(
                $"Model '{modelName}': infer() shape [{string.Join(", ", shape)}] (product {product}) "
                + $"doesn't match the {elementKindLabel}[] argument's element count {elementCount}. "
                + "The shape's product must equal the array length.");
        }
    }

    /// <summary>
    /// Pulls the explicit shape array out of an Int32[] / Int64[] argument
    /// (or a SQL array literal that coerces to integers). Used by the
    /// 2-arg <c>infer(value, shape)</c> form. The result is always Int32[]
    /// because the underlying session API expects <c>int</c> dims.
    /// </summary>
    private static int[] ReadShapeArray(ValueRef shapeArg, string modelName)
    {
        if (shapeArg.IsNull)
        {
            throw new InvalidOperationException(
                $"Model '{modelName}': infer() shape argument must not be null.");
        }
        if (!shapeArg.IsArray)
        {
            throw new InvalidOperationException(
                $"Model '{modelName}': infer() shape argument must be an integer array; got "
                + $"{shapeArg.Kind}{(shapeArg.IsArray ? "[]" : "")}.");
        }
        if (shapeArg.Materialized is int[] int32Direct)
        {
            return int32Direct;
        }
        if (shapeArg.Materialized is long[] int64Direct)
        {
            int[] copied = new int[int64Direct.Length];
            for (int i = 0; i < int64Direct.Length; i++) copied[i] = checked((int)int64Direct[i]);
            return copied;
        }
        ReadOnlySpan<ValueRef> elements = shapeArg.GetArrayElements();
        int[] result = new int[elements.Length];
        for (int i = 0; i < elements.Length; i++)
        {
            if (!elements[i].TryToInt32(out int v))
            {
                throw new InvalidOperationException(
                    $"Model '{modelName}': infer() shape element [{i}] ({elements[i].Kind}) "
                    + "is not coercible to Int32.");
            }
            result[i] = v;
        }
        return result;
    }

    /// <summary>
    /// Pulls a typed primitive array out of an array <see cref="ValueRef"/>.
    /// The fast path is <see cref="ValueRef.FromPrimitiveArray{T}"/>'s
    /// stash (the materialised payload IS the typed array). The slow
    /// fallback unpacks element-by-element from a <see cref="ValueRef"/>[]
    /// payload, which appears for arrays built by SQL array literals.
    /// </summary>
    private static T[] ExtractPrimitiveArray<T>(
        ValueRef arg, DataKind expectedKind, string modelName) where T : unmanaged
    {
        if (arg.ArrayElementKind != expectedKind)
        {
            throw new InvalidOperationException(
                $"Model '{modelName}': infer() expected a {expectedKind}[] argument "
                + $"but got {arg.ArrayElementKind}[]; cast the array element kind "
                + "before calling infer().");
        }

        if (arg.Materialized is T[] direct)
        {
            return direct;
        }

        ReadOnlySpan<ValueRef> elements = arg.GetArrayElements();
        T[] copied = new T[elements.Length];
        for (int i = 0; i < elements.Length; i++)
        {
            copied[i] = elements[i] switch
            {
                _ when typeof(T) == typeof(float) && elements[i].TryToFloat(out float f)
                    => (T)(object)f,
                _ when typeof(T) == typeof(int) && elements[i].TryToInt32(out int i32)
                    => (T)(object)i32,
                _ when typeof(T) == typeof(long) && elements[i].TryToInt64(out long i64)
                    => (T)(object)i64,
                _ => throw new InvalidOperationException(
                    $"Model '{modelName}': infer() could not coerce array element [{i}] "
                    + $"of kind {elements[i].Kind} to {expectedKind}."),
            };
        }
        return copied;
    }

    /// <summary>
    /// Resolves the input <see cref="TensorSpec.Shape"/> against the
    /// argument's element count. Dynamic dimensions (null entries)
    /// collapse to 1 for scalars / take the array length for the leading
    /// axis when the shape is 1-d. v1 keeps this simple: any other
    /// dynamic shape raises a clear error rather than guessing.
    /// </summary>
    private static int[] ResolveShape(TensorSpec spec, int elementCount)
    {
        IReadOnlyList<int?> declared = spec.Shape;
        if (declared.Count == 0)
        {
            // 0-d (scalar) tensor — element count must be 1.
            return [];
        }

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

        if (dynamicCount == 0)
        {
            return resolved;
        }
        if (dynamicCount == 1)
        {
            // One dynamic dim: it absorbs whatever element count is left
            // after dividing out the fixed dims. Most common case is a
            // sole dynamic batch dim.
            if (product == 0 || elementCount % product != 0)
            {
                throw new InvalidOperationException(
                    $"infer(): cannot fit {elementCount} elements into shape "
                    + $"[{string.Join(", ", declared.Select(d => d?.ToString() ?? "?"))}] "
                    + $"with one dynamic dim — fixed-dim product {product} doesn't divide.");
            }
            resolved[dynamicIndex] = (int)(elementCount / product);
            return resolved;
        }

        throw new NotSupportedException(
            $"infer() v1 doesn't resolve shapes with multiple dynamic dimensions "
            + $"(got {dynamicCount} in [{string.Join(", ", declared.Select(d => d?.ToString() ?? "?"))}]). "
            + "File a follow-up if a real model needs it; the shape solver wants "
            + "concrete examples to motivate its rules.");
    }

    /// <summary>
    /// Reads the single output tensor and packages it as a
    /// <see cref="ValueRef"/>. Shape product 1 surfaces as a scalar (no
    /// array wrapper); rank-1 outputs surface as a flat primitive array;
    /// rank ≥ 2 outputs surface as a multi-dim array wrapping a DataValue
    /// in <paramref name="arena"/> with the tensor's shape attached so
    /// downstream <c>arr[y, x]</c> bracket access resolves to a single
    /// element. <paramref name="arena"/> may be <see langword="null"/>
    /// when multi-dim materialisation is not required (the rank ≥ 2 path
    /// then falls back to the flat array, used by call sites in unit
    /// tests that don't carry a frame).
    /// </summary>
    private static ValueRef ReadOutputTensor(IInferenceTensor tensor, string modelName, IValueStore? arena)
    {
        long product = 1;
        foreach (int dim in tensor.Shape)
        {
            product *= dim;
        }

        switch (tensor.ElementKind)
        {
            case DataKind.Float32:
                ReadOnlySpan<float> f32 = tensor.AsSpan<float>();
                if (product == 1) return ValueRef.FromFloat32(f32[0]);
                return MaybeMultiDim(f32, tensor.Shape, DataKind.Float32, arena);
            case DataKind.Int64:
                ReadOnlySpan<long> i64 = tensor.AsSpan<long>();
                if (product == 1) return ValueRef.FromInt64(i64[0]);
                return MaybeMultiDim(i64, tensor.Shape, DataKind.Int64, arena);
            case DataKind.Int32:
                ReadOnlySpan<int> i32 = tensor.AsSpan<int>();
                if (product == 1) return ValueRef.FromInt32(i32[0]);
                return MaybeMultiDim(i32, tensor.Shape, DataKind.Int32, arena);
            default:
                throw new NotSupportedException(
                    $"Model '{modelName}': infer() v1 supports output element kinds "
                    + $"Float32, Int32, Int64. Session declares output as "
                    + $"{tensor.ElementKind}; extend ReadOutputTensor to add a converter.");
        }
    }

    /// <summary>
    /// Branches on tensor rank: rank-1 → flat primitive array on the managed
    /// heap; rank ≥ 2 → a multi-dim primitive ValueRef carrying the elements
    /// + shape in managed memory. The actual arena-backed multi-dim
    /// <see cref="DataValue"/> materialises at <see cref="ValueRef.ToDataValue"/>
    /// time into the target arena, so the value's lifetime survives the
    /// source arena's recycling at batch boundaries. The <paramref name="arena"/>
    /// parameter is kept for API symmetry with the rank-1 path but is unused
    /// here — materialisation happens later against the target.
    /// </summary>
    private static ValueRef MaybeMultiDim<T>(
        ReadOnlySpan<T> elements,
        IReadOnlyList<int> tensorShape,
        DataKind elementKind,
        IValueStore? arena)
        where T : unmanaged
    {
        _ = arena;  // managed-payload path; target arena resolves at ToDataValue.
        if (tensorShape.Count < 2)
        {
            return ValueRef.FromPrimitiveArray(elements.ToArray(), elementKind);
        }

        int[] shape = new int[tensorShape.Count];
        for (int i = 0; i < shape.Length; i++) shape[i] = tensorShape[i];

        return ValueRef.FromPrimitiveMultiDimArray(elements.ToArray(), shape, elementKind);
    }

    /// <summary>
    /// Reads every output tensor in <paramref name="outputBag"/> and packages
    /// the result as a <see cref="DataKind.Struct"/> keyed by output name —
    /// the dispatch reader used by <see cref="InferOutputsFunction"/>.
    /// The struct's <see cref="ValueRef.TypeId"/> is interned in
    /// <paramref name="types"/> so downstream <c>result['logits']</c> field
    /// access by name resolves without a plan-time schema. Positional access
    /// (<c>result[0]</c>) also works because struct field access falls back
    /// to ordinal lookup on integer indices.
    /// </summary>
    /// <remarks>
    /// Element-kind heterogeneity is allowed across outputs — RT-DETR-style
    /// detectors emit Float32 logits alongside Float32 boxes, but transformers
    /// regularly emit Int64 token-id outputs next to Float32 logits. Each
    /// field is built independently via <see cref="ReadOutputTensor"/>, so
    /// field kinds are whatever each tensor demands.
    /// </remarks>
    internal static ValueRef ReadAllOutputsAsStruct(
        IInferenceSession session,
        TensorBag outputBag,
        TypeRegistry? types,
        string modelName,
        IValueStore? arena)
    {
        IReadOnlyList<TensorSpec> outputs = session.Outputs;
        ValueRef[] fields = new ValueRef[outputs.Count];
        for (int i = 0; i < outputs.Count; i++)
        {
            TensorSpec spec = outputs[i];
            if (!outputBag.TryGet(spec.Name, out IInferenceTensor tensor))
            {
                throw new InvalidOperationException(
                    $"Model '{modelName}': infer_outputs() session returned no tensor named "
                    + $"'{spec.Name}' (declared output {i}). The session implementation "
                    + "is misconfigured — its output bag must contain every name in Outputs.");
            }
            fields[i] = ReadOutputTensor(tensor, modelName, arena);
        }

        ushort typeId = 0;
        if (types is not null)
        {
            StructFieldDescriptor[] descriptors = new StructFieldDescriptor[outputs.Count];
            for (int i = 0; i < outputs.Count; i++)
            {
                // Field TypeId mirrors the runtime ValueRef shape: a Float32[]
                // field interns as an array-of-Float32 type, a scalar Int64 as
                // the bare Int64 type. The plan-time SQL surface only uses
                // the field's NAME for `result[fieldname]` lookups; the typed
                // descriptor is for catalog inspection and shape rendering.
                int fieldTypeId = fields[i].IsArray
                    ? types.InternArrayType(fields[i].ArrayElementKind)
                    : (int)fields[i].Kind;
                descriptors[i] = new StructFieldDescriptor(outputs[i].Name, fieldTypeId);
            }
            typeId = (ushort)types.InternStructType(descriptors);
        }

        return ValueRef.FromStruct(fields, typeId);
    }
}

/// <summary>
/// <c>infer_outputs(value [, shape])</c> — sibling to <see cref="InferFunction"/>
/// that surfaces ALL declared outputs of the bound session as a
/// <see cref="DataKind.Struct"/> keyed by ONNX output name. Use this for
/// multi-output models where you need more than just the first declared
/// output (RT-DETR's <c>logits</c> + <c>pred_boxes</c>, RoBERTa-QA's
/// <c>start_logits</c> + <c>end_logits</c>, BlazeFace's boxes + scores).
/// The single-output / first-output-of-many shortcut stays on
/// <see cref="InferFunction"/> so existing SQL bodies continue to work
/// unchanged.
/// </summary>
/// <remarks>
/// <para>
/// Input marshalling is identical to <see cref="InferFunction"/> — same
/// 1-arg / 2-arg / multi-input struct / multi-input struct + shapes
/// dispatch shapes. The only difference is the output reading step.
/// </para>
/// <para>
/// SQL usage:
/// <code>
/// CREATE MODEL detect(img Image) RETURNS Array&lt;LabeledDetection&gt;
/// USING 'rtdetr/model.onnx'
/// AS BEGIN
///     DECLARE tensor Float32[] = image_to_tensor_chw(img, [640, 640]);
///     DECLARE outputs Struct = infer_outputs(tensor);
///     DECLARE logits Float32[] = outputs['logits'];
///     DECLARE boxes  Float32[] = outputs['pred_boxes'];
///     RETURN rtdetr_postprocess(logits, boxes, img)
/// END
/// </code>
/// </para>
/// </remarks>
public sealed class InferOutputsFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "infer_outputs";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;

    /// <inheritdoc />
    public static string Description =>
        "Same dispatch surface as infer() but returns a Struct of every declared "
        + "output keyed by ONNX output name (instead of just the first output). "
        + "Use for multi-output sessions — RT-DETR (logits + pred_boxes), RoBERTa "
        + "extractive QA (start_logits + end_logits), BlazeFace (boxes + scores). "
        + "Access fields via result['output_name'] or result[0] for positional. "
        + "Only callable from inside a CREATE MODEL body.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("inputs", DataKindMatcher.Exact(DataKind.Struct), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("shapes", DataKindMatcher.Exact(DataKind.Struct), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Struct)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Any),
                new ParameterSpec("shape", DataKindMatcher.Family(DataKindFamily.IntegerFamily), IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Struct)),
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("value", DataKindMatcher.Any)],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Struct)),
    ];

    /// <inheritdoc />
    public static BodyScopeRequirement BodyScope => BodyScopeRequirement.ModelBody;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<InferOutputsFunction>(argumentKinds);

    /// <inheritdoc />
    public bool IsPure => false;

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
        => InferFunction.DispatchAndReadAsync(
            arguments, frame, "infer_outputs",
            InferFunction.ReadAllOutputsAsStruct,
            cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Mirrors <see cref="InferFunction.ExecuteBatchAsync"/>: packs the column
    /// of per-row input tensors into one <c>[B, feature_dims...]</c> tensor and
    /// runs <c>Session.Run</c> once. The output side differs — instead of
    /// reading a single output as <c>Float32[]</c> per row, this splits EVERY
    /// declared output across rows and assembles each row's results as a
    /// <see cref="DataKind.Struct"/> keyed by output name (matching the per-row
    /// <see cref="InferFunction.ReadAllOutputsAsStruct"/> shape).
    /// </remarks>
    public async ValueTask<ValueRef[]> ExecuteBatchAsync(
        ReadOnlyMemory<ValueRef>[] argumentColumns,
        int rowCount,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        // Preconditions identical to InferFunction.ExecuteBatchAsync: only the
        // 1-arg or 2-arg form is batchable here (the named-session 3-arg form
        // falls through to per-row, same as infer()); a current model with a
        // default-bound session is required.
        if (rowCount <= 1
            || (argumentColumns.Length != 1 && argumentColumns.Length != 2)
            || frame.CurrentModel is not { } model
            || !model.BoundSessions.ContainsKey("default"))
        {
            DatumActivity.Scalars.Trace(
                $"infer_outputs.batch fallback=preconditions rows={rowCount} args={argumentColumns.Length} "
                + $"model={(frame.CurrentModel?.Name ?? "<none>")}");
            return await ScalarFunctionBatchHelpers.DefaultLoop(
                this, argumentColumns, rowCount, frame, cancellationToken).ConfigureAwait(false);
        }

        IInferenceSession session = await model.BoundSessions
            .ResolveAsync("default", cancellationToken)
            .ConfigureAwait(false);

        // Input-side gate: single packable input. Output side is checked
        // per-output during read because heterogeneous output kinds are the
        // whole point of infer_outputs.
        DataKind inputKind = session.Inputs.Count > 0 ? session.Inputs[0].ElementKind : DataKind.Float32;
        bool packableInput = inputKind is DataKind.Float32 or DataKind.Int64 or DataKind.Int32;
        if (session.Inputs.Count != 1
            || !packableInput
            || !InferFunction.HasDynamicLeadingDim(session.Inputs[0]))
        {
            DatumActivity.Scalars.Trace(
                $"infer_outputs.batch fallback=session-shape model={model.Name} inputs={session.Inputs.Count} "
                + $"inputKind={(session.Inputs.Count > 0 ? session.Inputs[0].ElementKind.ToString() : "n/a")} "
                + $"shape0={(session.Inputs.Count > 0 ? (session.Inputs[0].Shape.Count > 0 ? (session.Inputs[0].Shape[0]?.ToString() ?? "null") : "rank0") : "n/a")}");
            return await ScalarFunctionBatchHelpers.DefaultLoop(
                this, argumentColumns, rowCount, frame, cancellationToken).ConfigureAwait(false);
        }

        int[]? packedShape;
        if (argumentColumns.Length == 1)
        {
            if (!InferFunction.TryBuildPackedShapeFromSpec(session.Inputs[0], rowCount, out packedShape))
            {
                DatumActivity.Scalars.Trace(
                    $"infer_outputs.batch fallback=spec-trailing-dynamic model={model.Name}");
                return await ScalarFunctionBatchHelpers.DefaultLoop(
                    this, argumentColumns, rowCount, frame, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            if (!InferFunction.TryBuildPackedShapeFromExplicit(
                argumentColumns[1], rowCount, model.QualifiedName.ToString(), out packedShape))
            {
                DatumActivity.Scalars.Trace(
                    $"infer_outputs.batch fallback=explicit-shape-not-uniform model={model.Name}");
                return await ScalarFunctionBatchHelpers.DefaultLoop(
                    this, argumentColumns, rowCount, frame, cancellationToken).ConfigureAwait(false);
            }
        }

        using Activity? batchedSpan = DatumActivity.Scalars.StartActivity(
            $"infer_outputs.batch.run model={model.Name} shape=[{string.Join(",", packedShape)}]");
        return await ExecuteBatchedMultiOutputAsync(
            session, argumentColumns[0], rowCount, packedShape,
            model.QualifiedName.ToString(), frame.Source, frame.Types, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Cross-row packing + multi-output split. Inputs are packed identically to
    /// <see cref="InferFunction"/>'s single-output path; the divergence is that
    /// EVERY declared output gets split into per-row slices, then assembled into
    /// per-row <see cref="DataKind.Struct"/> values whose field set mirrors the
    /// session's declared outputs.
    /// </summary>
    private static async ValueTask<ValueRef[]> ExecuteBatchedMultiOutputAsync(
        IInferenceSession session,
        ReadOnlyMemory<ValueRef> valueColumn,
        int rowCount,
        int[] packedShape,
        string modelName,
        IValueStore? arena,
        TypeRegistry? types,
        CancellationToken cancellationToken)
    {
        TensorSpec inputSpec = session.Inputs[0];
        DataKind inputKind = inputSpec.ElementKind;

        // Materialise rows + check homogeneous element counts (input packing
        // is identical to InferFunction's path).
        Array[] inputs = new Array[rowCount];
        int perRowLen = -1;
        ReadOnlySpan<ValueRef> col = valueColumn.Span;
        for (int row = 0; row < rowCount; row++)
        {
            ValueRef cell = col[row];
            if (cell.IsNull)
            {
                throw new InvalidOperationException(
                    $"Model '{modelName}': infer_outputs() input must not be null (row {row}).");
            }
            if (!cell.IsArray || cell.ArrayElementKind != inputKind)
            {
                throw new InvalidOperationException(
                    $"Model '{modelName}': infer_outputs() batched dispatch expected {inputKind}[] inputs; "
                    + $"row {row} has {cell.Kind}{(cell.IsArray ? "[]" : "")}.");
            }

            Array rowInput = inputKind switch
            {
                DataKind.Float32 => InferFunction.MaterializeFloat32Row(cell, row, modelName),
                DataKind.Int64   => InferFunction.MaterializeInt64Row(cell, row, modelName),
                DataKind.Int32   => InferFunction.MaterializeInt32Row(cell, row, modelName),
                _ => throw new InvalidOperationException(
                    $"Model '{modelName}': batched packing path doesn't support input kind {inputKind}."),
            };
            int rowLen = rowInput.Length;
            if (perRowLen < 0) perRowLen = rowLen;
            else if (rowLen != perRowLen)
            {
                return await PerRowFallbackAsync().ConfigureAwait(false);
            }
            inputs[row] = rowInput;
        }

        int[] shape = new int[packedShape.Length];
        shape[0] = rowCount;
        for (int i = 1; i < packedShape.Length; i++) shape[i] = packedShape[i];

        long expectedPerRow = 1;
        for (int i = 1; i < shape.Length; i++) expectedPerRow *= shape[i];
        if (expectedPerRow != perRowLen)
        {
            return await PerRowFallbackAsync().ConfigureAwait(false);
        }

        TensorBag inputBag = session.CreateInputBag();
        TensorBag? outputBag = null;
        try
        {
            switch (inputKind)
            {
                case DataKind.Float32:
                {
                    float[] packed = new float[(long)rowCount * perRowLen];
                    for (int row = 0; row < rowCount; row++)
                    {
                        Buffer.BlockCopy(
                            (float[])inputs[row], 0,
                            packed, row * perRowLen * sizeof(float),
                            perRowLen * sizeof(float));
                    }
                    inputBag.Add<float>(inputSpec.Name, DataKind.Float32, shape, packed);
                    break;
                }
                case DataKind.Int64:
                {
                    long[] packed = new long[(long)rowCount * perRowLen];
                    for (int row = 0; row < rowCount; row++)
                    {
                        Buffer.BlockCopy(
                            (long[])inputs[row], 0,
                            packed, row * perRowLen * sizeof(long),
                            perRowLen * sizeof(long));
                    }
                    inputBag.Add<long>(inputSpec.Name, DataKind.Int64, shape, packed);
                    break;
                }
                case DataKind.Int32:
                {
                    int[] packed = new int[(long)rowCount * perRowLen];
                    for (int row = 0; row < rowCount; row++)
                    {
                        Buffer.BlockCopy(
                            (int[])inputs[row], 0,
                            packed, row * perRowLen * sizeof(int),
                            perRowLen * sizeof(int));
                    }
                    inputBag.Add<int>(inputSpec.Name, DataKind.Int32, shape, packed);
                    break;
                }
                default:
                    throw new InvalidOperationException(
                        $"Model '{modelName}': batched packing path doesn't support input kind {inputKind}.");
            }

            outputBag = await session.RunAsync(inputBag, cancellationToken).ConfigureAwait(false);

            // Pre-validate every output: present, kind we can split, total
            // element count divisible by rowCount (i.e., the output is batched).
            // Bail to per-row dispatch if any output fails — partial batching
            // would be inconsistent.
            IReadOnlyList<TensorSpec> outputs = session.Outputs;
            int outputCount = outputs.Count;
            IInferenceTensor[] outputTensors = new IInferenceTensor[outputCount];
            int[] perRowElementCounts = new int[outputCount];
            int[][] perRowShapes = new int[outputCount][]; // null entries → flat array or scalar
            for (int oi = 0; oi < outputCount; oi++)
            {
                TensorSpec outSpec = outputs[oi];
                if (!outputBag.TryGet(outSpec.Name, out IInferenceTensor outTensor))
                {
                    throw new InvalidOperationException(
                        $"Model '{modelName}': infer_outputs() session returned no tensor named "
                        + $"'{outSpec.Name}' (declared output {oi}).");
                }
                DataKind outKind = outTensor.ElementKind;
                if (outKind is not (DataKind.Float32 or DataKind.Int64 or DataKind.Int32))
                {
                    DatumActivity.Scalars.Trace(
                        $"infer_outputs.batch fallback=unsupported-output-kind model={modelName} "
                        + $"output={outSpec.Name} kind={outKind}");
                    return await PerRowFallbackAsync().ConfigureAwait(false);
                }

                long total = 1;
                foreach (int dim in outTensor.Shape) total *= dim;
                if (rowCount == 0 || total % rowCount != 0)
                {
                    DatumActivity.Scalars.Trace(
                        $"infer_outputs.batch fallback=output-not-row-divisible model={modelName} "
                        + $"output={outSpec.Name} total={total} rows={rowCount}");
                    return await PerRowFallbackAsync().ConfigureAwait(false);
                }
                perRowElementCounts[oi] = (int)(total / rowCount);
                outputTensors[oi] = outTensor;

                // Per-row shape preserves the FULL tensor rank, replacing the
                // leading batch dim with 1. This mirrors per-row dispatch: a
                // per-row call invokes the session with a batch=1 input, and
                // ONNX returns a tensor of shape [1, ...trailing]. Downstream
                // code (array_get, multi-dim bracket access) expects that exact
                // rank — dropping the leading dim would silently break every
                // SQL body that indexes the output with the rank declared in
                // the model's RETURNS annotation.
                int rank = outTensor.Shape.Count;
                if (rank >= 2)
                {
                    int[] perRowShape = new int[rank];
                    perRowShape[0] = 1;
                    for (int i = 1; i < rank; i++) perRowShape[i] = outTensor.Shape[i];
                    perRowShapes[oi] = perRowShape;
                }
                // rank == 1 (output is [N]): per-row data is a single element
                // per row → handled as a scalar below (perRow == 1 branch in
                // SliceRowAsValueRef); no per-row shape stored.
            }

            // Build the struct TypeId once. Field is "array of K" when the
            // per-row element count is > 1 — matches per-row dispatch's rule
            // (product == 1 → scalar, else → array/multi-dim).
            ushort typeId = 0;
            if (types is not null)
            {
                StructFieldDescriptor[] descriptors = new StructFieldDescriptor[outputCount];
                for (int oi = 0; oi < outputCount; oi++)
                {
                    DataKind outKind = outputTensors[oi].ElementKind;
                    bool isArray = perRowElementCounts[oi] > 1;
                    int fieldTypeId = isArray
                        ? types.InternArrayType(outKind)
                        : (int)outKind;
                    descriptors[oi] = new StructFieldDescriptor(outputs[oi].Name, fieldTypeId);
                }
                typeId = (ushort)types.InternStructType(descriptors);
            }

            _ = arena; // managed-payload path — arena materialisation happens at ToDataValue.

            // Assemble per-row structs: for each row, build a ValueRef[outputCount]
            // by slicing each output tensor at that row's offset.
            ValueRef[] results = new ValueRef[rowCount];
            for (int row = 0; row < rowCount; row++)
            {
                ValueRef[] fields = new ValueRef[outputCount];
                for (int oi = 0; oi < outputCount; oi++)
                {
                    IInferenceTensor t = outputTensors[oi];
                    int perRow = perRowElementCounts[oi];
                    int[]? perRowShape = perRowShapes[oi];
                    fields[oi] = SliceRowAsValueRef(t, row, perRow, perRowShape);
                }
                results[row] = ValueRef.FromStruct(fields, typeId);
            }
            return results;
        }
        finally
        {
            inputBag.Dispose();
            outputBag?.Dispose();
        }

        // Local fallback — re-runs the column through the per-row default
        // loop. Captures (valueColumn, rowCount, cancellationToken) from the
        // outer scope. Any work done above this point on packing was wasted,
        // matching the InferFunction sibling's behaviour.
        ValueTask<ValueRef[]> PerRowFallbackAsync()
        {
            return ScalarFunctionBatchHelpers.DefaultLoop(
                Instance, [valueColumn], rowCount, default!, cancellationToken);
        }
    }

    /// <summary>
    /// Extracts one row's slice from a batched output tensor and wraps it as a
    /// <see cref="ValueRef"/>: scalar if the tensor's per-row element count is
    /// one with no surviving per-row shape; multi-dim primitive if the per-row
    /// rank is ≥ 2; flat array otherwise.
    /// </summary>
    private static ValueRef SliceRowAsValueRef(
        IInferenceTensor tensor, int row, int perRow, int[]? perRowShape)
    {
        DataKind kind = tensor.ElementKind;
        int offset = row * perRow;

        // perRow == 1 always wins as a scalar — mirrors per-row dispatch's
        // ReadOutputTensor rule that "product == 1 → scalar" regardless of
        // tensor shape. A [N, 1] output gives every row a scalar, not a
        // shape-[1, 1] multi-dim array.
        switch (kind)
        {
            case DataKind.Float32:
            {
                ReadOnlySpan<float> span = tensor.AsSpan<float>().Slice(offset, perRow);
                if (perRow == 1) return ValueRef.FromFloat32(span[0]);
                if (perRowShape is not null) return ValueRef.FromPrimitiveMultiDimArray(span.ToArray(), perRowShape, DataKind.Float32);
                return ValueRef.FromPrimitiveArray(span.ToArray(), DataKind.Float32);
            }
            case DataKind.Int64:
            {
                ReadOnlySpan<long> span = tensor.AsSpan<long>().Slice(offset, perRow);
                if (perRow == 1) return ValueRef.FromInt64(span[0]);
                if (perRowShape is not null) return ValueRef.FromPrimitiveMultiDimArray(span.ToArray(), perRowShape, DataKind.Int64);
                return ValueRef.FromPrimitiveArray(span.ToArray(), DataKind.Int64);
            }
            case DataKind.Int32:
            {
                ReadOnlySpan<int> span = tensor.AsSpan<int>().Slice(offset, perRow);
                if (perRow == 1) return ValueRef.FromInt32(span[0]);
                if (perRowShape is not null) return ValueRef.FromPrimitiveMultiDimArray(span.ToArray(), perRowShape, DataKind.Int32);
                return ValueRef.FromPrimitiveArray(span.ToArray(), DataKind.Int32);
            }
            default:
                // Caller's pre-validation gates by kind; reaching this is a bug.
                throw new InvalidOperationException(
                    $"infer_outputs() batched split reached unsupported output kind {kind}.");
        }
    }

    /// <summary>
    /// Singleton for the per-row fallback closure — <see cref="InferOutputsFunction"/>
    /// is stateless so one instance suffices across every dispatch site.
    /// </summary>
    private static readonly InferOutputsFunction Instance = new();
}
