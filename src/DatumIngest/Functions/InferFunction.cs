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
/// single output tensor back to a <see cref="ValueRef"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Scope.</strong> Single-output sessions only. Multi-input is
/// supported via a struct argument: <c>infer({a: x, b: y})</c> binds
/// each session input by name (case-insensitive). For multi-input sessions
/// with multiple dynamic dimensions per input — BERT-family transformers
/// where every input is <c>[batch, seq_len]</c> — pass a parallel
/// struct of shape arrays as the second argument:
/// <c>infer({a: x, b: y}, {a: [1, n], b: [1, n]})</c>.
/// Multi-output bundles still need a struct-of-tensors return shape and
/// land when the first multi-output model demands them.
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
        "Dispatches the currently-bound model session on its input. Only "
        + "callable from inside a CREATE MODEL body — outside a model "
        + "frame there is no session to dispatch to. "
        + "infer(value) resolves the tensor shape from the session's input "
        + "spec (works when ≤1 dynamic dim). "
        + "infer(value, shape Int32[]) overrides the shape explicitly — required "
        + "when the input spec has multiple dynamic dims (e.g. PP-OCR-det's "
        + "[-1, 3, -1, -1] where batch + H + W are all dynamic). "
        + "infer({a: x, b: y}) and infer({a: x, b: y}, {a: [..], b: [..]}) "
        + "feed a multi-input session by matching struct field names to session "
        + "input names case-insensitively. v1 supports single-output sessions; "
        + "multi-output bundles are a follow-up.";

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
    public async ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        if (frame.CurrentModel is not { } model)
        {
            throw new InvalidOperationException(
                "infer() is only callable from inside a CREATE MODEL body. "
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
                $"Model '{model.QualifiedName.ToString()}': infer() bound session declares "
                + "no outputs. The session implementation is misconfigured.");
        }
        // v1 returns the FIRST output for multi-output sessions. The
        // convention HuggingFace optimum, transformers ONNX export, and
        // most tools follow is to list the primary output first (e.g.
        // `last_hidden_state` ahead of `pooler_output` for BERT-family
        // encoders). Named output selection (`infer(value, 'output_name')`)
        // is a follow-up; structured multi-output return is a separate
        // follow-up.

        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args.Length is < 1 or > 2)
        {
            throw new InvalidOperationException(
                $"infer() expects 1 or 2 arguments (value [, shape]); got {args.Length}.");
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
                session, arg, shapeStructArg, frame, model.QualifiedName.ToString(), cancellationToken)
                .ConfigureAwait(false);
        }

        if (session.Inputs.Count != 1)
        {
            throw new InvalidOperationException(
                $"Model '{model.QualifiedName.ToString()}': scalar infer() requires a "
                + $"single-input session, but the bound session declares "
                + $"{session.Inputs.Count} input(s). Use the struct form "
                + "infer({{field_name := value, ...}}) to bind multiple inputs by name.");
        }

        int[]? explicitShape = null;
        if (args.Length == 2)
        {
            explicitShape = ReadShapeArray(args[1], model.QualifiedName.ToString());
        }
        TensorSpec inputSpec = session.Inputs[0];
        TensorSpec outputSpec = session.Outputs[0];

        // Build the input tensor on the session's allocator. We bag-add
        // exactly one tensor because we've already validated single-input
        // above; the bag is owned by us and disposed once we've read the
        // output back.
        TensorBag inputBag = session.CreateInputBag();
        TensorBag? outputBag = null;
        try
        {
            AddInputTensor(inputBag, inputSpec, arg, model.QualifiedName.ToString(), explicitShape);

            outputBag = await session
                .RunAsync(inputBag, cancellationToken)
                .ConfigureAwait(false);

            if (!outputBag.TryGet(outputSpec.Name, out IInferenceTensor outputTensor))
            {
                throw new InvalidOperationException(
                    $"Model '{model.QualifiedName.ToString()}': session returned no tensor named "
                    + $"'{outputSpec.Name}' (declared output). The session implementation "
                    + "is misconfigured — its output bag must contain every name in "
                    + "Outputs.");
            }

            return ReadOutputTensor(outputTensor, model.QualifiedName.ToString());
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

            outputBag = await session
                .RunAsync(inputBag, cancellationToken)
                .ConfigureAwait(false);

            TensorSpec outputSpec = session.Outputs[0];
            if (!outputBag.TryGet(outputSpec.Name, out IInferenceTensor outputTensor))
            {
                throw new InvalidOperationException(
                    $"Model '{modelName}': session returned no tensor named '{outputSpec.Name}' "
                    + "(declared output). The session implementation is misconfigured — "
                    + "its output bag must contain every name in Outputs.");
            }

            return ReadOutputTensor(outputTensor, modelName);
        }
        finally
        {
            inputBag.Dispose();
            outputBag?.Dispose();
        }
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
}
