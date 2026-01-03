using DatumIngest.Functions;
using DatumIngest.Model;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

using SkiaSharp;

namespace DatumIngest.Models.Onnx;

/// <summary>
/// PaddleOCR PP-OCRv4 text detector wrapped as an <see cref="IModel"/>.
/// DBNet++-style segmentation model that emits a single-channel
/// probability map, post-processed into axis-aligned text-line bounding
/// boxes. Returns one detection-array per image; each detection is a
/// struct with a constant <c>label = "text"</c>, the mean probability
/// over the region, and the bbox in original-image pixel space.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Pairs with a recognizer.</strong> This is detection only —
/// it finds <em>where</em> text is, not <em>what</em> it says. Crop
/// each detected box and run a recognizer (e.g. <c>trocr_printed</c>)
/// on each crop to read the text:
/// <code>
/// SELECT receipt_id, models.trocr_printed_fp16(
///          image_crop(photo, det.x, det.y, det.w, det.h)) AS line
/// FROM receipts
/// CROSS APPLY UNNEST(models.ppocr_det_v4(photo)) AS det
/// </code>
/// </para>
/// <para>
/// <strong>Input pipeline.</strong> Each image is decoded via
/// SkiaSharp, aspect-preservingly resized so the longer side hits at
/// most <see cref="MaxSide"/> (960 px), then both dimensions are
/// rounded to the nearest multiple of <see cref="StridePx"/> (32 px) —
/// matching PaddleOCR's <c>DetResizeForTest</c>. Pixels are normalised
/// per-channel with ImageNet mean/std. NCHW RGB float32. The ONNX
/// graph accepts dynamic spatial dims so we don't need a fixed
/// letterbox.
/// </para>
/// <para>
/// <strong>Output post-processing (DBNet).</strong> The model emits
/// <c>[1, 1, H, W]</c> sigmoid probabilities at the resized
/// resolution. We threshold per-pixel at
/// <see cref="PixelThreshold"/> (0.3), find connected components via
/// BFS (4-connectivity), and accept each component whose mean
/// probability is at least <see cref="BoxScoreThreshold"/> (0.6) and
/// whose tight bbox is at least <see cref="MinSize"/> pixels on each
/// side. DBNet predicts <em>shrunken</em> regions, so each accepted
/// bbox is dilated by an "unclip" offset
/// <c>distance = area × <see cref="UnclipRatio"/> / perimeter</c>
/// (the standard DBNet polygon-offset formula reduced to the
/// axis-aligned case). Boxes are mapped back to original-image pixel
/// space by inverting the per-axis resize ratio.
/// </para>
/// <para>
/// <strong>Per-row overrides.</strong> Three optional float arguments,
/// matching the SCRFD pattern:
/// <c>[0] pixel_threshold</c>, <c>[1] box_score_threshold</c>,
/// <c>[2] unclip_ratio</c>. Pass nulls or omit to fall back to the
/// construction-time defaults.
/// </para>
/// </remarks>
public sealed class PpOcrDetectionModel : OnnxModel
{
    /// <summary>Maximum side length post-resize (PaddleOCR default).</summary>
    public const int MaxSide = 960;

    /// <summary>Network stride — both H and W are rounded to multiples of this.</summary>
    public const int StridePx = 32;

    private const int InputChannels = 3;

    // PaddleOCR uses ImageNet-statistics normalisation. Reuse the
    // pre-baked constants in ImageTensorPrep.
    private static readonly float[] NormScale = ImageTensorPrep.ImageNetScale;
    private static readonly float[] NormBias = ImageTensorPrep.ImageNetBias;

    // Every detection carries the constant `label = "text"` so the
    // leading (label, score, x, y, w, h) shape matches general-purpose
    // detectors like YOLOX / SCRFD. Cached once.
    private static readonly ValueRef TextLabel = ValueRef.FromString("text");

    private readonly string _onnxInputName;
    private readonly string _onnxOutputName;

    /// <summary>
    /// Per-pixel sigmoid threshold for the binary mask. Default 0.3 —
    /// PaddleOCR's <c>det_db_thresh</c>. Lower → more permissive,
    /// catches faint text but produces more false positives.
    /// </summary>
    public float PixelThreshold { get; }

    /// <summary>
    /// Mean-probability threshold per connected component. Default
    /// 0.6 — PaddleOCR's <c>det_db_box_thresh</c>. Components with a
    /// lower mean are dropped even if their pixel mask exceeds
    /// <see cref="PixelThreshold"/>.
    /// </summary>
    public float BoxScoreThreshold { get; }

    /// <summary>
    /// DBNet polygon-offset ratio. Default 1.5 — PaddleOCR's
    /// <c>det_db_unclip_ratio</c>. Larger → boxes are dilated more,
    /// useful when the recognizer needs extra margin around glyphs.
    /// </summary>
    public float UnclipRatio { get; }

    /// <summary>
    /// Minimum width / height (in resized-pixel space) for a component
    /// to be accepted. Default 3.
    /// </summary>
    public int MinSize { get; }

    /// <summary>
    /// Loads PP-OCRv4-det from <paramref name="modelFilePath"/>.
    /// Expects single-input <c>[B, 3, H, W]</c> with dynamic spatial
    /// dims and a single-output <c>[B, 1, H, W]</c> sigmoid probability
    /// map — verified at construction.
    /// </summary>
    public PpOcrDetectionModel(
        string name,
        string modelFilePath,
        float pixelThreshold = 0.3f,
        float boxScoreThreshold = 0.6f,
        float unclipRatio = 1.5f,
        int minSize = 3)
        : base(
            name,
            modelFilePath,
            inputKinds: [DataKind.Image],
            outputKind: DataKind.Struct,
            isDeterministic: true)
    {
        PixelThreshold = pixelThreshold;
        BoxScoreThreshold = boxScoreThreshold;
        UnclipRatio = unclipRatio;
        MinSize = minSize;

        _onnxInputName = Session.InputMetadata.Keys.FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"PP-OCRv4-det ONNX at '{modelFilePath}' has no input metadata.");
        _onnxOutputName = Session.OutputMetadata.Keys.FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"PP-OCRv4-det ONNX at '{modelFilePath}' has no output metadata.");
    }

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
        cancellationToken.ThrowIfCancellationRequested();
        if (inputs.Count == 0) return [];

        // Per-row dispatch: each image gets its own resized shape
        // (different aspect ratios → different padded H/W), so we can't
        // pack a single batched tensor cheaply. Independent Run() calls
        // also keep cancellation responsive.
        int batchSize = inputs.Count;
        ValueRef[] results = new ValueRef[batchSize];

        for (int row = 0; row < batchSize; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ValueRef image = inputs[row][0];
            if (image.IsNull)
            {
                throw new InvalidOperationException(
                    $"PpOcrDetectionModel received a null image at row {row}; filter nulls upstream.");
            }

            // Optional per-row overrides:
            //   [0] pixel_threshold      (Float64)
            //   [1] box_score_threshold  (Float64)
            //   [2] unclip_ratio         (Float64)
            IReadOnlyList<ValueRef> rowOverrides = overrides.Count > row
                ? overrides[row]
                : [];
            float rowPixelTh = rowOverrides.Count > 0 && !rowOverrides[0].IsNull
                ? rowOverrides[0].ToFloat()
                : PixelThreshold;
            float rowBoxTh = rowOverrides.Count > 1 && !rowOverrides[1].IsNull
                ? rowOverrides[1].ToFloat()
                : BoxScoreThreshold;
            float rowUnclip = rowOverrides.Count > 2 && !rowOverrides[2].IsNull
                ? rowOverrides[2].ToFloat()
                : UnclipRatio;

            SKBitmap decoded = image.AsImage();
            int origW = decoded.Width;
            int origH = decoded.Height;
            (int newW, int newH) = ComputeResizeShape(origW, origH);
            float scaleBackX = (float)origW / newW;
            float scaleBackY = (float)origH / newH;

            float[] tensorData = new float[InputChannels * newW * newH];
            ImageTensorPrep.StretchAndPackNchw(
                decoded, tensorData, newW, newH, NormScale, NormBias, bgr: false);

            float capturedPixelTh = rowPixelTh;
            float capturedBoxTh = rowBoxTh;
            float capturedUnclip = rowUnclip;
            int capturedW = newW;
            int capturedH = newH;
            float capturedScaleX = scaleBackX;
            float capturedScaleY = scaleBackY;
            int capturedOrigW = origW;
            int capturedOrigH = origH;

            results[row] = await Task.Run<ValueRef>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                DenseTensor<float> tensor = new(
                    tensorData,
                    [1, InputChannels, capturedH, capturedW]);
                NamedOnnxValue input = OnnxTensorConversion.CreateAutoCastInput(
                    Session, _onnxInputName, tensor);

                using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = Session.Run([input]);
                cancellationToken.ThrowIfCancellationRequested();

                DisposableNamedOnnxValue probValue = outputs.FirstOrDefault(v => v.Name == _onnxOutputName)
                    ?? outputs.First();
                DenseTensor<float> probMap = OnnxTensorConversion.ToFloatTensor(probValue);
                int[] probShape = probMap.Dimensions.ToArray();
                // Expected: [1, 1, H, W]. The exporter ensures channel=1.
                if (probShape.Length != 4 || probShape[0] != 1 || probShape[1] != 1
                    || probShape[2] != capturedH || probShape[3] != capturedW)
                {
                    throw new InvalidOperationException(
                        $"PP-OCRv4-det output shape {string.Join('x', probShape)} doesn't match "
                        + $"expected [1, 1, {capturedH}, {capturedW}].");
                }

                List<TextRegion> regions = FindRegions(
                    probMap.Buffer.Span,
                    capturedW, capturedH,
                    capturedPixelTh,
                    capturedBoxTh,
                    MinSize,
                    capturedUnclip,
                    capturedScaleX, capturedScaleY,
                    capturedOrigW, capturedOrigH);

                return BuildDetectionArray(regions);
            }, cancellationToken).ConfigureAwait(false);
        }

        return results;
    }

    /// <inheritdoc />
    protected override IReadOnlyCollection<NamedOnnxValue> BuildBatchInputs(
        IReadOnlyList<IReadOnlyList<ValueRef>> rows)
        => throw new InvalidOperationException(
            "PpOcrDetectionModel overrides InferBatchAsync directly. BuildBatchInputs is not used.");

    /// <inheritdoc />
    protected override IReadOnlyList<ValueRef> ParseBatchOutputs(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs,
        int batchSize)
        => throw new InvalidOperationException(
            "PpOcrDetectionModel overrides InferBatchAsync directly. ParseBatchOutputs is not used.");

    /// <summary>
    /// Aspect-preserving resize: longest side ≤ <see cref="MaxSide"/>,
    /// then both dims rounded to nearest multiple of <see cref="StridePx"/>
    /// (min one stride). Matches PaddleOCR's <c>DetResizeForTest</c>.
    /// </summary>
    private static (int Width, int Height) ComputeResizeShape(int origW, int origH)
    {
        float ratio = 1f;
        int longest = Math.Max(origW, origH);
        if (longest > MaxSide)
        {
            ratio = (float)MaxSide / longest;
        }
        int targetW = (int)MathF.Round(origW * ratio);
        int targetH = (int)MathF.Round(origH * ratio);
        // Round to nearest multiple of stride; floor of zero clamps to one stride.
        int snappedW = Math.Max(StridePx, (int)MathF.Round(targetW / (float)StridePx) * StridePx);
        int snappedH = Math.Max(StridePx, (int)MathF.Round(targetH / (float)StridePx) * StridePx);
        return (snappedW, snappedH);
    }

    /// <summary>
    /// BFS-based connected-components labeling over a thresholded
    /// probability map, with per-component bbox + mean score. Each
    /// surviving component is unclipped and mapped back to original
    /// image coordinates.
    /// </summary>
    private static List<TextRegion> FindRegions(
        ReadOnlySpan<float> probMap,
        int width, int height,
        float pixelThreshold,
        float boxScoreThreshold,
        int minSize,
        float unclipRatio,
        float scaleBackX, float scaleBackY,
        int origW, int origH)
    {
        bool[] visited = new bool[width * height];
        List<TextRegion> regions = new();
        Queue<int> queue = new();

        for (int seed = 0; seed < probMap.Length; seed++)
        {
            if (visited[seed]) continue;
            if (probMap[seed] < pixelThreshold)
            {
                visited[seed] = true;
                continue;
            }

            int minX = int.MaxValue, maxX = int.MinValue;
            int minY = int.MaxValue, maxY = int.MinValue;
            float probSum = 0f;
            int count = 0;

            queue.Clear();
            queue.Enqueue(seed);
            visited[seed] = true;

            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                int px = idx % width;
                int py = idx / width;

                if (px < minX) minX = px;
                if (px > maxX) maxX = px;
                if (py < minY) minY = py;
                if (py > maxY) maxY = py;
                probSum += probMap[idx];
                count++;

                // 4-connectivity
                if (px > 0)
                {
                    int n = idx - 1;
                    if (!visited[n] && probMap[n] >= pixelThreshold)
                    { visited[n] = true; queue.Enqueue(n); }
                    else { visited[n] = true; }
                }
                if (px + 1 < width)
                {
                    int n = idx + 1;
                    if (!visited[n] && probMap[n] >= pixelThreshold)
                    { visited[n] = true; queue.Enqueue(n); }
                    else { visited[n] = true; }
                }
                if (py > 0)
                {
                    int n = idx - width;
                    if (!visited[n] && probMap[n] >= pixelThreshold)
                    { visited[n] = true; queue.Enqueue(n); }
                    else { visited[n] = true; }
                }
                if (py + 1 < height)
                {
                    int n = idx + width;
                    if (!visited[n] && probMap[n] >= pixelThreshold)
                    { visited[n] = true; queue.Enqueue(n); }
                    else { visited[n] = true; }
                }
            }

            int boxW = maxX - minX + 1;
            int boxH = maxY - minY + 1;
            if (boxW < minSize || boxH < minSize) continue;
            if (count == 0) continue;

            float meanScore = probSum / count;
            if (meanScore < boxScoreThreshold) continue;

            // DBNet polygon-offset unclip, axis-aligned form.
            // distance = area × unclipRatio / perimeter
            // expand each side by `distance`.
            float area = boxW * (float)boxH;
            float perimeter = 2f * (boxW + boxH);
            float distance = perimeter > 0 ? (area * unclipRatio) / perimeter : 0f;

            float ex1 = minX - distance;
            float ey1 = minY - distance;
            float ex2 = maxX + 1 + distance;
            float ey2 = maxY + 1 + distance;

            // Map resized-pixel coords back to original-image space, then
            // clip to image bounds.
            float ox1 = MathF.Max(0, ex1 * scaleBackX);
            float oy1 = MathF.Max(0, ey1 * scaleBackY);
            float ox2 = MathF.Min(origW, ex2 * scaleBackX);
            float oy2 = MathF.Min(origH, ey2 * scaleBackY);

            float ow = ox2 - ox1;
            float oh = oy2 - oy1;
            if (ow <= 0 || oh <= 0) continue;

            regions.Add(new TextRegion(meanScore, ox1, oy1, ow, oh));
        }

        // Sort top-to-bottom, then left-to-right — natural reading order
        // for receipt-style documents. Consumers can re-sort if needed.
        regions.Sort((a, b) =>
        {
            int cmp = a.Y.CompareTo(b.Y);
            return cmp != 0 ? cmp : a.X.CompareTo(b.X);
        });

        return regions;
    }

    private static ValueRef BuildDetectionArray(List<TextRegion> regions)
    {
        if (regions.Count == 0)
        {
            return ValueRef.FromArray(DataKind.Struct, []);
        }

        ValueRef[] elements = new ValueRef[regions.Count];
        for (int i = 0; i < regions.Count; i++)
        {
            TextRegion r = regions[i];
            elements[i] = ValueRef.FromStruct(
            [
                TextLabel,
                ValueRef.FromFloat32(r.Score),
                ValueRef.FromFloat32(r.X),
                ValueRef.FromFloat32(r.Y),
                ValueRef.FromFloat32(r.W),
                ValueRef.FromFloat32(r.H),
            ]);
        }
        return ValueRef.FromArray(DataKind.Struct, elements);
    }

    private readonly record struct TextRegion(float Score, float X, float Y, float W, float H);
}
