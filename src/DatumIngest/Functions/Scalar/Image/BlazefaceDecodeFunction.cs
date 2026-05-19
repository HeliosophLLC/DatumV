using System.Collections.Concurrent;

using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions.Scalar.Activation;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

using SkiaSharp;

namespace Heliosoph.DatumV.Functions.Scalar.Image;

/// <summary>
/// MediaPipe BlazeFace post-processing: decode raw two-layer SSD anchor
/// outputs, NMS, and scale to source-image pixel coordinates. Returns one
/// <c>FaceDetection</c> per accepted face — bounding box plus the 6
/// BlazeFace keypoints (right eye, left eye, nose tip, mouth, right ear
/// tragion, left ear tragion) plus confidence.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Signature.</strong>
/// <c>blazeface_decode(box_coords_1 FLOAT32[], box_coords_2 FLOAT32[],
/// box_scores_1 FLOAT32[], box_scores_2 FLOAT32[], img Image,
/// input_size INT, conf_thresh FLOAT32, iou_thresh FLOAT32) →
/// Array&lt;FaceDetection&gt;</c>. The four tensor arguments correspond to
/// the four declared ONNX outputs (two anchor scales × box / score split);
/// they're separate so the SQL body can extract them by name from
/// <c>infer_outputs</c>.
/// </para>
/// <para>
/// <strong>Anchor pyramid (input_size = 256).</strong> Mirrors MediaPipe's
/// <c>SsdAnchorsCalculator</c> output for the canonical short-range face
/// detector config (<c>min_scale=0.148</c>, <c>max_scale=0.75</c>,
/// <c>fixed_anchor_size=true</c>, <c>anchor_offset=(0.5, 0.5)</c>):
/// <list type="bullet">
///   <item>Layer 1 (stride 16): 16×16 grid × 2 anchors per cell = 512 anchors.</item>
///   <item>Layer 2 (stride 32): 8×8 grid × 6 anchors per cell = 384 anchors.</item>
///   <item>Total: 896 anchors, ordering matches the network's output flatten.</item>
/// </list>
/// With <c>fixed_anchor_size=true</c>, every anchor has width=height=1.0 in
/// normalized coords — the network output emits absolute box sizes (in
/// input-size pixels), not scales relative to a per-anchor prior.
/// </para>
/// <para>
/// <strong>Decode (per anchor).</strong>
/// <list type="number">
///   <item><c>conf = sigmoid(raw_score)</c>; skip if &lt; <c>conf_thresh</c>.</item>
///   <item><c>cx_n = raw[0]/input_size + anchor_cx</c>,
///         <c>cy_n = raw[1]/input_size + anchor_cy</c>
///         (anchor centers are already normalized; output offsets are in
///         input-size pixels).</item>
///   <item><c>w_n = raw[2]/input_size</c>, <c>h_n = raw[3]/input_size</c>.</item>
///   <item>6 keypoints at <c>raw[4+2k], raw[5+2k]</c> for <c>k ∈ 0..5</c>,
///         decoded the same way as the center.</item>
/// </list>
/// </para>
/// <para>
/// <strong>NMS.</strong> Class-less (face is the only class). Sort
/// candidates descending by score; for each kept box, suppress any later
/// candidate with IoU above <c>iou_thresh</c>. Survivors map back to
/// source-image pixel coords by multiplying normalized values by the
/// source bitmap's <c>(width, height)</c> — assumes the producer used
/// <c>image_to_tensor_chw</c>'s stretch resize (not a letterbox).
/// </para>
/// </remarks>
public sealed class BlazefaceDecodeFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "blazeface_decode";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "MediaPipe BlazeFace post-processing: concat two-layer anchor outputs, sigmoid scores, "
        + "decode boxes + 6 keypoints, class-less NMS, scale to source coords. "
        + "blazeface_decode(box_coords_1, box_coords_2, box_scores_1, box_scores_2, img, "
        + "input_size, conf_thresh, iou_thresh) → Array<FaceDetection>. "
        + "Pass the four arrays straight from infer_outputs(); input_size = 256 for the "
        + "canonical MediaPipe short-range face detector.";

    /// <summary>Output named type — Struct&lt;bbox: BoundingBox, landmarks: Array&lt;Point2D&gt;, score: Float32&gt;.</summary>
    public const string ResultNamedType = "FaceDetection";

    private const int KeypointsPerDetection = 6;
    private const int ValuesPerAnchor = 4 + 2 * KeypointsPerDetection; // 16

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("box_coords_1", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array,
                    Metadata: new ParameterMetadata(
                        Description: "Layer-1 box+keypoint outputs, flat [anchors_1 × 16].")),
                new ParameterSpec("box_coords_2", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array,
                    Metadata: new ParameterMetadata(
                        Description: "Layer-2 box+keypoint outputs, flat [anchors_2 × 16].")),
                new ParameterSpec("box_scores_1", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array,
                    Metadata: new ParameterMetadata(
                        Description: "Layer-1 raw (pre-sigmoid) score logits, flat [anchors_1].")),
                new ParameterSpec("box_scores_2", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array,
                    Metadata: new ParameterMetadata(
                        Description: "Layer-2 raw (pre-sigmoid) score logits, flat [anchors_2].")),
                new ParameterSpec("img", DataKindMatcher.Exact(DataKind.Image),
                    Metadata: new ParameterMetadata(
                        Description: "Original input image. Used to scale normalized coords back to pixel space.")),
                new ParameterSpec("input_size", DataKindMatcher.Family(DataKindFamily.IntegerFamily),
                    Metadata: new ParameterMetadata(
                        Check: new InCheck(["128", "192", "256"]),
                        Unit: "pixels",
                        Description: "Square ONNX input dimension. 256 for the canonical short-range face detector.")),
                new ParameterSpec("conf_thresh", DataKindMatcher.Exact(DataKind.Float32),
                    Metadata: new ParameterMetadata(
                        Check: new BetweenCheck(0.0m, 1.0m),
                        Step: 0.05m,
                        Description: "Sigmoid-confidence threshold. Drops detections below this value pre-NMS.")),
                new ParameterSpec("iou_thresh", DataKindMatcher.Exact(DataKind.Float32),
                    Metadata: new ParameterMetadata(
                        Check: new BetweenCheck(0.0m, 1.0m),
                        Step: 0.05m,
                        Description: "IoU overlap threshold for class-less NMS. Higher = keep more overlapping faces.")),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Struct))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<BlazefaceDecodeFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull || args[3].IsNull || args[4].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.FromArray(DataKind.Struct, []));
        }

        float[] boxCoords1 = ActivationOps.ReadFloat32Array(args[0]);
        float[] boxCoords2 = ActivationOps.ReadFloat32Array(args[1]);
        float[] boxScores1 = ActivationOps.ReadFloat32Array(args[2]);
        float[] boxScores2 = ActivationOps.ReadFloat32Array(args[3]);
        SKBitmap img = args[4].AsImage();
        int inputSize = ReadIntArg(args[5], "input_size");
        float confThresh = args[6].AsFloat32();
        float iouThresh = args[7].AsFloat32();

        BlazefaceAnchorGrid grid = BlazefaceAnchorGrid.For(inputSize);

        // Shape sanity-check — wrong tensor sizes here usually mean a different
        // BlazeFace variant got pointed at this scalar (e.g. the full-range
        // detector with different anchor counts). Caught here cleanly instead
        // of buffer-overrunning the decode loop below.
        int expectedCoords1 = grid.Layer1AnchorCount * ValuesPerAnchor;
        int expectedCoords2 = grid.Layer2AnchorCount * ValuesPerAnchor;
        int expectedScores1 = grid.Layer1AnchorCount;
        int expectedScores2 = grid.Layer2AnchorCount;
        if (boxCoords1.Length != expectedCoords1)
        {
            throw new FunctionArgumentException(Name,
                $"box_coords_1 length {boxCoords1.Length} doesn't match input_size {inputSize}: "
                + $"expected {expectedCoords1} ({grid.Layer1AnchorCount} anchors × {ValuesPerAnchor}).");
        }
        if (boxCoords2.Length != expectedCoords2)
        {
            throw new FunctionArgumentException(Name,
                $"box_coords_2 length {boxCoords2.Length} doesn't match input_size {inputSize}: "
                + $"expected {expectedCoords2} ({grid.Layer2AnchorCount} anchors × {ValuesPerAnchor}).");
        }
        if (boxScores1.Length != expectedScores1)
        {
            throw new FunctionArgumentException(Name,
                $"box_scores_1 length {boxScores1.Length} doesn't match input_size {inputSize}: "
                + $"expected {expectedScores1}.");
        }
        if (boxScores2.Length != expectedScores2)
        {
            throw new FunctionArgumentException(Name,
                $"box_scores_2 length {boxScores2.Length} doesn't match input_size {inputSize}: "
                + $"expected {expectedScores2}.");
        }

        int srcW = img.Width;
        int srcH = img.Height;
        if (srcW <= 0 || srcH <= 0)
        {
            throw new FunctionArgumentException(Name,
                $"degenerate image dimensions: {srcW}×{srcH}.");
        }

        float inputSizeF = inputSize;

        List<FaceCandidate> candidates = new();

        // Decode layer 1 (e.g. 512 anchors at stride 16).
        DecodeLayer(
            boxCoords1, boxScores1, grid.Layer1AnchorsCx, grid.Layer1AnchorsCy,
            inputSizeF, srcW, srcH, confThresh, candidates);

        // Decode layer 2 (e.g. 384 anchors at stride 32).
        DecodeLayer(
            boxCoords2, boxScores2, grid.Layer2AnchorsCx, grid.Layer2AnchorsCy,
            inputSizeF, srcW, srcH, confThresh, candidates);

        List<FaceCandidate> kept = ApplyNms(candidates, iouThresh);
        return new ValueTask<ValueRef>(BuildFaceArray(kept, frame.Types));
    }

    /// <summary>
    /// Decodes one anchor layer's outputs into per-detection bbox + keypoints
    /// in source-image pixel coordinates. Skips anchors whose sigmoid score is
    /// below the confidence threshold.
    /// </summary>
    private static void DecodeLayer(
        ReadOnlySpan<float> boxCoords,
        ReadOnlySpan<float> boxScores,
        float[] anchorsCx,
        float[] anchorsCy,
        float inputSize,
        int srcW,
        int srcH,
        float confThresh,
        List<FaceCandidate> candidates)
    {
        int anchorCount = anchorsCx.Length;
        for (int a = 0; a < anchorCount; a++)
        {
            float score = Sigmoid(boxScores[a]);
            if (score < confThresh) continue;

            int b = a * ValuesPerAnchor;
            float ax = anchorsCx[a];
            float ay = anchorsCy[a];

            // Normalized [0, 1] center + size. The network emits offsets in
            // input-size pixels; divide by input_size to land in normalized
            // anchor-grid space, then add the anchor center.
            float cxN = boxCoords[b]     / inputSize + ax;
            float cyN = boxCoords[b + 1] / inputSize + ay;
            float wN  = boxCoords[b + 2] / inputSize;
            float hN  = boxCoords[b + 3] / inputSize;

            // Top-left + size in source-image pixels. image_to_tensor_chw is
            // a stretch resize, so the normalized → pixel mapping is just
            // a per-axis multiply.
            float x = (cxN - wN * 0.5f) * srcW;
            float y = (cyN - hN * 0.5f) * srcH;
            float w = wN * srcW;
            float h = hN * srcH;

            // Six BlazeFace keypoints, same decode as the center.
            float[] kpX = new float[KeypointsPerDetection];
            float[] kpY = new float[KeypointsPerDetection];
            for (int k = 0; k < KeypointsPerDetection; k++)
            {
                float kxN = boxCoords[b + 4 + k * 2]     / inputSize + ax;
                float kyN = boxCoords[b + 4 + k * 2 + 1] / inputSize + ay;
                kpX[k] = kxN * srcW;
                kpY[k] = kyN * srcH;
            }

            candidates.Add(new FaceCandidate(score, x, y, w, h, kpX, kpY));
        }
    }

    /// <summary>
    /// Class-less NMS over face candidates. Sort by score descending; for
    /// each kept candidate, suppress every later candidate with IoU above
    /// <paramref name="iouThreshold"/>.
    /// </summary>
    private static List<FaceCandidate> ApplyNms(List<FaceCandidate> candidates, float iouThreshold)
    {
        if (candidates.Count == 0) return candidates;
        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        bool[] suppressed = new bool[candidates.Count];
        List<FaceCandidate> kept = new();
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

    private static float Iou(in FaceCandidate a, in FaceCandidate b)
    {
        float ax2 = a.X + a.W, ay2 = a.Y + a.H;
        float bx2 = b.X + b.W, by2 = b.Y + b.H;
        float ix1 = MathF.Max(a.X, b.X);
        float iy1 = MathF.Max(a.Y, b.Y);
        float ix2 = MathF.Min(ax2, bx2);
        float iy2 = MathF.Min(ay2, by2);
        float iw = MathF.Max(0, ix2 - ix1);
        float ih = MathF.Max(0, iy2 - iy1);
        float intersection = iw * ih;
        float union = a.W * a.H + b.W * b.H - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    private static float Sigmoid(float x) => 1f / (1f + MathF.Exp(-x));

    /// <summary>
    /// Builds the <c>Array&lt;FaceDetection&gt;</c> output. Stamps the
    /// nested <c>BoundingBox</c> and outer <c>FaceDetection</c> TypeIds
    /// from the per-query registry so downstream consumers resolve fields
    /// (<c>det.bbox.x</c>, <c>det.landmarks[0]</c>, <c>det.score</c>)
    /// through the catalog's named-type vocabulary.
    /// </summary>
    private static ValueRef BuildFaceArray(List<FaceCandidate> faces, TypeRegistry? types)
    {
        if (faces.Count == 0)
        {
            return ValueRef.FromArray(DataKind.Struct, Array.Empty<ValueRef>());
        }

        ushort faceDetectionTypeId = 0;
        ushort boundingBoxTypeId = 0;
        if (types is not null)
        {
            faceDetectionTypeId = (ushort)types.GetTypeIdByName(ResultNamedType);
            boundingBoxTypeId = (ushort)types.GetTypeIdByName("BoundingBox");
        }

        ValueRef[] elements = new ValueRef[faces.Count];
        for (int i = 0; i < faces.Count; i++)
        {
            FaceCandidate f = faces[i];

            ValueRef[] bboxFields =
            [
                ValueRef.FromFloat32(f.X),
                ValueRef.FromFloat32(f.Y),
                ValueRef.FromFloat32(f.W),
                ValueRef.FromFloat32(f.H),
            ];
            ValueRef bbox = boundingBoxTypeId == 0
                ? ValueRef.FromStruct(bboxFields)
                : ValueRef.FromStruct(bboxFields, boundingBoxTypeId);

            ValueRef[] kpElements = new ValueRef[KeypointsPerDetection];
            for (int k = 0; k < KeypointsPerDetection; k++)
            {
                kpElements[k] = ValueRef.FromPoint2D(f.KpX[k], f.KpY[k]);
            }
            ValueRef landmarks = ValueRef.FromArray(DataKind.Point2D, kpElements);

            ValueRef[] outerFields =
            [
                bbox,
                ValueRef.FromString("face"),    // label — fixed at "face" since this is a face-only detector
                landmarks,
                ValueRef.FromFloat32(f.Score),
            ];
            elements[i] = faceDetectionTypeId == 0
                ? ValueRef.FromStruct(outerFields)
                : ValueRef.FromStruct(outerFields, faceDetectionTypeId);
        }
        return ValueRef.FromArray(DataKind.Struct, elements);
    }

    private static int ReadIntArg(ValueRef arg, string name)
    {
        int value = arg.Kind switch
        {
            DataKind.Int8 => arg.AsInt8(),
            DataKind.Int16 => arg.AsInt16(),
            DataKind.Int32 => arg.AsInt32(),
            DataKind.Int64 => checked((int)arg.AsInt64()),
            _ => throw new FunctionArgumentException(Name,
                $"{name} must be an integer kind, got {arg.Kind}."),
        };
        if (value <= 0)
        {
            throw new FunctionArgumentException(Name, $"{name} must be > 0, got {value}.");
        }
        return value;
    }

    /// <summary>Per-detection record carried through decode + NMS + array build.</summary>
    private readonly record struct FaceCandidate(
        float Score, float X, float Y, float W, float H,
        float[] KpX, float[] KpY);

    /// <summary>
    /// Pre-computed BlazeFace anchor centers for a given square input size.
    /// Mirrors MediaPipe's <c>SsdAnchorsCalculator</c> with the canonical
    /// short-range face config: 2 layers (strides 16 + 32), 2 + 6 anchors
    /// per cell respectively, <c>fixed_anchor_size=true</c> so all anchors
    /// at a layer share the same (cx, cy) — the only thing distinguishing
    /// anchors at one cell is the network's output scale.
    /// </summary>
    private sealed class BlazefaceAnchorGrid
    {
        public int Layer1AnchorCount { get; }
        public int Layer2AnchorCount { get; }
        public float[] Layer1AnchorsCx { get; }
        public float[] Layer1AnchorsCy { get; }
        public float[] Layer2AnchorsCx { get; }
        public float[] Layer2AnchorsCy { get; }

        private BlazefaceAnchorGrid(
            float[] l1Cx, float[] l1Cy, float[] l2Cx, float[] l2Cy)
        {
            Layer1AnchorsCx = l1Cx;
            Layer1AnchorsCy = l1Cy;
            Layer2AnchorsCx = l2Cx;
            Layer2AnchorsCy = l2Cy;
            Layer1AnchorCount = l1Cx.Length;
            Layer2AnchorCount = l2Cx.Length;
        }

        private static readonly ConcurrentDictionary<int, BlazefaceAnchorGrid> Cache = new();

        public static BlazefaceAnchorGrid For(int inputSize) => Cache.GetOrAdd(inputSize, Build);

        /// <summary>
        /// Builds the anchor grid. Layer 1 uses stride 16 with 2 anchors per
        /// cell; layer 2 uses stride 32 with 6 anchors per cell. Anchor
        /// centers are normalized to <c>[0, 1]</c> of the input dimensions
        /// — that's the coordinate space the network's box offsets land in
        /// after dividing the raw outputs by <c>input_size</c>.
        /// </summary>
        private static BlazefaceAnchorGrid Build(int inputSize)
        {
            (int stride1, int anchorsPerCell1, int stride2, int anchorsPerCell2) =
                inputSize switch
                {
                    // The 256-input short-range face detector: 16×16×2 + 8×8×6.
                    256 => (16, 2, 32, 6),
                    // 128-input full-range variant: 16×16×2 + 8×8×6 too (same
                    // grid, different network depth). Same anchor placements
                    // since the strides scale with input.
                    128 => (16, 2, 32, 6),
                    // 192-input variants: same anchor topology, different stride math.
                    192 => (16, 2, 32, 6),
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(inputSize), inputSize,
                        "BlazeFace decode only supports 128 / 192 / 256 input sizes; " +
                        "extend the BlazefaceAnchorGrid.Build switch to add another."),
                };

            float[] l1Cx, l1Cy;
            BuildLayer(inputSize, stride1, anchorsPerCell1, out l1Cx, out l1Cy);
            float[] l2Cx, l2Cy;
            BuildLayer(inputSize, stride2, anchorsPerCell2, out l2Cx, out l2Cy);
            return new BlazefaceAnchorGrid(l1Cx, l1Cy, l2Cx, l2Cy);
        }

        private static void BuildLayer(
            int inputSize, int stride, int anchorsPerCell,
            out float[] cx, out float[] cy)
        {
            int cellsPerSide = inputSize / stride;
            int totalAnchors = cellsPerSide * cellsPerSide * anchorsPerCell;
            cx = new float[totalAnchors];
            cy = new float[totalAnchors];
            int cursor = 0;
            for (int yi = 0; yi < cellsPerSide; yi++)
            {
                for (int xi = 0; xi < cellsPerSide; xi++)
                {
                    float ccx = (xi + 0.5f) / cellsPerSide;
                    float ccy = (yi + 0.5f) / cellsPerSide;
                    for (int k = 0; k < anchorsPerCell; k++)
                    {
                        cx[cursor] = ccx;
                        cy[cursor] = ccy;
                        cursor++;
                    }
                }
            }
        }
    }
}
