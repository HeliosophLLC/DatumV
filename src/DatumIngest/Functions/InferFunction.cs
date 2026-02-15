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
/// <strong>Scope (smallest viable).</strong> Single-input, single-output
/// sessions only. Multi-input bundles need either a struct-of-tensors
/// argument shape or named overloads; multi-output bundles need a
/// struct-of-tensors return shape. Both extensions are mechanical and
/// land when the first multi-tensor model demands them.
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
        + "frame there is no session to dispatch to. v1 supports "
        + "single-input, single-output sessions; multi-tensor bundles "
        + "are a follow-up.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        // Single argument: any numeric scalar or primitive array. The
        // return rule is SameAs(0) because v1 echoes the argument kind —
        // the session's actual output kind is decided at run time, and
        // the surrounding model body's RETURN coercion takes care of
        // any mismatch with the declared model return type.
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

        if (session.Inputs.Count != 1 || session.Outputs.Count != 1)
        {
            throw new InvalidOperationException(
                $"Model '{model.QualifiedName.ToString()}': infer() v1 supports only "
                + $"single-input, single-output sessions, but the bound session "
                + $"declares {session.Inputs.Count} input(s) and {session.Outputs.Count} "
                + "output(s). Multi-tensor support is a follow-up.");
        }

        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args.Length != 1)
        {
            throw new InvalidOperationException(
                $"infer() expects exactly 1 argument; got {args.Length}.");
        }

        ValueRef arg = args[0];
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
            AddInputTensor(inputBag, inputSpec, arg, model.QualifiedName.ToString());

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
    /// Marshals one call-site argument into the input tensor. Scalars
    /// fan out into the input shape's element count (with dynamic
    /// dimensions resolving to 1); primitive arrays flow through with
    /// a 1-d shape matching the array length.
    /// </summary>
    private static void AddInputTensor(
        TensorBag bag, TensorSpec spec, ValueRef arg, string modelName)
    {
        if (arg.IsNull)
        {
            throw new InvalidOperationException(
                $"Model '{modelName}': infer() input must not be null.");
        }

        switch (spec.ElementKind)
        {
            case DataKind.Float32:
                AddFloat32Tensor(bag, spec, arg, modelName);
                break;
            case DataKind.Int64:
                AddInt64Tensor(bag, spec, arg, modelName);
                break;
            case DataKind.Int32:
                AddInt32Tensor(bag, spec, arg, modelName);
                break;
            default:
                throw new NotSupportedException(
                    $"Model '{modelName}': infer() v1 supports input element kinds "
                    + $"Float32, Int32, Int64. Session declares '{spec.Name}' as "
                    + $"{spec.ElementKind}; extend AddInputTensor to add a marshaler.");
        }
    }

    private static void AddFloat32Tensor(
        TensorBag bag, TensorSpec spec, ValueRef arg, string modelName)
    {
        if (arg.IsArray)
        {
            float[] data = ExtractPrimitiveArray<float>(arg, DataKind.Float32, modelName);
            bag.Add<float>(spec.Name, DataKind.Float32, ResolveShape(spec, data.Length), data);
        }
        else if (arg.TryToFloat(out float scalar))
        {
            bag.Add<float>(spec.Name, DataKind.Float32, ResolveShape(spec, 1), [scalar]);
        }
        else
        {
            throw new InvalidOperationException(
                $"Model '{modelName}': infer() expected a numeric scalar or Float32 array "
                + $"for input '{spec.Name}', got {arg.Kind}{(arg.IsArray ? "[]" : "")}.");
        }
    }

    private static void AddInt64Tensor(
        TensorBag bag, TensorSpec spec, ValueRef arg, string modelName)
    {
        if (arg.IsArray)
        {
            long[] data = ExtractPrimitiveArray<long>(arg, DataKind.Int64, modelName);
            bag.Add<long>(spec.Name, DataKind.Int64, ResolveShape(spec, data.Length), data);
        }
        else if (arg.TryToInt64(out long scalar))
        {
            bag.Add<long>(spec.Name, DataKind.Int64, ResolveShape(spec, 1), [scalar]);
        }
        else
        {
            throw new InvalidOperationException(
                $"Model '{modelName}': infer() expected a numeric scalar or Int64 array "
                + $"for input '{spec.Name}', got {arg.Kind}{(arg.IsArray ? "[]" : "")}.");
        }
    }

    private static void AddInt32Tensor(
        TensorBag bag, TensorSpec spec, ValueRef arg, string modelName)
    {
        if (arg.IsArray)
        {
            int[] data = ExtractPrimitiveArray<int>(arg, DataKind.Int32, modelName);
            bag.Add<int>(spec.Name, DataKind.Int32, ResolveShape(spec, data.Length), data);
        }
        else if (arg.TryToInt32(out int scalar))
        {
            bag.Add<int>(spec.Name, DataKind.Int32, ResolveShape(spec, 1), [scalar]);
        }
        else
        {
            throw new InvalidOperationException(
                $"Model '{modelName}': infer() expected a numeric scalar or Int32 array "
                + $"for input '{spec.Name}', got {arg.Kind}{(arg.IsArray ? "[]" : "")}.");
        }
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
