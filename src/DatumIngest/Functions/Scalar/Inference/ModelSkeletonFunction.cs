using System.Text;

using DatumIngest.Execution;
using DatumIngest.Inference;
using DatumIngest.Inference.OnnxRuntime;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Models;

namespace DatumIngest.Functions.Scalar.Inference;

/// <summary>
/// <c>inference.model_skeleton(path) → STRING</c>. Returns a starter
/// <c>CREATE MODEL</c> body for an ONNX file: the parameter list, return
/// type, <c>USING</c> clause, and an <c>infer()</c>-shaped body, all
/// pre-filled from the file's input + output tensors. The user pastes it
/// into the editor, names the model, and edits the body — paste-and-edit
/// is faster than look-up-and-type once the second model lands.
/// </summary>
/// <remarks>
/// <para>
/// Reads the file via <see cref="OnnxRuntimeBackend.LoadAsync"/> on CPU
/// (same path <c>inference.onnx_inspect</c> uses) to harvest tensor specs.
/// The output is meant as a template, not a finished model — the user is
/// expected to rename, add normalization, swap activations, etc.
/// </para>
/// <para>
/// <strong>Limitations.</strong> Multi-input / multi-output models render
/// with a TODO comment in the body (the v1 <c>infer()</c> surface is
/// single-tensor in / single-tensor out). Dynamic dimensions surface in the
/// comments so the user knows where they need to constrain shapes.
/// </para>
/// </remarks>
public sealed class ModelSkeletonFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "model_skeleton";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Generates a CREATE MODEL template for an ONNX file: " +
        "inference.model_skeleton(path) → STRING. " +
        "Pre-fills parameters, return type, USING clause, and an infer()-shaped body from the file's IO tensors. " +
        "Paste-and-edit beats look-up-and-type once a user integrates their second model.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("path", DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ModelSkeletonFunction>(argumentKinds);

    /// <inheritdoc />
    public async ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull)
        {
            return ValueRef.Null(DataKind.String);
        }

        string rawPath = arg.AsString();
        // Scalar function: no ModelCatalog on the frame. Require absolute /
        // file:// paths, same constraint the tokenizer functions document.
        string resolvedPath = ResolveAbsolutePath(rawPath);

        if (!System.IO.File.Exists(resolvedPath))
        {
            throw new FileNotFoundException(
                $"ONNX model file not found at '{resolvedPath}'.", resolvedPath);
        }

        InferenceLoadRequest request = new(
            ModelFilePath: resolvedPath,
            SessionName: "skeleton",
            Device: InferenceDevice.OnnxRuntimeCpu,
            Optimization: InferenceOptimization.None);

        OnnxRuntimeBackend backend = new();
        using IInferenceSession session = await backend.LoadAsync(request, cancellationToken);
        string skeleton = BuildSkeleton(resolvedPath, session.Inputs, session.Outputs);
        return ValueRef.FromString(skeleton);
    }

    private static string ResolveAbsolutePath(string path)
    {
        if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return path["file://".Length..];
        }
        if (!Path.IsPathRooted(path))
        {
            throw new FunctionArgumentException(Name,
                $"'{path}' is a relative path. Scalar inference helpers don't have access " +
                "to the catalog's model directory; pass an absolute path or a 'file://'-prefixed URI.");
        }
        return path;
    }

    /// <summary>
    /// Renders the CREATE MODEL skeleton from the file's IO tensors. The
    /// model name is a placeholder (<c>your_model_name</c>) — users
    /// always need to choose one. Multi-tensor IO surfaces a TODO body
    /// instead of guessing what's wanted.
    /// </summary>
    private static string BuildSkeleton(
        string path,
        IReadOnlyList<TensorSpec> inputs,
        IReadOnlyList<TensorSpec> outputs)
    {
        StringBuilder sb = new();
        sb.AppendLine("-- Generated CREATE MODEL skeleton. Rename `your_model_name` and edit the body before running.");
        sb.AppendLine($"--   source: {path}");
        AppendTensorComment(sb, "input ", inputs);
        AppendTensorComment(sb, "output", outputs);
        sb.AppendLine("CREATE MODEL your_model_name(");

        // Parameters: one per input tensor, named after the ONNX name when
        // it's a valid SQL identifier, else `arg{i}`.
        for (int i = 0; i < inputs.Count; i++)
        {
            TensorSpec spec = inputs[i];
            string paramName = SafeParamName(spec.Name, i);
            string paramType = ParamType(spec);
            string comma = i + 1 < inputs.Count ? "," : "";
            sb.AppendLine($"    @{paramName} {paramType}{comma}");
        }

        // Return type: from the single output; multi-output gets a TODO.
        string returnType;
        if (outputs.Count == 1)
        {
            returnType = ParamType(outputs[0]);
        }
        else
        {
            returnType = $"TODO_multi_output_return /* {outputs.Count} outputs */";
        }

        sb.AppendLine($") RETURNS {returnType}");
        sb.AppendLine($"USING 'file://{path}'");
        sb.AppendLine("AS BEGIN");

        if (inputs.Count == 1 && outputs.Count == 1)
        {
            string paramName = SafeParamName(inputs[0].Name, 0);
            sb.AppendLine($"    RETURN infer(@{paramName})");
        }
        else
        {
            sb.AppendLine($"    -- TODO: this model has {inputs.Count} input(s) and {outputs.Count} output(s).");
            sb.AppendLine("    -- v1 infer() handles single-tensor in / single-tensor out only.");
            sb.AppendLine("    -- Multi-tensor infer is a follow-up; for now wire via a custom procedural body.");
            sb.AppendLine("    RETURN NULL");
        }

        sb.AppendLine("END");
        return sb.ToString();
    }

    private static void AppendTensorComment(StringBuilder sb, string kindLabel, IReadOnlyList<TensorSpec> specs)
    {
        if (specs.Count == 0) return;
        for (int i = 0; i < specs.Count; i++)
        {
            TensorSpec s = specs[i];
            string shape = "[" + string.Join(", ", s.Shape.Select(d => d?.ToString() ?? "dyn")) + "]";
            sb.AppendLine($"--   {kindLabel} {i}: {s.Name}  {s.ElementKind}{shape}");
        }
    }

    /// <summary>
    /// Maps an ONNX <see cref="TensorSpec"/> to a SQL parameter / return
    /// type. v1 <c>infer()</c> works in flat array tensors so we always
    /// emit <c>Kind[]</c>; single-element / scalar shapes would still
    /// arrive as a length-1 array which the user can unwrap.
    /// </summary>
    private static string ParamType(TensorSpec spec) => $"{spec.ElementKind}[]";

    /// <summary>
    /// Best-effort coercion of an ONNX tensor name to a SQL identifier.
    /// Falls back to <c>arg{i}</c> when the name is empty or non-trivial
    /// (contains slashes, dots, hyphens — common in HF exports).
    /// </summary>
    private static string SafeParamName(string onnxName, int ordinal)
    {
        if (string.IsNullOrEmpty(onnxName)) return $"arg{ordinal}";

        bool allSafe = true;
        for (int i = 0; i < onnxName.Length; i++)
        {
            char c = onnxName[i];
            bool first = i == 0;
            bool ok = first
                ? (char.IsLetter(c) || c == '_')
                : (char.IsLetterOrDigit(c) || c == '_');
            if (!ok) { allSafe = false; break; }
        }
        return allSafe ? onnxName : $"arg{ordinal}";
    }
}
