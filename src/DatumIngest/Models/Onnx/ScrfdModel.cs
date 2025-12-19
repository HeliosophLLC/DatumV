using DatumIngest.Functions;
using DatumIngest.Model;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

using SkiaSharp;

namespace DatumIngest.Models.Onnx;

/// <summary>
/// SCRFD face detector wrapped as an <see cref="IModel"/>. SCRFD ("Sample
/// and Computation Redistribution for Face Detection") is the InsightFace
/// successor to RetinaFace — same general FPN-with-anchors architecture
/// but with distance-based bbox regression instead of prior-box deltas, and
/// a single foreground score per anchor rather than a softmaxed pair.
/// Returns one detection-array per image; each detection is a struct with
/// score, bbox, and the 5 facial landmarks in original-image pixel space.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Input pipeline.</strong> Each image is decoded via SkiaSharp,
/// letterboxed to 640×640 (resized preserving aspect ratio so the longer
/// side hits 640, then placed at the top-left of an otherwise-zero canvas),
/// and packed as <strong>NCHW</strong> float32 in <strong>RGB</strong>
/// channel order with <c>(pixel - 127.5) / 128</c> normalisation. SCRFD's
/// reference Python wrapper builds this via OpenCV's
/// <c>cv2.dnn.blobFromImage(img, 1/128, size, (127.5, 127.5, 127.5),
/// swapRB=True)</c> — we do the equivalent inline. The padded region stays
/// at <c>-127.5/128 ≈ -0.992</c> (i.e. zero-pixel becomes that value after
/// normalisation), matching what <c>blobFromImage</c> would produce on a
/// zero-padded canvas.
/// </para>
/// <para>
/// <strong>Output shape.</strong> The model emits 9 anonymous tensors —
/// 3 outputs per stride at strides 8 / 16 / 32. Per stride the outputs are
/// score <c>[K, 1]</c>, bbox <c>[K, 4]</c>, kps <c>[K, 10]</c>, where
/// <c>K = (input/stride)² × 2</c> for the 2 anchors per cell. We identify
/// outputs by shape (the names in the buffalo_l export are numeric IDs).
/// Bbox values are distances <c>(left, top, right, bottom)</c> from the
/// anchor centre in stride-units; multiply by stride and subtract/add from
/// the centre to recover pixel-space corners. Keypoints are similarly
/// stride-scaled offsets. Scores are sigmoid-activated foreground
/// probabilities (no background channel).
/// </para>
/// <para>
/// <strong>SQL surface.</strong> Each row's output column is an
/// <c>Array&lt;Struct{score: Float32, x: Float32, y: Float32, w: Float32, h: Float32, landmarks: Array&lt;Struct{x: Float32, y: Float32}&gt;}&gt;</c>
/// — same shape as the previous RetinaFace surface.
/// </para>
/// </remarks>
public sealed class ScrfdModel : OnnxModel
{
    private const int InputSize = 640;
    private const int InputChannels = 3;
    private const int NumAnchorsPerCell = 2;
    private const int NumLandmarks = 5;

    private static readonly int[] FpnStrides = [8, 16, 32];

    // Pre-computed anchor counts at 640×640 input. K = (640/stride)² × 2.
    private static readonly int[] AnchorCountsByStride = [12800, 3200, 800];

    // Standard SCRFD normalisation. The reference Python uses 127.5 mean
    // and 1/128 scale; the slight mean/scale mismatch (mean is half of
    // 255, scale is half of 256) is inherited from the InsightFace
    // codebase. Matching it bit-for-bit is necessary — the network was
    // trained against exactly this normalisation.
    private const float InputMean = 127.5f;
    private const float InputScale = 1f / 128f;

    private readonly string _onnxInputName;

    // Resolved per stride (fpn index 0=stride8, 1=stride16, 2=stride32):
    // score / bbox / kps output names. The buffalo_l export uses anonymous
    // numeric names; we map them by element-count of their shape.
    private readonly string[] _scoreOutputNames = new string[3];
    private readonly string[] _bboxOutputNames = new string[3];
    private readonly string[] _kpsOutputNames = new string[3];

    /// <summary>
    /// Construction-time default score threshold. Per-row callers can
    /// override via the optional <c>confidence_threshold</c> argument.
    /// Defaults to 0.5 — SCRFD scores are well-calibrated and 0.5 is the
    /// threshold used throughout the InsightFace ecosystem.
    /// </summary>
    public float ConfidenceThreshold { get; }

    /// <summary>
    /// Construction-time default IoU threshold for NMS. Per-row callers
    /// can override via the optional <c>iou_threshold</c> argument.
    /// Defaults to 0.4.
    /// </summary>
    public float IouThreshold { get; }

    /// <summary>
    /// Loads SCRFD from <paramref name="modelFilePath"/>. Expects the
    /// buffalo_l-style export with NCHW input <c>[1, 3, H, W]</c> and the
    /// 9 FPN outputs (score / bbox / kps × 3 strides) — verified at
    /// construction by element-count.
    /// </summary>
    public ScrfdModel(
        string name,
        string modelFilePath,
        float confidenceThreshold = 0.5f,
        float iouThreshold = 0.4f)
        : base(
            name,
            modelFilePath,
            inputKinds: [DataKind.Image],
            outputKind: DataKind.Struct,
            isDeterministic: true)
    {
        ConfidenceThreshold = confidenceThreshold;
        IouThreshold = iouThreshold;

        _onnxInputName = Session.InputMetadata.Keys.FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"ONNX model at '{modelFilePath}' has no input metadata; cannot determine input tensor name.");

        ResolveOutputNames();
    }

    /// <inheritdoc />
    /// <remarks>
    /// The <c>landmarks</c> field is declared as <c>Array&lt;Struct{x, y}&gt;</c>
    /// using the nested-Fields constructor plus <c>IsArray = true</c>, so the
    /// operator's <c>InternStructFromColumnInfoFields</c> recurses through and
    /// registers both the outer detection struct and the inner keypoint struct
    /// in the per-query <c>TypeRegistry</c>. Without the nested declaration,
    /// inner structs would surface to consumers as nameless field-less shapes.
    /// </remarks>
    public override IReadOnlyList<ColumnInfo>? OutputFields =>
    [
        new ColumnInfo("score", DataKind.Float32, nullable: false),
        new ColumnInfo("x", DataKind.Float32, nullable: false),
        new ColumnInfo("y", DataKind.Float32, nullable: false),
        new ColumnInfo("w", DataKind.Float32, nullable: false),
        new ColumnInfo("h", DataKind.Float32, nullable: false),
        new ColumnInfo("landmarks", nullable: false,
        [
            new ColumnInfo("x", DataKind.Float32, nullable: false),
            new ColumnInfo("y", DataKind.Float32, nullable: false),
        ])
        { IsArray = true },
    ];

    /// <inheritdoc />
    public override async Task<IReadOnlyList<ValueRef>> InferBatchAsync(
        IReadOnlyList<IReadOnlyList<ValueRef>> inputs,
        IReadOnlyList<IReadOnlyList<ValueRef>> overrides,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (inputs.Count == 0) return [];

        // The exported graph accepts dynamic spatial dims but we resize to a
        // fixed 640×640 letterbox, so each Run() processes one image with a
        // pinned shape. Per-row override slots match RetinaFace's design.
        int batchSize = inputs.Count;
        int planeFloats = InputChannels * InputSize * InputSize;

        ValueRef[] results = new ValueRef[batchSize];

        for (int row = 0; row < batchSize; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ValueRef image = inputs[row][0];
            if (image.IsNull)
            {
                throw new InvalidOperationException(
                    $"ScrfdModel received a null image at row {row}; filter nulls upstream before invoking the model.");
            }

            // Optional per-row hyperparameters declared by the catalog as
            //   [0] confidence_threshold (Float64)
            //   [1] iou_threshold        (Float64)
            // Missing or null entries fall back to construction-time defaults.
            IReadOnlyList<ValueRef> rowOverrides = overrides.Count > row
                ? overrides[row]
                : [];
            float rowConfidence = rowOverrides.Count > 0 && !rowOverrides[0].IsNull
                ? rowOverrides[0].ToFloat()
                : ConfidenceThreshold;
            float rowIou = rowOverrides.Count > 1 && !rowOverrides[1].IsNull
                ? rowOverrides[1].ToFloat()
                : IouThreshold;

            byte[] bytes = image.AsBytes();
            float[] tensorData = new float[planeFloats];
            float ratio = LetterboxAndPackNchwRgb(bytes, tensorData);
            float inverseScale = 1f / ratio;

            float capturedConfidence = rowConfidence;
            float capturedIou = rowIou;
            float capturedInverse = inverseScale;
            results[row] = await Task.Run<ValueRef>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                DenseTensor<float> tensor = new(
                    tensorData,
                    [1, InputChannels, InputSize, InputSize]);
                NamedOnnxValue input = OnnxTensorConversion.CreateAutoCastInput(Session, _onnxInputName, tensor);

                using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = Session.Run([input]);

                cancellationToken.ThrowIfCancellationRequested();

                return DecodeImage(outputs, capturedInverse, capturedConfidence, capturedIou);
            }, cancellationToken).ConfigureAwait(false);
        }

        return results;
    }

    /// <inheritdoc />
    protected override IReadOnlyCollection<NamedOnnxValue> BuildBatchInputs(
        IReadOnlyList<IReadOnlyList<ValueRef>> rows)
        => throw new InvalidOperationException(
            "ScrfdModel overrides InferBatchAsync directly. BuildBatchInputs is not used.");

    /// <inheritdoc />
    protected override IReadOnlyList<ValueRef> ParseBatchOutputs(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs,
        int batchSize)
        => throw new InvalidOperationException(
            "ScrfdModel overrides InferBatchAsync directly. ParseBatchOutputs is not used.");

    /// <summary>
    /// Resolves the 9 output names to (score, bbox, kps) per stride by
    /// matching the declared element-count of each output against the
    /// expected K × {1, 4, 10} for the 640×640 input. The buffalo_l export
    /// uses anonymous numeric names so this match-by-shape is the only
    /// reliable hook.
    /// </summary>
    private void ResolveOutputNames()
    {
        // Map each declared output name → its trailing-dim count (1 / 4 / 10).
        // The leading dim is K which depends on stride (12800 / 3200 / 800
        // for strides 8 / 16 / 32 at 640×640 input).
        Dictionary<int, List<(string Name, int K)>> byChannelCount = new()
        {
            [1] = [],
            [4] = [],
            [10] = [],
        };
        foreach (KeyValuePair<string, NodeMetadata> kv in Session.OutputMetadata)
        {
            int[] dims = kv.Value.Dimensions;
            if (dims.Length != 2)
            {
                continue;
            }
            int channels = dims[1];
            if (!byChannelCount.ContainsKey(channels)) continue;
            byChannelCount[channels].Add((kv.Key, dims[0]));
        }

        if (byChannelCount[1].Count != 3 || byChannelCount[4].Count != 3 || byChannelCount[10].Count != 3)
        {
            throw new InvalidOperationException(
                $"SCRFD ONNX expected 3 outputs each of channel-count {{1, 4, 10}} but found "
                + $"{byChannelCount[1].Count}/{byChannelCount[4].Count}/{byChannelCount[10].Count}. "
                + $"All outputs: [{string.Join(", ", Session.OutputMetadata.Keys)}]");
        }

        // Within each channel-count group, sort by K descending so that
        // index 0 (largest K = 12800) maps to stride 8, index 1 (3200) to
        // stride 16, index 2 (800) to stride 32.
        for (int i = 0; i < 3; i++)
        {
            byChannelCount[1].Sort((a, b) => b.K.CompareTo(a.K));
            byChannelCount[4].Sort((a, b) => b.K.CompareTo(a.K));
            byChannelCount[10].Sort((a, b) => b.K.CompareTo(a.K));
        }

        for (int i = 0; i < 3; i++)
        {
            (string scoreName, int scoreK) = byChannelCount[1][i];
            (string bboxName, int bboxK) = byChannelCount[4][i];
            (string kpsName, int kpsK) = byChannelCount[10][i];

            int expectedK = AnchorCountsByStride[i];
            if (scoreK != expectedK || bboxK != expectedK || kpsK != expectedK)
            {
                throw new InvalidOperationException(
                    $"SCRFD ONNX K-count mismatch at fpn[{i}] (stride={FpnStrides[i]}): expected K={expectedK} "
                    + $"but got score={scoreK}, bbox={bboxK}, kps={kpsK}. "
                    + $"Was the ONNX exported for a different input size than 640×640?");
            }

            _scoreOutputNames[i] = scoreName;
            _bboxOutputNames[i] = bboxName;
            _kpsOutputNames[i] = kpsName;
        }
    }

    /// <summary>
    /// Letterboxes the image into the 640×640 NCHW tensor preserving
    /// aspect ratio. Pixels are normalised as <c>(rgb - 127.5) / 128</c>
    /// in RGB channel order. The image is placed at the top-left; the
    /// padded region is filled with the same normalised value that a
    /// zero-pixel would produce (<c>-127.5/128</c>), matching OpenCV's
    /// <c>blobFromImage</c> behaviour on a zero-padded canvas.
    /// </summary>
    private static float LetterboxAndPackNchwRgb(byte[] imageBytes, float[] dest)
    {
        using SKBitmap? decoded = SKBitmap.Decode(imageBytes)
            ?? throw new InvalidOperationException("SkiaSharp failed to decode image bytes for SCRFD input.");

        int origW = decoded.Width;
        int origH = decoded.Height;

        float ratio = MathF.Min((float)InputSize / origW, (float)InputSize / origH);
        int newW = Math.Max(1, Math.Min(InputSize, (int)MathF.Round(origW * ratio)));
        int newH = Math.Max(1, Math.Min(InputSize, (int)MathF.Round(origH * ratio)));

        SKImageInfo targetInfo = new(newW, newH, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap resized = decoded.Resize(targetInfo, SKSamplingOptions.Default)
            ?? throw new InvalidOperationException(
                $"SkiaSharp failed to resize image to {newW}×{newH} for SCRFD letterbox.");

        // Fill the entire tensor with the "zero pixel" value first — that's
        // what blobFromImage would emit for the padded region. The actual
        // image pixels overwrite the top-left rectangle below.
        const float ZeroPixelNormalised = (0f - InputMean) * InputScale; // ≈ -0.992
        Array.Fill(dest, ZeroPixelNormalised);

        nint pixelPtr = resized.GetPixels();
        int planeSize = InputSize * InputSize;

        unsafe
        {
            byte* source = (byte*)pixelPtr;
            // NCHW: each channel is a contiguous H*W plane.
            // R plane: dest[0..planeSize), G plane: [planeSize..2*planeSize),
            // B plane: [2*planeSize..3*planeSize). Skia gives RGBA bytes,
            // so source offset 0=R, 1=G, 2=B.
            for (int y = 0; y < newH; y++)
            {
                int srcRowOffset = y * newW * 4;
                int dstRowOffset = y * InputSize;
                for (int x = 0; x < newW; x++)
                {
                    int srcOffset = srcRowOffset + x * 4;
                    int dstPixel = dstRowOffset + x;
                    dest[dstPixel] = (source[srcOffset + 0] - InputMean) * InputScale;                 // R plane
                    dest[planeSize + dstPixel] = (source[srcOffset + 1] - InputMean) * InputScale;     // G plane
                    dest[2 * planeSize + dstPixel] = (source[srcOffset + 2] - InputMean) * InputScale; // B plane
                }
            }
        }

        return ratio;
    }

    private ValueRef DecodeImage(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs,
        float inverseScale,
        float confidenceThreshold,
        float iouThreshold)
    {
        Dictionary<string, DisposableNamedOnnxValue> byName = new(StringComparer.Ordinal);
        foreach (DisposableNamedOnnxValue v in outputs)
        {
            byName[v.Name] = v;
        }

        List<Detection> candidates = new();

        for (int fpn = 0; fpn < FpnStrides.Length; fpn++)
        {
            int stride = FpnStrides[fpn];
            DenseTensor<float> scoreTensor = OnnxTensorConversion.ToFloatTensor(byName[_scoreOutputNames[fpn]]);
            DenseTensor<float> bboxTensor = OnnxTensorConversion.ToFloatTensor(byName[_bboxOutputNames[fpn]]);
            DenseTensor<float> kpsTensor = OnnxTensorConversion.ToFloatTensor(byName[_kpsOutputNames[fpn]]);

            DecodeStride(
                stride,
                scoreTensor.Buffer.Span,
                bboxTensor.Buffer.Span,
                kpsTensor.Buffer.Span,
                inverseScale,
                confidenceThreshold,
                candidates);
        }

        List<Detection> kept = ApplyNms(candidates, iouThreshold);
        return BuildDetectionArray(kept);
    }

    /// <summary>
    /// Decodes one FPN level. Anchor centres are simply
    /// <c>(ix * stride, iy * stride)</c> repeated <see cref="NumAnchorsPerCell"/>
    /// times — no half-pixel offset like RetinaFace's MXNet variant. Bbox
    /// regression is distance-based: the four predicted values are
    /// <c>(left, top, right, bottom)</c> distances from the anchor centre,
    /// in stride-units (multiply by stride to get pixels). Keypoint deltas
    /// are similarly stride-scaled offsets from the anchor centre.
    /// </summary>
    private static void DecodeStride(
        int stride,
        ReadOnlySpan<float> scores,    // [K, 1]
        ReadOnlySpan<float> bboxDists, // [K, 4]   per-row: (l, t, r, b) in stride-units
        ReadOnlySpan<float> kpsDeltas, // [K, 10]  per-row: (dx0, dy0, dx1, dy1, ..., dx4, dy4) in stride-units
        float inverseScale,
        float confidenceThreshold,
        List<Detection> output)
    {
        int featSize = InputSize / stride;
        // K = featSize² × NumAnchorsPerCell. Iterate (iy, ix, anchor) so
        // index k = (iy * featSize + ix) * NumAnchorsPerCell + a maps onto
        // SCRFD's anchor-major layout (each cell's anchors adjacent in K).
        for (int iy = 0; iy < featSize; iy++)
        {
            for (int ix = 0; ix < featSize; ix++)
            {
                float anchorCx = ix * stride;
                float anchorCy = iy * stride;
                int cellBase = (iy * featSize + ix) * NumAnchorsPerCell;

                for (int a = 0; a < NumAnchorsPerCell; a++)
                {
                    int k = cellBase + a;
                    float score = scores[k];
                    if (score < confidenceThreshold) continue;

                    int bboxOffset = k * 4;
                    float distLeft = bboxDists[bboxOffset + 0] * stride;
                    float distTop = bboxDists[bboxOffset + 1] * stride;
                    float distRight = bboxDists[bboxOffset + 2] * stride;
                    float distBottom = bboxDists[bboxOffset + 3] * stride;

                    float x1 = anchorCx - distLeft;
                    float y1 = anchorCy - distTop;
                    float x2 = anchorCx + distRight;
                    float y2 = anchorCy + distBottom;

                    // Map letterboxed pixel coords back to original image space.
                    float ox = x1 * inverseScale;
                    float oy = y1 * inverseScale;
                    float ow = (x2 - x1) * inverseScale;
                    float oh = (y2 - y1) * inverseScale;

                    int kpsOffset = k * NumLandmarks * 2;
                    float[] lmX = new float[NumLandmarks];
                    float[] lmY = new float[NumLandmarks];
                    for (int p = 0; p < NumLandmarks; p++)
                    {
                        float dx = kpsDeltas[kpsOffset + p * 2 + 0] * stride;
                        float dy = kpsDeltas[kpsOffset + p * 2 + 1] * stride;
                        lmX[p] = (anchorCx + dx) * inverseScale;
                        lmY[p] = (anchorCy + dy) * inverseScale;
                    }

                    output.Add(new Detection(score, ox, oy, ow, oh, lmX, lmY));
                }
            }
        }
    }

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

    private static ValueRef BuildDetectionArray(List<Detection> detections)
    {
        if (detections.Count == 0)
        {
            return ValueRef.FromArray(DataKind.Struct, []);
        }

        ValueRef[] elements = new ValueRef[detections.Count];
        for (int i = 0; i < detections.Count; i++)
        {
            Detection d = detections[i];
            ValueRef[] keypoints = new ValueRef[NumLandmarks];
            for (int k = 0; k < NumLandmarks; k++)
            {
                keypoints[k] = ValueRef.FromStruct(
                [
                    ValueRef.FromFloat32(d.LandmarksX[k]),
                    ValueRef.FromFloat32(d.LandmarksY[k]),
                ]);
            }
            ValueRef landmarks = ValueRef.FromArray(DataKind.Struct, keypoints);

            elements[i] = ValueRef.FromStruct(
            [
                ValueRef.FromFloat32(d.Score),
                ValueRef.FromFloat32(d.X),
                ValueRef.FromFloat32(d.Y),
                ValueRef.FromFloat32(d.W),
                ValueRef.FromFloat32(d.H),
                landmarks,
            ]);
        }
        return ValueRef.FromArray(DataKind.Struct, elements);
    }

    private readonly record struct Detection(
        float Score,
        float X,
        float Y,
        float W,
        float H,
        float[] LandmarksX,
        float[] LandmarksY);
}
