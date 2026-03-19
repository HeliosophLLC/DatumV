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
            (session, bag, _, modelName) => ReadFirstOutput(session, bag, modelName),
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
    /// <item>Explicit shape argument (the 2-arg <c>infer(value, shape)</c>
    /// form) — per-row shape variability is the precise case batching
    /// can't absorb.</item>
    /// <item>Session input rank &lt; 2 or with non-dynamic leading dim, or
    /// any non-leading dynamic dim — not a batchable shape.</item>
    /// <item>Row inputs have differing element counts — variable-shape
    /// detectors that resize per-image to different dims.</item>
    /// </list>
    /// In every fallback the per-row loop produces results indistinguishable
    /// from the batched path; this method is purely a performance override.
    /// </remarks>
    public ValueTask<ValueRef[]> ExecuteBatchAsync(
        ReadOnlyMemory<ValueRef>[] argumentColumns,
        int rowCount,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        // The columnar packing optimisation only applies to the 1-arg
        // single-input form against a batchable session. Every other
        // shape (multi-input struct, explicit per-row shape, non-batchable
        // session) falls back to the default per-row loop.
        if (rowCount <= 1
            || argumentColumns.Length != 1
            || frame.CurrentModel is not { } model
            || !model.BoundSessions.TryGetValue("default", out IInferenceSession? session)
            || session.Inputs.Count != 1
            || !IsBatchableShape(session.Inputs[0]))
        {
            return ScalarFunctionBatchHelpers.DefaultLoop(this, argumentColumns, rowCount, frame, cancellationToken);
        }

        return ExecuteBatchedSingleInputAsync(
            session, argumentColumns[0], rowCount, model.QualifiedName.ToString(), cancellationToken);
    }

    /// <summary>
    /// Cross-row packing path. Pre-validates every row's input as a
    /// Float32 array of the same length, packs them into a single
    /// <c>[B, ...]</c> Float32 tensor, runs the session once, splits the
    /// output back into per-row results.
    /// </summary>
    private static async ValueTask<ValueRef[]> ExecuteBatchedSingleInputAsync(
        IInferenceSession session,
        ReadOnlyMemory<ValueRef> valueColumn,
        int rowCount,
        string modelName,
        CancellationToken cancellationToken)
    {
        // Materialise every row's input as a managed float[]. We need
        // them anyway for the packed copy, and the homogeneous-length
        // check decides whether batching is even legal for this batch.
        float[][] inputs = new float[rowCount][];
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
            if (!cell.IsArray || cell.ArrayElementKind != DataKind.Float32)
            {
                throw new InvalidOperationException(
                    $"Model '{modelName}': infer() batched dispatch expected Float32[] inputs; "
                    + $"row {row} has {cell.Kind}{(cell.IsArray ? "[]" : "")}.");
            }
            if (cell.Materialized is not float[] rowInput)
            {
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
                rowInput = copied;
            }
            if (perRowLen < 0) perRowLen = rowInput.Length;
            else if (rowInput.Length != perRowLen)
            {
                // Variable element counts can't be cross-row packed — bail
                // to per-row dispatch via the static helper. The work done
                // up to this row is wasted; the alternative is a two-pass
                // shape check which costs the same in the homogeneous case.
                return await PerRowFallbackAsync().ConfigureAwait(false);
            }
            inputs[row] = rowInput;
        }

        // Build the packed [B, feature_dims...] tensor. IsBatchableShape
        // guarantees shape[0] is dynamic and shape[1..] are concrete.
        TensorSpec inputSpec = session.Inputs[0];
        int[] shape = new int[inputSpec.Shape.Count];
        shape[0] = rowCount;
        for (int i = 1; i < inputSpec.Shape.Count; i++)
        {
            shape[i] = inputSpec.Shape[i]!.Value;
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

        float[] packed = new float[(long)rowCount * perRowLen];
        for (int row = 0; row < rowCount; row++)
        {
            Buffer.BlockCopy(
                inputs[row], 0,
                packed, row * perRowLen * sizeof(float),
                perRowLen * sizeof(float));
        }

        TensorSpec outputSpec = session.Outputs[0];
        TensorBag inputBag = session.CreateInputBag();
        TensorBag? outputBag = null;
        try
        {
            inputBag.Add<float>(inputSpec.Name, DataKind.Float32, shape, packed);
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
            ValueRef[] results = new ValueRef[rowCount];
            for (int row = 0; row < rowCount; row++)
            {
                float[] rowOutput = outputSpan.Slice(row * outputPerRow, outputPerRow).ToArray();
                results[row] = outputPerRow == 1
                    ? ValueRef.FromFloat32(rowOutput[0])
                    : ValueRef.FromPrimitiveArray(rowOutput, DataKind.Float32);
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
    /// Returns whether the session's input shape admits cross-row batching:
    /// rank ≥ 2 with a dynamic leading dim and concrete non-leading dims —
    /// the canonical <c>[batch, feature_dims...]</c> shape used by ONNX
    /// classifiers / embedders / recognizers. Mirrors the legacy
    /// <c>InferOperator.IsBatchableShape</c> gate.
    /// </summary>
    private static bool IsBatchableShape(TensorSpec spec)
    {
        if (spec.Shape.Count < 2) return false;
        if (spec.Shape[0] is not null) return false;
        for (int i = 1; i < spec.Shape.Count; i++)
        {
            if (spec.Shape[i] is null) return false;
        }
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
        string modelName);

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

        if (!model.BoundSessions.TryGetValue("default", out IInferenceSession? session))
        {
            throw new InvalidOperationException(
                $"Model '{model.QualifiedName.ToString()}' has no 'default' session bound. "
                + "Multi-session bundles need a session-name argument; v1 only "
                + "supports single-session bundles (CREATE MODEL with a single "
                + "USING file).");
        }

        if (session.Outputs.Count == 0)
        {
            throw new InvalidOperationException(
                $"Model '{model.QualifiedName.ToString()}': {functionName}() bound session declares "
                + "no outputs. The session implementation is misconfigured.");
        }

        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args.Length is < 1 or > 2)
        {
            throw new InvalidOperationException(
                $"{functionName}() expects 1 or 2 arguments (value [, shape]); got {args.Length}.");
        }

        ValueRef arg = args[0];

        // Multi-input dispatch: a struct argument unpacks by field name
        // into the session's input bag. Routes here for any (Struct) or
        // (Struct, Struct) call regardless of session arity — a struct arg
        // against a single-input session is allowed but unusual.
        if (arg.Kind == DataKind.Struct && !arg.IsArray)
        {
            ValueRef? shapeStructArg = args.Length == 2 ? args[1] : null;
            return await ExecuteMultiInputAsync(
                session, arg, shapeStructArg, frame, model.QualifiedName.ToString(),
                functionName, reader, cancellationToken)
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
        if (args.Length == 2)
        {
            explicitShape = ReadShapeArray(args[1], model.QualifiedName.ToString());
        }
        TensorSpec inputSpec = session.Inputs[0];

        TensorBag inputBag = session.CreateInputBag();
        TensorBag? outputBag = null;
        try
        {
            AddInputTensor(inputBag, inputSpec, arg, model.QualifiedName.ToString(), explicitShape);

            DatumActivity.Scalars.Trace($"[infer] {model.QualifiedName}.{functionName}: pre-Run single-input '{inputSpec.Name}' kind={inputSpec.ElementKind}");
            outputBag = await session
                .RunAsync(inputBag, cancellationToken)
                .ConfigureAwait(false);
            DatumActivity.Scalars.Trace($"[infer] {model.QualifiedName}.{functionName}: post-Run outputs={session.Outputs.Count}");

            return reader(session, outputBag, frame.Types, model.QualifiedName.ToString());
        }
        catch (Exception ex)
        {
            DatumActivity.Scalars.Trace($"[infer] {model.QualifiedName}.{functionName}: THREW {ex.GetType().Name}: {ex.Message}");
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

            DatumActivity.Scalars.Trace($"[infer] {modelName}.{functionName}: pre-Run multi-input inputs={session.Inputs.Count}");
            outputBag = await session
                .RunAsync(inputBag, cancellationToken)
                .ConfigureAwait(false);
            DatumActivity.Scalars.Trace($"[infer] {modelName}.{functionName}: post-Run outputs={session.Outputs.Count}");

            return reader(session, outputBag, frame.Types, modelName);
        }
        catch (Exception ex)
        {
            DatumActivity.Scalars.Trace($"[infer] {modelName}.{functionName}: THREW {ex.GetType().Name}: {ex.Message}");
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
        IInferenceSession session, TensorBag outputBag, string modelName)
    {
        TensorSpec outputSpec = session.Outputs[0];
        if (!outputBag.TryGet(outputSpec.Name, out IInferenceTensor outputTensor))
        {
            throw new InvalidOperationException(
                $"Model '{modelName}': session returned no tensor named "
                + $"'{outputSpec.Name}' (declared output). The session implementation "
                + "is misconfigured — its output bag must contain every name in Outputs.");
        }
        return ReadOutputTensor(outputTensor, modelName);
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
    /// <see cref="ValueRef"/>. Shape product 1 surfaces as a scalar
    /// (no array wrapper), everything else as a primitive array of the
    /// tensor's element kind.
    /// </summary>
    private static ValueRef ReadOutputTensor(IInferenceTensor tensor, string modelName)
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
                return product == 1
                    ? ValueRef.FromFloat32(f32[0])
                    : ValueRef.FromPrimitiveArray(f32.ToArray(), DataKind.Float32);
            case DataKind.Int64:
                ReadOnlySpan<long> i64 = tensor.AsSpan<long>();
                return product == 1
                    ? ValueRef.FromInt64(i64[0])
                    : ValueRef.FromPrimitiveArray(i64.ToArray(), DataKind.Int64);
            case DataKind.Int32:
                ReadOnlySpan<int> i32 = tensor.AsSpan<int>();
                return product == 1
                    ? ValueRef.FromInt32(i32[0])
                    : ValueRef.FromPrimitiveArray(i32.ToArray(), DataKind.Int32);
            default:
                throw new NotSupportedException(
                    $"Model '{modelName}': infer() v1 supports output element kinds "
                    + $"Float32, Int32, Int64. Session declares output as "
                    + $"{tensor.ElementKind}; extend ReadOutputTensor to add a converter.");
        }
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
        string modelName)
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
            fields[i] = ReadOutputTensor(tensor, modelName);
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
}
