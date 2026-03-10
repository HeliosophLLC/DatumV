using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Models.Onnx;

using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Image;

/// <summary>
/// <c>yolox_preprocess(img Image, target_size Int32) → Float32[]</c>. The
/// canonical YOLOX preprocessing pipeline as a single SQL function: aspect-
/// preserving letterbox resize into a square <c>target_size × target_size</c>
/// canvas, BGR channel order, raw 0-255 pixel values (no per-channel
/// normalisation), 114-gray padding, packed as Float32 NCHW.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a YOLOX-specific function rather than reusing
/// <c>image_letterbox_tensor_chw</c>.</strong> YOLOX is unique in the ONNX
/// model zoo for being trained on cv2-loaded BGR images at raw 0-255 float
/// values — no <c>/255</c>, no ImageNet stats. Expressing that through the
/// general letterbox function works but reads awkwardly
/// (<c>mean = [0,0,0], std = [1/255, 1/255, 1/255]</c>, no BGR flag exposed
/// at SQL level). Bundling the YOLOX preset behind a named function keeps
/// the model bodies clean: <c>yolox_preprocess(img, 640)</c> is what every
/// YOLOX variant calls.
/// </para>
/// <para>
/// <strong>Output layout.</strong> NCHW Float32, length =
/// <c>3 × target_size × target_size</c>. Channel order is BGR: plane 0 is
/// the blue channel, plane 1 green, plane 2 red. Padded region fills with
/// the gray value 114 (raw byte). The letterbox scale factor is implicit
/// — recover it in postprocess from <c>target_size / max(img.W, img.H)</c>.
/// </para>
/// </remarks>
public sealed class YoloxPreprocessFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "yolox_preprocess";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "YOLOX-style image preprocessing: aspect-preserving letterbox to square target_size, "
        + "BGR channel order, raw 0-255 pixel values, 114-gray padding, Float32 NCHW. "
        + "Returns Float32[3 × target_size × target_size]; feed directly into infer().";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("img",         DataKindMatcher.Exact(DataKind.Image),
                    Metadata: new ParameterMetadata(
                        Description: "Source image (any size, any colour type — SkiaSharp handles the conversion).")),
                new ParameterSpec("target_size", DataKindMatcher.Family(DataKindFamily.IntegerFamily),
                    Metadata: new ParameterMetadata(
                        Check: new InCheck(["416", "640"]),
                        Unit: "pixels",
                        Description: "Square ONNX input dimension. 416 for nano/tiny variants; 640 for s/m/l/x/darknet.")),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    private const float PadValue = 114f;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<YoloxPreprocessFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef imgArg = args[0];
        if (imgArg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.Float32));
        }

        int targetSize = ReadTargetSize(args[1]);
        SKBitmap source = imgArg.AsImage();
        float[] output = new float[3 * targetSize * targetSize];
        ImageTensorPrep.LetterboxAndPackNchw(
            source, output, targetSize,
            scale: 1f, bias: 0f, padFill: PadValue, bgr: true);

        return new ValueTask<ValueRef>(ValueRef.FromPrimitiveArray(output, DataKind.Float32));
    }

    private static int ReadTargetSize(ValueRef arg)
    {
        int value = arg.Kind switch
        {
            DataKind.Int8 => arg.AsInt8(),
            DataKind.Int16 => arg.AsInt16(),
            DataKind.Int32 => arg.AsInt32(),
            DataKind.Int64 => checked((int)arg.AsInt64()),
            _ => throw new FunctionArgumentException("yolox_preprocess",
                $"target_size must be an integer kind, got {arg.Kind}."),
        };
        if (value <= 0)
        {
            throw new FunctionArgumentException("yolox_preprocess",
                $"target_size must be > 0, got {value}.");
        }
        return value;
    }
}
