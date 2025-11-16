using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Functions.Image;

/// <summary>
/// SQL entry point for fused image pipelines:
/// <c>img(source, f =&gt; f.transform_a().transform_b().reduce())</c>.
/// </summary>
/// <remarks>
/// <para>
/// The function is named <c>img</c> rather than <c>image</c> because <c>IMAGE</c> is
/// already a reserved type keyword (for <c>CAST(x AS IMAGE)</c>). Renaming the type
/// keyword or teaching the tokenizer to disambiguate by lookahead is feasible but
/// out of scope here.
/// </para>
/// </remarks>
/// <remarks>
/// <para>
/// This function exists in the registry so the parser, type resolver, and language-server
/// completion all see it as a real function — but it has no runtime body. The planner
/// recognises calls to <c>img()</c> and rewrites them into
/// <c>FusedImagePipelineExpression</c> nodes, walking the
/// lambda body to collect transforms and an optional terminal sink. Each function inside
/// the lambda must resolve to an <see cref="IImagePipelineFunction"/> (transform) or
/// <see cref="IImagePipelineSink"/> (sink); anything else is a plan-time error.
/// </para>
/// <para>
/// <strong>Identity short-circuit.</strong> An <c>image(x, f =&gt; f)</c> call with a body
/// that's exactly the lambda parameter is a no-op and lowers to <paramref>x</paramref>
/// itself — no decode, no encode, no FusedImagePipelineExpression. Format conversions
/// must therefore be expressed by an explicit transform (e.g. a future <c>encode</c>
/// stage), never as a side effect of <c>image(x, f =&gt; f)</c>.
/// </para>
/// </remarks>
public sealed class ImagePipelineFunction : IHigherOrderFunction
{
    private static readonly HashSet<int> LambdaIndices = [1];

    /// <inheritdoc />
    public string Name => "img";

    /// <inheritdoc />
    public IReadOnlySet<int> GetLambdaParameterIndices(int argumentCount) => LambdaIndices;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException(
                "img() requires exactly 2 arguments: a byte source (Image or UInt8Array) " +
                "and a lambda body (e.g. image(file, f => f.blur(5))).");
        }

        // The image source must produce bytes — Image kind from a sidecar/scan, or
        // UInt8Array (raw encoded bytes that haven't been kind-cast yet).
        if (argumentKinds[0] is not (DataKind.Image or DataKind.UInt8Array))
        {
            throw new ArgumentException(
                $"img() first argument must be Image or UInt8Array (the byte source), got {argumentKinds[0]}.");
        }

        // Return type is determined by the lambda body during planner lowering — could be
        // Image (transform-only chain), or any sink's ResultKind (terminal sink chain). The
        // type resolver answers via FusedImagePipelineExpression's resolved kind once
        // lowering has run; in the pre-lowered state we report Image as the safe default.
        return DataKind.Image;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments) =>
        throw new InvalidOperationException(
            "img() must be lowered to a FusedImagePipelineExpression at plan time and " +
            "should never reach the runtime evaluator. This indicates the planner pass did " +
            "not run, or ran but failed to recognise the call.");

    /// <inheritdoc />
    public DataValue ExecuteHigherOrder(
        ReadOnlySpan<DataValue> arguments,
        IReadOnlyDictionary<int, LambdaExpression> lambdaArguments,
        LambdaEvaluator lambdaEvaluator) =>
        throw new InvalidOperationException(
            "img() must be lowered to a FusedImagePipelineExpression at plan time and " +
            "should never reach the runtime evaluator. This indicates the planner pass did " +
            "not run, or ran but failed to recognise the call.");
}
