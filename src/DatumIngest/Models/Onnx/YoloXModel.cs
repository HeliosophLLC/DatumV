using DatumIngest.Functions;
using DatumIngest.Model;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

using SkiaSharp;

namespace DatumIngest.Models.Onnx;

/// <summary>
/// YOLOX object detector — Apache-2.0 licensed alternative to YOLOv8.
/// Wraps Megvii's YOLOX family (nano / tiny / s / m / l / x / darknet)
/// behind a single model class. Returns one detection-array per image,
/// each detection a <c>Struct{label, score, x, y, w, h}</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Differences from <see cref="YoloModel"/> (YOLOv8).</strong>
/// </para>
/// <list type="bullet">
///   <item><description>
///     <strong>Letterbox preprocessing.</strong> Resize preserving
///     aspect ratio, pad with gray (114) to fill the input square. The
///     resized image lands in the top-left; padding fills the right/
///     bottom. Output bbox coords are in the padded square's space, so
///     post-processing reverses by dividing by the scale factor (no
///     offset subtraction needed).
///   </description></item>
///   <item><description>
///     <strong>BGR channel order, no normalisation.</strong> YOLOX was
///     trained with cv2-loaded BGR images at raw 0–255 float values
///     (no <c>/255</c>, no ImageNet stats). Skia's RGBA buffer is
///     re-ordered to BGR during the tensor pack.
///   </description></item>
///   <item><description>
///     <strong>Output shape <c>[N, anchors, 85]</c>.</strong> 4 bbox
///     (cx, cy, w, h in pixel coords) + 1 objectness + 80 class scores.
///     YOLOv8 has no objectness; YOLOX uses
///     <c>confidence = objectness × max(class_scores)</c>.
///   </description></item>
///   <item><description>
///     <strong>Variable input size.</strong> nano and tiny variants are
///     trained at 416×416; s/m/l/x/darknet at 640×640. The constructor
///     reads the input dimension from ONNX metadata so a single class
///     handles both.
///   </description></item>
/// </list>
/// </remarks>
public sealed class YoloXModel : OnnxModel
{
    private const int InputChannels = 3;
    private const int NumClasses = 80;
    private const int ValuesPerPrediction = 4 + 1 + NumClasses; // 85
    private const byte PadValue = 114;

    private readonly int _inputSize;
    private readonly int _expectedAnchors;
    private readonly string _onnxInputName;
    private readonly string _onnxOutputName;
    private readonly IReadOnlyList<string> _labels;
    private readonly bool _supportsBatching;

    /// <summary>Score threshold below which a prediction is dropped pre-NMS. Defaults to 0.25.</summary>
    public float ConfidenceThreshold { get; }

    /// <summary>IoU threshold for NMS. Boxes with IoU above this are suppressed. Defaults to 0.45.</summary>
    public float IouThreshold { get; }

    /// <summary>The square input dimension (416 for nano/tiny, 640 for s/m/l/x/darknet).</summary>
    public int InputSize => _inputSize;

    /// <summary>
    /// Whether this model's ONNX graph accepts a dynamic batch dimension.
    /// Megvii's default exports pin <c>batch=1</c>; re-export with
    /// <c>--dynamic</c> to enable batched dispatch.
    /// </summary>
    public bool SupportsBatching => _supportsBatching;

    /// <summary>
    /// Loads YOLOX from <paramref name="modelFilePath"/>. Defaults to the
    /// COCO-80 label vocabulary; pass <paramref name="labels"/> to override
    /// for custom-trained variants.
    /// </summary>
    public YoloXModel(
        string name,
        string modelFilePath,
        IReadOnlyList<string>? labels = null,
        float confidenceThreshold = 0.25f,
        float iouThreshold = 0.45f)
        : base(
            name,
            modelFilePath,
            inputKinds: [DataKind.Image],
            outputKind: DataKind.Struct,
            isDeterministic: true)
    {
        IReadOnlyList<string> resolvedLabels = labels ?? CocoLabels.Names;
        if (resolvedLabels.Count != NumClasses)
        {
            throw new ArgumentException(
                $"YOLOX expects {NumClasses} class labels but got {resolvedLabels.Count}. " +
                "Pass null to use the default COCO-80 vocabulary, or supply 80 strings for a custom-trained model.",
                nameof(labels));
        }

        _labels = resolvedLabels;
        ConfidenceThreshold = confidenceThreshold;
        IouThreshold = iouThreshold;

        _onnxInputName = Session.InputMetadata.Keys.FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"ONNX model at '{modelFilePath}' has no input metadata; cannot determine input tensor name.");
        _onnxOutputName = Session.OutputMetadata.Keys.FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"ONNX model at '{modelFilePath}' has no output metadata; cannot determine output tensor name.");

        // Read input size from metadata. Expected dims: [batch, 3, H, W]
        // with H == W. nano/tiny use 416, others 640.
        NodeMetadata inputMeta = Session.InputMetadata[_onnxInputName];
        if (inputMeta.Dimensions.Length != 4 || inputMeta.Dimensions[1] != InputChannels)
        {
            throw new InvalidOperationException(
                $"YOLOX expects 4-D input with 3 channels, got dimensions [{string.Join(",", inputMeta.Dimensions)}].");
        }
        _inputSize = inputMeta.Dimensions[2] > 0
            ? inputMeta.Dimensions[2]
            : 640;  // dynamic-shape exports may report -1; default to 640

        // Anchor count = sum over 3 strides (8, 16, 32) of (size/stride)^2.
        // 416 → 3549, 640 → 8400.
        int s8 = _inputSize / 8, s16 = _inputSize / 16, s32 = _inputSize / 32;
        _expectedAnchors = s8 * s8 + s16 * s16 + s32 * s32;

        // Detect dynamic batch dim — same heuristic as YoloModel.
        int batchDim = inputMeta.Dimensions.Length > 0 ? inputMeta.Dimensions[0] : 1;
        bool symbolicBatchDim = inputMeta.SymbolicDimensions.Length > 0
            && !string.IsNullOrEmpty(inputMeta.SymbolicDimensions[0]);
        _supportsBatching = batchDim <= 0 || symbolicBatchDim;
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

        int batchSize = inputs.Count;
        int planeSize = _inputSize * _inputSize;
        int perImageFloats = InputChannels * planeSize;
        float[] tensorData = new float[batchSize * perImageFloats];
        float[] scales = new float[batchSize];

        for (int row = 0; row < batchSize; row++)
        {
            ValueRef image = inputs[row][0];
            if (image.IsNull)
            {
                throw new InvalidOperationException(
                    $"YoloXModel received a null image at row {row}; filter nulls upstream.");
            }
            byte[] bytes = image.AsBytes();
            DecodeAndPackBgrLetterbox(
                bytes,
                tensorData.AsSpan(row * perImageFloats, perImageFloats),
                _inputSize,
                out scales[row]);
        }

        return await Task.Run<IReadOnlyList<ValueRef>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            int perImageValues = ValuesPerPrediction * _expectedAnchors;
            ValueRef[] results = new ValueRef[batchSize];

            if (_supportsBatching && batchSize > 1)
            {
                // Dynamic-batch: one Session.Run for everything.
                DenseTensor<float> tensor = new(
                    tensorData,
                    [batchSize, InputChannels, _inputSize, _inputSize]);
                NamedOnnxValue input = OnnxTensorConversion.CreateAutoCastInput(Session, _onnxInputName, tensor);

                using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = Session.Run([input]);
                DisposableNamedOnnxValue first = outputs.FirstOrDefault()
                    ?? throw new InvalidOperationException("YOLOX ONNX session returned no outputs.");

                DenseTensor<float> raw = OnnxTensorConversion.ToFloatTensor(first);
                ReadOnlySpan<float> flat = raw.Buffer.Span;

                if (flat.Length != batchSize * perImageValues)
                {
                    throw new InvalidOperationException(
                        $"YOLOX output shape mismatch (batched): expected {batchSize * perImageValues} floats " +
                        $"but got {flat.Length}. Per-image expected {perImageValues} ({_expectedAnchors} anchors × {ValuesPerPrediction}).");
                }

                for (int row = 0; row < batchSize; row++)
                {
                    ReadOnlySpan<float> rowSlice = flat.Slice(row * perImageValues, perImageValues);
                    List<Detection> detections = DecodeRowAndNms(rowSlice, scales[row]);
                    results[row] = BuildDetectionArray(detections);
                }
            }
            else
            {
                // Fixed batch=1 (Megvii's default): per-image dispatch loop.
                for (int row = 0; row < batchSize; row++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    DenseTensor<float> tensor = new(
                        tensorData.AsMemory(row * perImageFloats, perImageFloats),
                        [1, InputChannels, _inputSize, _inputSize]);
                    NamedOnnxValue input = OnnxTensorConversion.CreateAutoCastInput(Session, _onnxInputName, tensor);

                    using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = Session.Run([input]);
                    DisposableNamedOnnxValue first = outputs.FirstOrDefault()
                        ?? throw new InvalidOperationException("YOLOX ONNX session returned no outputs.");

                    DenseTensor<float> raw = OnnxTensorConversion.ToFloatTensor(first);
                    ReadOnlySpan<float> flat = raw.Buffer.Span;

                    if (flat.Length != perImageValues)
                    {
                        throw new InvalidOperationException(
                            $"YOLOX output shape mismatch (per-image): expected {perImageValues} floats but got {flat.Length}.");
                    }

                    List<Detection> detections = DecodeRowAndNms(flat, scales[row]);
                    results[row] = BuildDetectionArray(detections);
                }
            }

            return results;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override IReadOnlyCollection<NamedOnnxValue> BuildBatchInputs(
        IReadOnlyList<IReadOnlyList<ValueRef>> rows)
        => throw new InvalidOperationException(
            "YoloXModel overrides InferBatchAsync directly to thread per-row letterbox scale " +
            "from preprocessing into postprocessing. BuildBatchInputs is not used.");

    /// <inheritdoc />
    protected override IReadOnlyList<ValueRef> ParseBatchOutputs(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs,
        int batchSize)
        => throw new InvalidOperationException(
            "YoloXModel overrides InferBatchAsync directly. ParseBatchOutputs is not used.");

    /// <summary>
    /// Decodes the encoded image bytes, letterbox-resizes preserving
    /// aspect ratio, places the resized image in the top-left of the
    /// padded square (filling the rest with gray <c>114</c>), and writes
    /// the result to <paramref name="dest"/> in <strong>BGR NCHW</strong>
    /// layout with <strong>no normalisation</strong> (raw 0–255 float).
    /// Outputs the scale factor (<c>min(target/h, target/w)</c>) so post-
    /// processing can reverse the letterbox.
    /// </summary>
    private static void DecodeAndPackBgrLetterbox(
        byte[] imageBytes, Span<float> dest, int targetSize, out float scale)
    {
        using SKBitmap? decoded = SKBitmap.Decode(imageBytes)
            ?? throw new InvalidOperationException("SkiaSharp failed to decode image bytes for YOLOX input.");

        int origW = decoded.Width;
        int origH = decoded.Height;

        // Letterbox scale: fit longest side into target.
        scale = MathF.Min(targetSize / (float)origH, targetSize / (float)origW);
        int newW = (int)(origW * scale);
        int newH = (int)(origH * scale);

        // Resize preserving aspect ratio.
        SKImageInfo resizedInfo = new(newW, newH, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap resized = decoded.Resize(resizedInfo, SKSamplingOptions.Default)
            ?? throw new InvalidOperationException(
                $"SkiaSharp failed to resize image to {newW}×{newH} for YOLOX letterbox.");

        int planeSize = targetSize * targetSize;
        // Initialise all three channel planes with the gray pad value (114).
        // YOLOX's training preproc fills the pad region with 114 in every
        // channel — same value for B, G, R.
        for (int i = 0; i < planeSize; i++)
        {
            dest[i] = PadValue;                  // B plane
            dest[planeSize + i] = PadValue;      // G plane
            dest[2 * planeSize + i] = PadValue;  // R plane
        }

        // Overwrite the top-left (newH × newW) region with the resized image,
        // re-ordering RGBA → BGR.
        nint pixelPtr = resized.GetPixels();
        unsafe
        {
            byte* source = (byte*)pixelPtr;
            for (int y = 0; y < newH; y++)
            {
                int rowDestBase = y * targetSize;
                int rowSrcBase = y * newW * 4;
                for (int x = 0; x < newW; x++)
                {
                    int srcOffset = rowSrcBase + x * 4;
                    int destIdx = rowDestBase + x;

                    // SKColorType.Rgba8888 byte order: R(0), G(1), B(2), A(3).
                    // YOLOX wants BGR channel order in the output tensor.
                    dest[destIdx]                   = source[srcOffset + 2]; // B → channel 0
                    dest[planeSize + destIdx]       = source[srcOffset + 1]; // G → channel 1
                    dest[2 * planeSize + destIdx]   = source[srcOffset];     // R → channel 2
                }
            }
        }
    }

    /// <summary>
    /// Decodes a single image's <c>[anchors × 85]</c> output slice
    /// (anchor-major: each anchor's 85 values are contiguous), filters
    /// by <c>objectness × max_class_score</c>, applies NMS, and reverses
    /// the letterbox scale to produce bbox coords in the original image's
    /// pixel space.
    /// </summary>
    private List<Detection> DecodeRowAndNms(ReadOnlySpan<float> rowSlice, float scale)
    {
        List<Detection> candidates = new();

        for (int anchor = 0; anchor < _expectedAnchors; anchor++)
        {
            int anchorBase = anchor * ValuesPerPrediction;
            float cx = rowSlice[anchorBase];
            float cy = rowSlice[anchorBase + 1];
            float bw = rowSlice[anchorBase + 2];
            float bh = rowSlice[anchorBase + 3];
            float objectness = rowSlice[anchorBase + 4];

            // Find max class score for this anchor.
            float bestClassScore = 0f;
            int bestClass = -1;
            for (int c = 0; c < NumClasses; c++)
            {
                float score = rowSlice[anchorBase + 5 + c];
                if (score > bestClassScore)
                {
                    bestClassScore = score;
                    bestClass = c;
                }
            }

            // YOLOX confidence: objectness × class_score. Both are sigmoid-
            // activated by the model's built-in decoder, so they're already
            // in [0, 1].
            float confidence = objectness * bestClassScore;
            if (confidence < ConfidenceThreshold) continue;

            // Reverse letterbox: divide by scale to get original-image coords.
            // Image is in top-left of the padded square so no offset to subtract.
            float x = (cx - bw * 0.5f) / scale;
            float y = (cy - bh * 0.5f) / scale;
            float w = bw / scale;
            float h = bh / scale;

            candidates.Add(new Detection(bestClass, confidence, x, y, w, h));
        }

        return ApplyNms(candidates, IouThreshold);
    }

    /// <summary>
    /// Class-aware non-maximum suppression. Same as <see cref="YoloModel"/>'s
    /// implementation: sort by score descending; for each kept box, suppress
    /// any later same-class candidate with IoU above the threshold.
    /// </summary>
    private static List<Detection> ApplyNms(List<Detection> candidates, float iouThreshold)
    {
        if (candidates.Count == 0) return candidates;

        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        bool[] suppressed = new bool[candidates.Count];
        List<Detection> kept = new();

        for (int i = 0; i < candidates.Count; i++)
        {
            if (suppressed[i]) continue;
            kept.Add(candidates[i]);
            for (int j = i + 1; j < candidates.Count; j++)
            {
                if (suppressed[j]) continue;
                if (candidates[j].ClassId != candidates[i].ClassId) continue;
                if (Iou(candidates[i], candidates[j]) > iouThreshold)
                {
                    suppressed[j] = true;
                }
            }
        }

        return kept;
    }

    private static float Iou(Detection a, Detection b)
    {
        float ax1 = a.X, ay1 = a.Y, ax2 = a.X + a.W, ay2 = a.Y + a.H;
        float bx1 = b.X, by1 = b.Y, bx2 = b.X + b.W, by2 = b.Y + b.H;

        float ix1 = MathF.Max(ax1, bx1);
        float iy1 = MathF.Max(ay1, by1);
        float ix2 = MathF.Min(ax2, bx2);
        float iy2 = MathF.Min(ay2, by2);

        float iw = MathF.Max(0, ix2 - ix1);
        float ih = MathF.Max(0, iy2 - iy1);
        float intersection = iw * ih;
        float union = a.W * a.H + b.W * b.H - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    private ValueRef BuildDetectionArray(List<Detection> detections)
    {
        if (detections.Count == 0)
        {
            return ValueRef.FromArray(DataKind.Struct, Array.Empty<ValueRef>());
        }

        ValueRef[] elements = new ValueRef[detections.Count];
        for (int i = 0; i < detections.Count; i++)
        {
            Detection d = detections[i];
            elements[i] = ValueRef.FromStruct(
            [
                ValueRef.FromString(_labels[d.ClassId]),
                ValueRef.FromFloat32(d.Score),
                ValueRef.FromFloat32(d.X),
                ValueRef.FromFloat32(d.Y),
                ValueRef.FromFloat32(d.W),
                ValueRef.FromFloat32(d.H),
            ]);
        }
        return ValueRef.FromArray(DataKind.Struct, elements);
    }

    private readonly record struct Detection(int ClassId, float Score, float X, float Y, float W, float H);
}
