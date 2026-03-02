using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Image;

/// <summary>
/// Returns the pixel width of an <see cref="DataKind.Image"/> value, as
/// <see cref="DataKind.Int32"/>. Used inside <c>CREATE MODEL</c> bodies to
/// compute aspect-preserving resize targets and scale-back factors —
/// detectors like PP-OCR-det need access to both the original and
/// resized dimensions to map output boxes back to source-pixel space.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why scalar accessors instead of a struct.</strong> Returning
/// width and height as separate scalars composes cleanly with array
/// literals (<c>image_to_tensor_chw(resized, [image_height(resized), image_width(resized)], …)</c>)
/// and arithmetic (<c>image_width(orig) / image_width(resized)</c>) without
/// needing struct field access syntax. The cost — two function calls per
/// row instead of one — is dominated by everything else in a detector
/// pipeline.
/// </para>
/// <para>
/// <strong>Null handling.</strong> A null image input yields a null
/// <c>Int32</c>. Downstream arithmetic on null propagates per SQL's
/// three-valued logic.
/// </para>
/// </remarks>
public sealed class ImageWidthFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "image_width";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Returns the pixel width of an Image value as Int32. " +
        "Pair with image_height() to compose aspect-preserving preprocessing inside model bodies.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("img", DataKindMatcher.Exact(DataKind.Image))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Int32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ImageWidthFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Int32));
        }
        SKBitmap bmp = arg.AsImage();
        return new ValueTask<ValueRef>(ValueRef.FromInt32(bmp.Width));
    }
}

/// <summary>
/// Returns the pixel height of an <see cref="DataKind.Image"/> value, as
/// <see cref="DataKind.Int32"/>. Sibling to <see cref="ImageWidthFunction"/>.
/// </summary>
public sealed class ImageHeightFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "image_height";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Returns the pixel height of an Image value as Int32. " +
        "Pair with image_width() to compose aspect-preserving preprocessing inside model bodies.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("img", DataKindMatcher.Exact(DataKind.Image))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Int32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ImageHeightFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Int32));
        }
        SKBitmap bmp = arg.AsImage();
        return new ValueTask<ValueRef>(ValueRef.FromInt32(bmp.Height));
    }
}
