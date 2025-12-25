using DatumIngest.Functions;
using DatumIngest.Model;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

using SkiaSharp;

namespace DatumIngest.Models.Onnx;

/// <summary>
/// MobileNetV2 ImageNet classifier wrapped as an <see cref="IModel"/> backend.
/// Accepts a single <see cref="DataKind.Image"/> column per row, returns
/// <c>Struct{label: String, score: Float32}</c> — the top-1 predicted class
/// plus the softmax probability assigned to it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Input pipeline.</strong> Each row's image bytes are decoded with
/// SkiaSharp, resized to 224×224, converted to RGB float32, normalised with the
/// ImageNet statistics
/// (mean = <c>[0.485, 0.456, 0.406]</c>, std = <c>[0.229, 0.224, 0.225]</c>),
/// and packed in NCHW layout. The full batch becomes a single
/// <c>[N, 3, 224, 224]</c> tensor and dispatches in one ONNX call.
/// </para>
/// <para>
/// <strong>Output pipeline.</strong> The graph emits <c>[N, 1000]</c> float32
/// logits; we argmax across the class dimension and convert the winning
/// logit to a softmax probability. The label looks up in the supplied
/// vocabulary; with no labels file we fall back to <c>"class_&lt;index&gt;"</c>.
/// Surfacing <c>score</c> alongside <c>label</c> is the prerequisite for the
/// confidence-gated cascade (<c>tasks.classify</c>) — consumers can filter on
/// <c>WHERE result.score &gt; 0.7</c> or feed the score into a router.
/// </para>
/// <para>
/// Trained on the standard ImageNet-1k taxonomy. The model file is
/// <c>mobilenetv2-12.onnx</c> from the official ONNX model zoo
/// (<a href="https://github.com/onnx/models">onnx/models</a>).
/// </para>
/// </remarks>
public sealed class MobileNetV2Model : OnnxModel
{
    private const int InputWidth = 224;
    private const int InputHeight = 224;
    private const int InputChannels = 3;
    private const int ClassCount = 1000;

    private static readonly float[] ImageNetMean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] ImageNetStd = [0.229f, 0.224f, 0.225f];

    private readonly string _onnxInputName;
    private readonly IReadOnlyList<string>? _labels;

    /// <summary>
    /// Loads the MobileNetV2 ONNX file at <paramref name="modelFilePath"/>. When
    /// <paramref name="labels"/> is non-null and contains exactly
    /// <see cref="ClassCount"/> entries, predictions resolve through it; otherwise
    /// they emit as <c>class_&lt;index&gt;</c>.
    /// </summary>
    public MobileNetV2Model(
        string name,
        string modelFilePath,
        IReadOnlyList<string>? labels = null)
        : base(
            name,
            modelFilePath,
            inputKinds: [DataKind.Image],
            // Struct{label: String, score: Float32}. Field names live in the
            // schema layer, not on the ValueRef carrier — the operator/shell
            // pick them up from the model catalog's declared output schema.
            outputKind: DataKind.Struct,
            isDeterministic: true)
    {
        if (labels is not null && labels.Count != ClassCount)
        {
            throw new ArgumentException(
                $"MobileNetV2 expects {ClassCount} ImageNet labels but got {labels.Count}. " +
                $"Either supply the full ImageNet-1k label list or pass null to fall back to 'class_<index>'.",
                nameof(labels));
        }

        _labels = labels;

        // The reference MobileNetV2 ONNX graph uses "input" as its input name; if
        // someone hands us a slightly different export, fall back to whatever the
        // session reports rather than failing for a name mismatch.
        _onnxInputName = Session.InputMetadata.Keys.FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"ONNX model at '{modelFilePath}' has no input metadata; cannot determine input tensor name.");
    }

    /// <inheritdoc/>
    public override IReadOnlyList<ColumnInfo>? OutputFields =>
    [
        new ColumnInfo("label", DataKind.String, nullable: false),
        new ColumnInfo("score", DataKind.Float32, nullable: false),
    ];

    /// <inheritdoc />
    protected override IReadOnlyCollection<NamedOnnxValue> BuildBatchInputs(
        IReadOnlyList<IReadOnlyList<ValueRef>> rows)
    {
        int batchSize = rows.Count;
        int planeSize = InputHeight * InputWidth;
        int perImageFloats = InputChannels * planeSize;
        float[] tensorData = new float[batchSize * perImageFloats];

        for (int row = 0; row < batchSize; row++)
        {
            IReadOnlyList<ValueRef> rowInputs = rows[row];
            if (rowInputs.Count != 1)
            {
                throw new InvalidOperationException(
                    $"MobileNetV2 expects a single input column per row but row {row} has {rowInputs.Count}.");
            }

            ValueRef image = rowInputs[0];
            if (image.IsNull)
            {
                throw new InvalidOperationException(
                    $"MobileNetV2 received a null image at row {row}; filter nulls upstream before invoking the model.");
            }

            SKBitmap decoded = image.AsImage();
            int destOffset = row * perImageFloats;
            ResizeAndPackImage(decoded, tensorData.AsSpan(destOffset, perImageFloats));
        }

        DenseTensor<float> tensor = new(
            tensorData,
            [batchSize, InputChannels, InputHeight, InputWidth]);

        return [OnnxTensorConversion.CreateAutoCastInput(Session, _onnxInputName, tensor)];
    }

    /// <inheritdoc />
    protected override IReadOnlyList<ValueRef> ParseBatchOutputs(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs,
        int batchSize)
    {
        DisposableNamedOnnxValue first = outputs.FirstOrDefault()
            ?? throw new InvalidOperationException("MobileNetV2 ONNX session returned no outputs.");

        DenseTensor<float> logits = OnnxTensorConversion.ToFloatTensor(first);
        ReadOnlySpan<float> flat = logits.Buffer.Span;

        if (flat.Length != batchSize * ClassCount)
        {
            throw new InvalidOperationException(
                $"MobileNetV2 output shape mismatch: expected {batchSize * ClassCount} floats " +
                $"(batchSize={batchSize}, classes={ClassCount}) but got {flat.Length}.");
        }

        ValueRef[] results = new ValueRef[batchSize];
        for (int row = 0; row < batchSize; row++)
        {
            ReadOnlySpan<float> rowLogits = flat.Slice(row * ClassCount, ClassCount);
            int bestIdx = ArgMaxAndSoftmax(rowLogits, out float bestScore);
            string label = _labels is not null
                ? _labels[bestIdx]
                : $"class_{bestIdx}";
            results[row] = ValueRef.FromStruct(
            [
                ValueRef.FromString(label),
                ValueRef.FromFloat32(bestScore),
            ]);
        }

        return results;
    }

    /// <summary>
    /// Resizes the source bitmap to 224×224 RGB, normalises with the ImageNet
    /// statistics, and writes the result into <paramref name="dest"/> in NCHW
    /// layout (R-plane, then G-plane, then B-plane).
    /// </summary>
    private static void ResizeAndPackImage(SKBitmap decoded, Span<float> dest)
    {
        SKImageInfo targetInfo = new(InputWidth, InputHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap resized = decoded.Resize(targetInfo, SKSamplingOptions.Default)
            ?? throw new InvalidOperationException(
                $"SkiaSharp failed to resize image to {InputWidth}×{InputHeight} for MobileNetV2 input.");

        int planeSize = InputHeight * InputWidth;
        nint pixelPtr = resized.GetPixels();

        unsafe
        {
            byte* source = (byte*)pixelPtr;

            for (int yx = 0; yx < planeSize; yx++)
            {
                int srcOffset = yx * 4;
                // Skia gives us RGBA; ImageNet normalisation is per-channel on
                // the [0, 1] float range, then (x − mean) / std.
                float r = source[srcOffset] / 255f;
                float g = source[srcOffset + 1] / 255f;
                float b = source[srcOffset + 2] / 255f;

                dest[yx] = (r - ImageNetMean[0]) / ImageNetStd[0];
                dest[planeSize + yx] = (g - ImageNetMean[1]) / ImageNetStd[1];
                dest[2 * planeSize + yx] = (b - ImageNetMean[2]) / ImageNetStd[2];
            }
        }
    }

    /// <summary>
    /// Finds the argmax over <paramref name="logits"/> and converts the winning
    /// logit to its softmax probability. Numerically stable (subtracts the max
    /// before exponentiating). Two passes over the input.
    /// </summary>
    private static int ArgMaxAndSoftmax(ReadOnlySpan<float> logits, out float bestProbability)
    {
        int bestIdx = 0;
        float bestVal = logits[0];
        for (int i = 1; i < logits.Length; i++)
        {
            if (logits[i] > bestVal)
            {
                bestVal = logits[i];
                bestIdx = i;
            }
        }

        double sumExp = 0;
        for (int i = 0; i < logits.Length; i++)
        {
            sumExp += Math.Exp(logits[i] - bestVal);
        }

        bestProbability = (float)(1.0 / sumExp);
        return bestIdx;
    }
}
