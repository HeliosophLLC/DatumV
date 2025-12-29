using DatumIngest.Functions;
using DatumIngest.Model;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

using SkiaSharp;

namespace DatumIngest.Models.Onnx;

/// <summary>
/// U²-Net salient object detector — input <c>Image</c>, output <c>Image</c>
/// single-channel saliency mask sized to match the input. Wraps the standard
/// <c>u2net.onnx</c> (full, ~176M params) and <c>u2netp.onnx</c> (lite, ~4.7M)
/// exports from xuebinqin/U-2-Net interchangeably.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Input pipeline.</strong> Each row's image is decoded via SkiaSharp,
/// stretch-resized to the network's fixed 320×320 input, converted to RGB
/// (the platform-native bitmap from <see cref="ValueRef.AsImage"/> is BGRA on
/// Windows; <c>ImageTensorPrep.StretchAndPackNchw</c> handles the swap),
/// and packed as <strong>NCHW</strong> float32 with ImageNet normalisation
/// (<c>mean=[0.485,0.456,0.406]</c>, <c>std=[0.229,0.224,0.225]</c>) — the
/// standard U²-Net preprocessing used by the upstream training/inference code.
/// </para>
/// <para>
/// <strong>Output pipeline.</strong> The graph emits seven deep-supervision
/// outputs (<c>d0..d6</c>); we take the first (<c>d0</c>), the final
/// saliency map of shape <c>[1,1,320,320]</c>. Values are sigmoid'd in the
/// graph but not range-spread, so we min-max normalise per image to fill
/// <c>[0,1]</c>, then write a grayscale-as-RGBA bitmap that we resize back
/// to the input's original dimensions and hand to
/// <see cref="ValueRef.FromImage(SKBitmap)"/>. RGBA-with-equal-channels (rather
/// than <see cref="SKColorType.Gray8"/>) keeps the output uniform with every
/// other image-emitting model so downstream consumers don't branch on colour
/// type.
/// </para>
/// <para>
/// <strong>Per-row dispatch.</strong> The network input shape is fixed
/// (320×320), so the only reason rows can't share a single ONNX call is that
/// the post-processing resize-back targets each row's own original W×H.
/// Keeping per-row dispatch matches <see cref="SuperResolutionModel"/>'s
/// pattern; batched 320×320 inference + a per-row scatter step is a viable
/// future optimisation.
/// </para>
/// <para>
/// <strong>Compositing.</strong> U²-Net only emits the mask; cutting the
/// foreground out of the source image (the typical "background removal" use
/// case) is the job of a follow-up <c>cutout(image, mask)</c> scalar function,
/// not the model class.
/// </para>
/// </remarks>
public sealed class U2NetModel : OnnxModel
{
    private const int InputChannels = 3;
    private const int InputSize = 320;

    private readonly string _onnxInputName;

    /// <summary>
    /// Loads a U²-Net ONNX file from <paramref name="modelFilePath"/>. Validates
    /// that the graph has a single image input with the expected
    /// <c>[N, 3, 320, 320]</c> shape so a wrong-architecture export fails up-front
    /// rather than silently producing nonsense masks.
    /// </summary>
    public U2NetModel(string name, string modelFilePath)
        : base(
            name,
            modelFilePath,
            inputKinds: [DataKind.Image],
            outputKind: DataKind.Image,
            isDeterministic: true)
    {
        if (Session.InputMetadata.Count != 1)
        {
            throw new InvalidOperationException(
                $"U2NetModel expected exactly one ONNX input but '{modelFilePath}' has "
                + $"{Session.InputMetadata.Count}: [{string.Join(", ", Session.InputMetadata.Keys)}].");
        }

        _onnxInputName = Session.InputMetadata.Keys.First();

        int[] inputDims = Session.InputMetadata[_onnxInputName].Dimensions;
        if (inputDims.Length != 4 || inputDims[1] != InputChannels)
        {
            throw new InvalidOperationException(
                $"U2NetModel expected input shape [N, {InputChannels}, 320, 320] but '{modelFilePath}' "
                + $"declares [{string.Join(", ", inputDims)}]. Was the ONNX exported for a different architecture?");
        }
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<ValueRef>> InferBatchAsync(
        IReadOnlyList<IReadOnlyList<ValueRef>> inputs,
        IReadOnlyList<IReadOnlyList<ValueRef>> overrides,
        CancellationToken cancellationToken)
    {
        _ = overrides;
        cancellationToken.ThrowIfCancellationRequested();
        if (inputs.Count == 0) return [];

        ValueRef[] results = new ValueRef[inputs.Count];

        for (int row = 0; row < inputs.Count; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ValueRef image = inputs[row][0];
            if (image.IsNull)
            {
                throw new InvalidOperationException(
                    $"U2NetModel received a null image at row {row}; filter nulls upstream before invoking the model.");
            }

            SKBitmap decoded = image.AsImage();
            results[row] = await Task.Run<ValueRef>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return InferSingleImage(decoded);
            }, cancellationToken).ConfigureAwait(false);
        }

        return results;
    }

    /// <inheritdoc />
    protected override IReadOnlyCollection<NamedOnnxValue> BuildBatchInputs(
        IReadOnlyList<IReadOnlyList<ValueRef>> rows)
        => throw new InvalidOperationException(
            "U2NetModel overrides InferBatchAsync directly because each row's mask is resized back to its own original dims. BuildBatchInputs is not used.");

    /// <inheritdoc />
    protected override IReadOnlyList<ValueRef> ParseBatchOutputs(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs,
        int batchSize)
        => throw new InvalidOperationException(
            "U2NetModel overrides InferBatchAsync directly. ParseBatchOutputs is not used.");

    private ValueRef InferSingleImage(SKBitmap decoded)
    {
        int origW = decoded.Width;
        int origH = decoded.Height;

        float[] inputData = new float[InputChannels * InputSize * InputSize];
        ImageTensorPrep.StretchAndPackNchw(
            decoded,
            inputData,
            InputSize,
            InputSize,
            ImageTensorPrep.ImageNetScale,
            ImageTensorPrep.ImageNetBias);

        DenseTensor<float> inputTensor = new(
            inputData,
            [1, InputChannels, InputSize, InputSize]);
        NamedOnnxValue input = OnnxTensorConversion.CreateAutoCastInput(Session, _onnxInputName, inputTensor);

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = Session.Run([input]);

        // U²-Net exports seven deep-supervision tensors d0..d6 in declaration
        // order; d0 is the final fused saliency map. The lite (u2netp) export
        // has the same output ordering.
        DisposableNamedOnnxValue first = outputs.FirstOrDefault()
            ?? throw new InvalidOperationException("U²-Net ONNX session returned no outputs.");

        DenseTensor<float> outTensor = OnnxTensorConversion.ToFloatTensor(first);
        int[] outDims = outTensor.Dimensions.ToArray();

        if (outDims.Length != 4 || outDims[0] != 1 || outDims[1] != 1
            || outDims[2] != InputSize || outDims[3] != InputSize)
        {
            throw new InvalidOperationException(
                $"U²-Net output shape mismatch: expected [1, 1, {InputSize}, {InputSize}], got "
                + $"[{string.Join(", ", outDims)}].");
        }

        ReadOnlySpan<float> flat = outTensor.Buffer.Span;
        int planeSize = InputSize * InputSize;

        // Min-max normalise to [0, 1]. The graph applies sigmoid but the
        // resulting distribution is bunched — without this the visible mask
        // is washed out. Matches the upstream Python reference's `normPRED`.
        float min = float.PositiveInfinity;
        float max = float.NegativeInfinity;
        for (int i = 0; i < planeSize; i++)
        {
            float v = flat[i];
            if (v < min) min = v;
            if (v > max) max = v;
        }
        // Degenerate uniform mask — avoid divide-by-zero; emit a flat black mask.
        float range = (max - min) > 1e-6f ? (max - min) : 1f;

        SKImageInfo smallInfo = new(InputSize, InputSize, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using SKBitmap smallMask = new(smallInfo);
        nint smallPtr = smallMask.GetPixels();
        unsafe
        {
            byte* dst = (byte*)smallPtr;
            for (int yx = 0; yx < planeSize; yx++)
            {
                float v = (flat[yx] - min) / range;
                byte g = NormalizeToByte(v);
                int o = yx * 4;
                dst[o + 0] = g;
                dst[o + 1] = g;
                dst[o + 2] = g;
                dst[o + 3] = 255;
            }
        }

        // Resize the 320×320 mask back to the input's original dimensions.
        // Ownership transfers to the ValueRef — no `using` on the result.
        SKImageInfo finalInfo = new(origW, origH, SKColorType.Rgba8888, SKAlphaType.Opaque);
        SKBitmap finalMask = smallMask.Resize(finalInfo, SKSamplingOptions.Default)
            ?? throw new InvalidOperationException(
                $"SkiaSharp failed to resize the U²-Net mask to {origW}×{origH}.");

        return ValueRef.FromImage(finalMask);
    }

    private static byte NormalizeToByte(float value)
    {
        float scaled = value * 255f;
        if (scaled < 0f) return 0;
        if (scaled > 255f) return 255;
        return (byte)MathF.Round(scaled);
    }
}
