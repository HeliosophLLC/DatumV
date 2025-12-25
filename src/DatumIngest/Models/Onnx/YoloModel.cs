using DatumIngest.Functions;
using DatumIngest.Model;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

using SkiaSharp;

namespace DatumIngest.Models.Onnx;

/// <summary>
/// YOLOv8 object detector wrapped as an <see cref="IModel"/>. Returns one
/// detection-array per image — each detection a struct with label, score,
/// and bounding-box coordinates (top-left origin, original-image pixels).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Input pipeline.</strong> Each image is decoded via SkiaSharp,
/// resized to 640×640 (stretched, not letterboxed — the simpler path; box
/// coords below scale back proportionally so detections stay correct on
/// non-square inputs), converted to NCHW float [0, 1], no per-channel
/// normalisation (YOLOv8 trains on raw [0,1] inputs).
/// </para>
/// <para>
/// <strong>Output shape.</strong> The ONNX graph emits <c>[N, 84, 8400]</c>
/// where 84 = 4 bbox values (cx, cy, w, h in 640×640 pixel space) + 80 COCO
/// class scores (sigmoid-activated, no objectness in v8). We transpose to
/// <c>[8400, 84]</c> for cache-friendly iteration, find the max class per
/// anchor, drop predictions below <see cref="ConfidenceThreshold"/>, run
/// NMS at <see cref="IouThreshold"/>, and scale surviving boxes back to the
/// original image's pixel dimensions.
/// </para>
/// <para>
/// <strong>SQL surface.</strong> Each row's output column is an
/// <c>Array&lt;Struct{label: String, score: Float32, x: Float32, y: Float32, w: Float32, h: Float32}&gt;</c>.
/// Element count varies by image (commonly 0–10).
/// </para>
/// </remarks>
public sealed class YoloModel : OnnxModel
{
    private const int InputSize = 640;
    private const int InputChannels = 3;
    private const int NumClasses = 80;
    private const int ValuesPerPrediction = 4 + NumClasses; // 84
    private const int NumAnchors = 8400;

    private readonly string _onnxInputName;
    private readonly string _onnxOutputName;
    private readonly IReadOnlyList<string> _labels;
    private readonly bool _supportsBatching;

    /// <summary>Score threshold below which a prediction is dropped pre-NMS. Defaults to 0.25.</summary>
    public float ConfidenceThreshold { get; }

    /// <summary>IoU threshold for NMS. Boxes with IoU above this are suppressed. Defaults to 0.45.</summary>
    public float IouThreshold { get; }

    /// <summary>
    /// Loads YOLOv8 from <paramref name="modelFilePath"/>. Defaults to the
    /// COCO-80 label vocabulary; pass <paramref name="labels"/> to override
    /// for custom-trained variants.
    /// </summary>
    public YoloModel(
        string name,
        string modelFilePath,
        IReadOnlyList<string>? labels = null,
        float confidenceThreshold = 0.25f,
        float iouThreshold = 0.45f)
        : base(
            name,
            modelFilePath,
            inputKinds: [DataKind.Image],
            // YOLO emits a typed array of detection structs (Kind=Struct + IsArray).
            // The IModel interface currently advertises only the kind; the IsArray
            // bit will land alongside the schema-layer collapse in the next PR.
            outputKind: DataKind.Struct,
            isDeterministic: true)
    {
        IReadOnlyList<string> resolvedLabels = labels ?? CocoLabels.Names;
        if (resolvedLabels.Count != NumClasses)
        {
            throw new ArgumentException(
                $"YOLOv8 expects {NumClasses} class labels but got {resolvedLabels.Count}. " +
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

        // Detect whether the model accepts a dynamic batch dimension. Ultralytics'
        // default export pins batch=1 (Dimensions[0] == 1, no symbolic name). With
        // `dynamic=True`, batch becomes -1 / has a symbolic name like "batch_size",
        // and one Run() call processes N images. We adapt at dispatch time.
        Microsoft.ML.OnnxRuntime.NodeMetadata inputMeta = Session.InputMetadata[_onnxInputName];
        int batchDim = inputMeta.Dimensions.Length > 0 ? inputMeta.Dimensions[0] : 1;
        bool symbolicBatchDim = inputMeta.SymbolicDimensions.Length > 0
            && !string.IsNullOrEmpty(inputMeta.SymbolicDimensions[0]);
        _supportsBatching = batchDim <= 0 || symbolicBatchDim;
    }

    /// <summary>
    /// Whether this model's ONNX graph accepts a dynamic batch dimension and
    /// can therefore process multiple images in one <see cref="InferenceSession.Run(IReadOnlyCollection{NamedOnnxValue})"/>
    /// call. <see langword="false"/> when the model was exported with a pinned
    /// <c>batch=1</c> dim (the Ultralytics default); <see langword="true"/>
    /// when exported with <c>dynamic=True</c>.
    /// </summary>
    public bool SupportsBatching => _supportsBatching;

    /// <inheritdoc />
    public override IReadOnlyList<ColumnInfo>? OutputFields =>
    [
        new ColumnInfo("label", DataKind.String, nullable: false),
        new ColumnInfo("score", DataKind.Float32, nullable: false),
        new ColumnInfo("x", DataKind.Float32, nullable: false),
        new ColumnInfo("y", DataKind.Float32, nullable: false),
        new ColumnInfo("w", DataKind.Float32, nullable: false),
        new ColumnInfo("h", DataKind.Float32, nullable: false),
    ];

    /// <inheritdoc />
    public override async Task<IReadOnlyList<ValueRef>> InferBatchAsync(
        IReadOnlyList<IReadOnlyList<ValueRef>> inputs,
        IReadOnlyList<IReadOnlyList<ValueRef>> overrides,
        CancellationToken cancellationToken)
    {
        _ = overrides;
        cancellationToken.ThrowIfCancellationRequested();
        if (inputs.Count == 0) return [];

        // Preprocess: decode + resize + pack. Capture per-row scale factors so
        // postprocessing can map detections back to the input image's pixel
        // dimensions (the model sees a fixed 640×640 view).
        int batchSize = inputs.Count;
        int planeSize = InputSize * InputSize;
        int perImageFloats = InputChannels * planeSize;
        float[] tensorData = new float[batchSize * perImageFloats];
        float[] scaleX = new float[batchSize];
        float[] scaleY = new float[batchSize];

        for (int row = 0; row < batchSize; row++)
        {
            ValueRef image = inputs[row][0];
            if (image.IsNull)
            {
                throw new InvalidOperationException(
                    $"YoloModel received a null image at row {row}; filter nulls upstream before invoking the model.");
            }
            SKBitmap decoded = image.AsImage();
            ResizeAndPack(decoded, tensorData.AsSpan(row * perImageFloats, perImageFloats), out int origW, out int origH);
            scaleX[row] = origW / (float)InputSize;
            scaleY[row] = origH / (float)InputSize;
        }

        return await Task.Run<IReadOnlyList<ValueRef>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            int perImageValues = ValuesPerPrediction * NumAnchors;
            ValueRef[] results = new ValueRef[batchSize];

            if (_supportsBatching && batchSize > 1)
            {
                // Dynamic-batch model: one Session.Run for the whole batch.
                // Output shape is [batchSize, 84, 8400]; slice per row.
                DenseTensor<float> tensor = new(
                    tensorData,
                    [batchSize, InputChannels, InputSize, InputSize]);
                NamedOnnxValue input = OnnxTensorConversion.CreateAutoCastInput(Session, _onnxInputName, tensor);

                using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = Session.Run([input]);
                DisposableNamedOnnxValue first = outputs.FirstOrDefault()
                    ?? throw new InvalidOperationException("YOLOv8 ONNX session returned no outputs.");

                DenseTensor<float> raw = OnnxTensorConversion.ToFloatTensor(first);
                ReadOnlySpan<float> flat = raw.Buffer.Span;

                if (flat.Length != batchSize * perImageValues)
                {
                    throw new InvalidOperationException(
                        $"YOLOv8 output shape mismatch (batched): expected {batchSize * perImageValues} floats " +
                        $"but got {flat.Length}. Per-image expected {perImageValues}.");
                }

                for (int row = 0; row < batchSize; row++)
                {
                    ReadOnlySpan<float> rowSlice = flat.Slice(row * perImageValues, perImageValues);
                    List<Detection> detections = DecodeRowAndNms(rowSlice, scaleX[row], scaleY[row]);
                    results[row] = BuildDetectionArray(detections);
                }
            }
            else
            {
                // Fixed-batch=1 model (Ultralytics default export): loop one
                // image per Run(). Re-export with `dynamic=True` to take the
                // batched path above when throughput matters.
                for (int row = 0; row < batchSize; row++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    DenseTensor<float> tensor = new(
                        tensorData.AsMemory(row * perImageFloats, perImageFloats),
                        [1, InputChannels, InputSize, InputSize]);
                    NamedOnnxValue input = OnnxTensorConversion.CreateAutoCastInput(Session, _onnxInputName, tensor);

                    using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = Session.Run([input]);
                    DisposableNamedOnnxValue first = outputs.FirstOrDefault()
                        ?? throw new InvalidOperationException("YOLOv8 ONNX session returned no outputs.");

                    DenseTensor<float> raw = OnnxTensorConversion.ToFloatTensor(first);
                    ReadOnlySpan<float> flat = raw.Buffer.Span;

                    if (flat.Length != perImageValues)
                    {
                        throw new InvalidOperationException(
                            $"YOLOv8 output shape mismatch (per-image): expected {perImageValues} floats but got {flat.Length}.");
                    }

                    List<Detection> detections = DecodeRowAndNms(flat, scaleX[row], scaleY[row]);
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
            "YoloModel overrides InferBatchAsync directly to thread per-row scale factors " +
            "from preprocessing into postprocessing. BuildBatchInputs is not used.");

    /// <inheritdoc />
    protected override IReadOnlyList<ValueRef> ParseBatchOutputs(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs,
        int batchSize)
        => throw new InvalidOperationException(
            "YoloModel overrides InferBatchAsync directly. ParseBatchOutputs is not used.");

    private static void ResizeAndPack(SKBitmap decoded, Span<float> dest, out int origWidth, out int origHeight)
    {
        origWidth = decoded.Width;
        origHeight = decoded.Height;

        SKImageInfo targetInfo = new(InputSize, InputSize, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap resized = decoded.Resize(targetInfo, SKSamplingOptions.Default)
            ?? throw new InvalidOperationException(
                $"SkiaSharp failed to resize image to {InputSize}×{InputSize} for YOLO input.");

        int planeSize = InputSize * InputSize;
        nint pixelPtr = resized.GetPixels();

        unsafe
        {
            byte* source = (byte*)pixelPtr;
            for (int yx = 0; yx < planeSize; yx++)
            {
                int srcOffset = yx * 4;
                dest[yx] = source[srcOffset] / 255f;                          // R plane
                dest[planeSize + yx] = source[srcOffset + 1] / 255f;          // G plane
                dest[2 * planeSize + yx] = source[srcOffset + 2] / 255f;      // B plane
            }
        }
    }

    /// <summary>
    /// Decodes a row's <c>[84 × 8400]</c> slice (column-major: values laid out
    /// as <c>val0_anchor0..val0_anchor8399, val1_anchor0..</c>), filters by
    /// confidence, applies NMS, scales surviving boxes to original-image
    /// pixel coords (top-left origin), and returns them.
    /// </summary>
    private List<Detection> DecodeRowAndNms(ReadOnlySpan<float> rowSlice, float sx, float sy)
    {
        // YOLOv8 ONNX output is column-major: rowSlice[v * 8400 + a] is value v of anchor a.
        List<Detection> candidates = new();

        for (int anchor = 0; anchor < NumAnchors; anchor++)
        {
            // Find max class score for this anchor.
            float bestScore = 0f;
            int bestClass = -1;
            for (int c = 0; c < NumClasses; c++)
            {
                float score = rowSlice[(4 + c) * NumAnchors + anchor];
                if (score > bestScore)
                {
                    bestScore = score;
                    bestClass = c;
                }
            }

            if (bestScore < ConfidenceThreshold) continue;

            // Box in 640×640 space: (cx, cy, w, h) → top-left (x, y).
            float cx = rowSlice[0 * NumAnchors + anchor];
            float cy = rowSlice[1 * NumAnchors + anchor];
            float bw = rowSlice[2 * NumAnchors + anchor];
            float bh = rowSlice[3 * NumAnchors + anchor];

            // Scale back to original-image dimensions.
            float x = (cx - bw * 0.5f) * sx;
            float y = (cy - bh * 0.5f) * sy;
            float w = bw * sx;
            float h = bh * sy;

            candidates.Add(new Detection(bestClass, bestScore, x, y, w, h));
        }

        return ApplyNms(candidates, IouThreshold);
    }

    /// <summary>
    /// Class-aware non-maximum suppression. Sorts candidates by score
    /// descending; for each, suppresses any later candidate of the same
    /// class whose box has IoU above <paramref name="iouThreshold"/> with
    /// the kept candidate. Different classes don't suppress each other —
    /// a person standing next to a bicycle keeps both detections.
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
