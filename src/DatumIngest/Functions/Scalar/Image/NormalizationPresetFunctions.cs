using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Image;

/// <summary>
/// ImageNet RGB channel-mean preset: <c>[0.485, 0.456, 0.406]</c>. The
/// standard normalization used across PyTorch / torchvision vision models
/// trained on ImageNet — ResNet, MobileNet, EfficientNet, ViT, and most
/// detection / segmentation backbones derived from them. Slots directly
/// into <c>image_to_tensor_chw</c>'s <c>mean</c> argument.
/// </summary>
/// <remarks>
/// Returning a <see cref="DataKind.Float32"/> array (rather than a literal
/// array each call site types in) avoids the array-literal Float kind
/// inference issue: <c>[0.485, 0.456, 0.406]</c> typed inline parses as
/// <c>Array&lt;Int8&gt;</c>, which doesn't cast to <c>Array&lt;Float32&gt;</c>.
/// </remarks>
public sealed class ImagenetMeanFunction : IFunction, IScalarFunction
{
    private static readonly float[] Values = [0.485f, 0.456f, 0.406f];

    /// <inheritdoc />
    public static string Name => "imagenet_mean";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "ImageNet RGB channel-mean preset constants for image normalization: [0.485, 0.456, 0.406]. " +
        "Returns Float32[3]; pass to image_to_tensor_chw as the `mean` argument.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ImagenetMeanFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
        => new(ValueRef.FromPrimitiveArray((float[])Values.Clone(), DataKind.Float32));
}

/// <summary>
/// ImageNet RGB channel-std preset: <c>[0.229, 0.224, 0.225]</c>. Pair with
/// <see cref="ImagenetMeanFunction"/> for the canonical ImageNet
/// normalization (used by ResNet / MobileNet / EfficientNet / ViT family
/// models and their derivatives).
/// </summary>
public sealed class ImagenetStdFunction : IFunction, IScalarFunction
{
    private static readonly float[] Values = [0.229f, 0.224f, 0.225f];

    /// <inheritdoc />
    public static string Name => "imagenet_std";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "ImageNet RGB channel-std preset constants for image normalization: [0.229, 0.224, 0.225]. " +
        "Returns Float32[3]; pass to image_to_tensor_chw as the `std` argument.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ImagenetStdFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
        => new(ValueRef.FromPrimitiveArray((float[])Values.Clone(), DataKind.Float32));
}

/// <summary>
/// OpenAI CLIP RGB channel-mean preset:
/// <c>[0.48145466, 0.4578275, 0.40821073]</c>. The normalization used by
/// CLIP image encoders and the models that re-use them (BLIP, Florence-2's
/// vision tower, MetaCLIP, OpenCLIP variants).
/// </summary>
public sealed class ClipMeanFunction : IFunction, IScalarFunction
{
    private static readonly float[] Values = [0.48145466f, 0.4578275f, 0.40821073f];

    /// <inheritdoc />
    public static string Name => "clip_mean";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "OpenAI CLIP RGB channel-mean preset constants: [0.48145466, 0.4578275, 0.40821073]. " +
        "Returns Float32[3]; pass to image_to_tensor_chw as the `mean` argument when using CLIP-family models.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ClipMeanFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
        => new(ValueRef.FromPrimitiveArray((float[])Values.Clone(), DataKind.Float32));
}

/// <summary>
/// OpenAI CLIP RGB channel-std preset:
/// <c>[0.26862954, 0.26130258, 0.27577711]</c>. Pair with
/// <see cref="ClipMeanFunction"/> for the canonical CLIP normalization.
/// </summary>
public sealed class ClipStdFunction : IFunction, IScalarFunction
{
    private static readonly float[] Values = [0.26862954f, 0.26130258f, 0.27577711f];

    /// <inheritdoc />
    public static string Name => "clip_std";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "OpenAI CLIP RGB channel-std preset constants: [0.26862954, 0.26130258, 0.27577711]. " +
        "Returns Float32[3]; pass to image_to_tensor_chw as the `std` argument when using CLIP-family models.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ClipStdFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
        => new(ValueRef.FromPrimitiveArray((float[])Values.Clone(), DataKind.Float32));
}
