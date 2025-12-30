using DatumIngest.Functions;
using DatumIngest.Model;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

using SkiaSharp;

namespace DatumIngest.Models.Onnx;

/// <summary>
/// Monocular depth estimator — input <c>Image</c>, output <c>Image</c>
/// single-channel relative depth map sized to match the input. Wraps Intel
/// MiDaS / DPT ONNX exports interchangeably (e.g. <c>midas_v21_small_256.onnx</c>
/// and <c>dpt_large_384.onnx</c> from <c>isl-org/MiDaS</c>).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Architectural template.</strong> Structurally identical to
/// <see cref="U2NetModel"/> — fixed-square input, single-channel float output,
/// per-image min-max normalisation, grayscale-as-RGBA bitmap, resize back to the
/// source image's original dims. The two architecture-specific knobs are the
/// network input size (256 for MiDaS-small v2.1, 384 for DPT-Large) and the
/// preprocessing convention (BGR + ImageNet stats for MiDaS-small;
/// RGB + <c>mean=[0.5,0.5,0.5]</c>, <c>std=[0.5,0.5,0.5]</c> for DPT).
/// </para>
/// <para>
/// <strong>Input pipeline.</strong> Each row's image is decoded via
/// <see cref="ValueRef.AsImage"/> (arena-owned <see cref="SKBitmap"/>),
/// stretch-resized to the network's fixed square input by
/// <see cref="ImageTensorPrep.StretchAndPackNchw(SKBitmap, Span{float}, int, int, ReadOnlySpan{float}, ReadOnlySpan{float}, bool)"/>
/// — the helper handles the platform-native bitmap → RGB/BGR swap during the
/// resize, so no manual colour-type copy is needed. The MiDaS reference
/// transforms preserve aspect ratio with <c>multiple_of=32</c>; we square-stretch
/// instead, which costs a small amount of accuracy on extreme aspect ratios but
/// keeps the pipeline simple and avoids letterboxing artefacts in the depth map.
/// </para>
/// <para>
/// <strong>Output pipeline.</strong> Both architectures emit a single
/// <c>[1, inputSize, inputSize]</c> tensor of inverse depth (bigger value =
/// closer) in arbitrary units. We min-max normalise per image to fill
/// <c>[0, 1]</c>, then write a grayscale-as-RGBA bitmap which we resize back to
/// the input's original dimensions and hand to <see cref="ValueRef.FromImage(SKBitmap)"/>.
/// RGBA-with-equal-channels (rather than <see cref="SKColorType.Gray8"/>) keeps
/// the output uniform with every other image-emitting model so downstream
/// consumers don't branch on colour type. <strong>The depth map is relative,
/// not metric</strong> — per-image normalisation discards absolute scale, which
/// is fine for visualisation and ranking but not for measurement.
/// </para>
/// <para>
/// <strong>Per-row dispatch.</strong> The network input shape is fixed, so the
/// only reason rows can't share a single ONNX call is that the post-processing
/// resize-back targets each row's own original W×H. Same trade-off as
/// <see cref="U2NetModel"/> and <see cref="SuperResolutionModel"/>; batched
/// fixed-size inference + a per-row scatter step is a viable future
/// optimisation.
/// </para>
/// </remarks>
public sealed class DepthEstimationModel : OnnxModel
{
    private const int InputChannels = 3;

    private readonly string _onnxInputName;
    private readonly int _inputSize;
    private readonly bool _bgr;
    private readonly float[] _channelScale;
    private readonly float[] _channelBias;

    /// <summary>
    /// Loads a MiDaS / DPT ONNX file from <paramref name="modelFilePath"/>.
    /// Validates that the graph has a single image input with the expected
    /// <c>[N, 3, inputSize, inputSize]</c> shape so a wrong-architecture export
    /// fails up-front rather than silently producing nonsense depth maps.
    /// </summary>
    /// <param name="name">Catalog name this model is registered under.</param>
    /// <param name="modelFilePath">Absolute path to the ONNX file.</param>
    /// <param name="inputSize">Network input side length (256 for MiDaS-small, 384 for DPT-Large).</param>
    /// <param name="bgr">When <see langword="true"/>, packs the input tensor in BGR order (MiDaS-small v2.1 convention); when <see langword="false"/>, RGB (DPT convention).</param>
    /// <param name="channelMean">Per-channel mean (length 3) in <c>[0, 1]</c> — applied as <c>(rawByte/255 - mean) / std</c>.</param>
    /// <param name="channelStd">Per-channel standard deviation (length 3) in <c>[0, 1]</c>.</param>
    public DepthEstimationModel(
        string name,
        string modelFilePath,
        int inputSize,
        bool bgr,
        float[] channelMean,
        float[] channelStd)
        : base(
            name,
            modelFilePath,
            inputKinds: [DataKind.Image],
            outputKind: DataKind.Image,
            isDeterministic: true)
    {
        ArgumentNullException.ThrowIfNull(channelMean);
        ArgumentNullException.ThrowIfNull(channelStd);
        if (inputSize < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inputSize), inputSize, "Depth estimator input size must be at least 1.");
        }
        if (channelMean.Length != InputChannels || channelStd.Length != InputChannels)
        {
            throw new ArgumentException(
                $"Depth estimator expects per-channel mean and std of length {InputChannels}, "
                + $"got mean={channelMean.Length}, std={channelStd.Length}.");
        }

        _inputSize = inputSize;
        _bgr = bgr;

        // Convert (mean, std) over [0, 1] to (scale, bias) over raw bytes:
        //   normalised = (rawByte/255 - mean) / std
        //              = rawByte * (1 / (255*std)) + (-mean / std)
        _channelScale = new float[InputChannels];
        _channelBias = new float[InputChannels];
        for (int c = 0; c < InputChannels; c++)
        {
            if (channelStd[c] <= 0f)
            {
                throw new ArgumentException(
                    $"Depth estimator channelStd[{c}] must be positive, got {channelStd[c]}.");
            }
            _channelScale[c] = 1f / (255f * channelStd[c]);
            _channelBias[c] = -channelMean[c] / channelStd[c];
        }

        if (Session.InputMetadata.Count != 1)
        {
            throw new InvalidOperationException(
                $"DepthEstimationModel expected exactly one ONNX input but '{modelFilePath}' has "
                + $"{Session.InputMetadata.Count}: [{string.Join(", ", Session.InputMetadata.Keys)}].");
        }

        _onnxInputName = Session.InputMetadata.Keys.First();

        int[] inputDims = Session.InputMetadata[_onnxInputName].Dimensions;
        if (inputDims.Length != 4 || inputDims[1] != InputChannels)
        {
            throw new InvalidOperationException(
                $"DepthEstimationModel expected input shape [N, {InputChannels}, {inputSize}, {inputSize}] but '{modelFilePath}' "
                + $"declares [{string.Join(", ", inputDims)}]. Was the ONNX exported for a different architecture?");
        }

        // Fixed (non-dynamic) spatial dims must match the declared inputSize;
        // dynamic dims (-1) are accepted because the caller is asserting the
        // shape they intend to feed.
        if (inputDims[2] > 0 && inputDims[2] != inputSize)
        {
            throw new InvalidOperationException(
                $"DepthEstimationModel was constructed with inputSize={inputSize} but '{modelFilePath}' "
                + $"declares H={inputDims[2]}. Use the matching inputSize for this export.");
        }
        if (inputDims[3] > 0 && inputDims[3] != inputSize)
        {
            throw new InvalidOperationException(
                $"DepthEstimationModel was constructed with inputSize={inputSize} but '{modelFilePath}' "
                + $"declares W={inputDims[3]}. Use the matching inputSize for this export.");
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
                    $"DepthEstimationModel received a null image at row {row}; filter nulls upstream before invoking the model.");
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
            "DepthEstimationModel overrides InferBatchAsync directly because each row's depth map is resized back to its own original dims. BuildBatchInputs is not used.");

    /// <inheritdoc />
    protected override IReadOnlyList<ValueRef> ParseBatchOutputs(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs,
        int batchSize)
        => throw new InvalidOperationException(
            "DepthEstimationModel overrides InferBatchAsync directly. ParseBatchOutputs is not used.");

    private ValueRef InferSingleImage(SKBitmap decoded)
    {
        int origW = decoded.Width;
        int origH = decoded.Height;

        float[] inputData = new float[InputChannels * _inputSize * _inputSize];
        ImageTensorPrep.StretchAndPackNchw(
            decoded,
            inputData,
            _inputSize,
            _inputSize,
            _channelScale,
            _channelBias,
            bgr: _bgr);

        DenseTensor<float> inputTensor = new(
            inputData,
            [1, InputChannels, _inputSize, _inputSize]);
        NamedOnnxValue input = OnnxTensorConversion.CreateAutoCastInput(Session, _onnxInputName, inputTensor);

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = Session.Run([input]);

        DisposableNamedOnnxValue first = outputs.FirstOrDefault()
            ?? throw new InvalidOperationException("Depth-estimation ONNX session returned no outputs.");

        DenseTensor<float> outTensor = OnnxTensorConversion.ToFloatTensor(first);
        int[] outDims = outTensor.Dimensions.ToArray();

        // Intel MiDaS / DPT exports emit [1, H, W] (3-dim, no channel axis).
        // Some re-exports include a singleton channel dim → also accept
        // [1, 1, H, W]. Anything else is a wrong-architecture file.
        int outH;
        int outW;
        if (outDims.Length == 3 && outDims[0] == 1
            && outDims[1] == _inputSize && outDims[2] == _inputSize)
        {
            outH = outDims[1];
            outW = outDims[2];
        }
        else if (outDims.Length == 4 && outDims[0] == 1 && outDims[1] == 1
            && outDims[2] == _inputSize && outDims[3] == _inputSize)
        {
            outH = outDims[2];
            outW = outDims[3];
        }
        else
        {
            throw new InvalidOperationException(
                $"Depth-estimation output shape mismatch: expected [1, {_inputSize}, {_inputSize}] or "
                + $"[1, 1, {_inputSize}, {_inputSize}], got [{string.Join(", ", outDims)}].");
        }

        ReadOnlySpan<float> flat = outTensor.Buffer.Span;
        int planeSize = outH * outW;

        // Min-max normalise to [0, 1]. MiDaS / DPT outputs are inverse depth
        // in arbitrary units — without this the visible map is washed out
        // and per-image dynamic range is meaningless. Matches the upstream
        // Python reference's `depth_min`/`depth_max` rescale.
        float min = float.PositiveInfinity;
        float max = float.NegativeInfinity;
        for (int i = 0; i < planeSize; i++)
        {
            float v = flat[i];
            if (v < min) min = v;
            if (v > max) max = v;
        }
        // Degenerate uniform map — avoid divide-by-zero; emit a flat black image.
        float range = (max - min) > 1e-6f ? (max - min) : 1f;

        SKImageInfo smallInfo = new(outW, outH, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using SKBitmap smallDepth = new(smallInfo);
        nint smallPtr = smallDepth.GetPixels();
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

        // Resize the network-resolution depth map back to the input's
        // original dimensions. Ownership transfers to the ValueRef —
        // no `using` on the result.
        SKImageInfo finalInfo = new(origW, origH, SKColorType.Rgba8888, SKAlphaType.Opaque);
        SKBitmap finalDepth = smallDepth.Resize(finalInfo, SKSamplingOptions.Default)
            ?? throw new InvalidOperationException(
                $"SkiaSharp failed to resize the depth map to {origW}×{origH}.");

        return ValueRef.FromImage(finalDepth);
    }

    private static byte NormalizeToByte(float value)
    {
        float scaled = value * 255f;
        if (scaled < 0f) return 0;
        if (scaled > 255f) return 255;
        return (byte)MathF.Round(scaled);
    }
}
