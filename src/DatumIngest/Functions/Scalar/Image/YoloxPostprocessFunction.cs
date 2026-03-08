using System.Collections.Concurrent;

using DatumIngest.Execution;
using DatumIngest.Functions.Scalar.Activation;
using DatumIngest.Manifest;
using DatumIngest.Model;

using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Image;

/// <summary>
/// YOLOX post-processing: decode raw <c>[anchors × 85]</c> output, class-aware
/// NMS, and reverse the letterbox scale. Returns one
/// <c>LabeledDetection</c> per accepted detection in original-image pixel
/// coordinates.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Signature.</strong>
/// <c>yolox_postprocess(raw FLOAT32[], labels STRING[], img Image,
/// input_size INT, conf_thresh FLOAT32, iou_thresh FLOAT32) →
/// Array&lt;LabeledDetection&gt;</c>. <c>labels</c> is the class vocabulary
/// (80 strings for COCO) indexed by predicted class id. <c>input_size</c>
/// is the model's square input dimension (416 for nano/tiny, 640 for s/m/l/x).
/// </para>
/// <para>
/// <strong>Algorithm.</strong> Mirrors Megvii's reference postprocess
/// (their <c>onnx_inference.py</c> demo) since the published ONNX exports
/// omit the bbox decoder:
/// <list type="number">
///   <item><strong>Decode bboxes per anchor.</strong> Megvii's exports
///         arrive with raw grid-relative bbox values:
///         <c>cx = (raw + grid) × stride</c>,
///         <c>w = exp(raw) × stride</c>. Three strides 8/16/32 stack
///         row-major along the anchor dimension.</item>
///   <item><strong>Confidence filter.</strong>
///         <c>conf = objectness × max(class_scores)</c>. Both are sigmoid-
///         activated by the head regardless of the export flag, so they're
///         already in [0, 1].</item>
///   <item><strong>Class-aware NMS.</strong> Sort by score descending; for
///         each kept box, suppress any later same-class candidate with IoU
///         above <c>iou_thresh</c>.</item>
///   <item><strong>Reverse letterbox.</strong> Divide bbox coordinates by
///         <c>ratio = min(input_size / img.W, input_size / img.H)</c>; the
///         original image lands in the top-left of the padded square so no
///         offset to subtract.</item>
/// </list>
/// </para>
/// <para>
/// <strong>Output struct.</strong> <c>LabeledDetection</c> from
/// <see cref="NamedTypeRegistry"/>:
/// <c>Struct&lt;bbox: BoundingBox, label: String, score: Float32&gt;</c>.
/// Coordinates are in original-image pixel space; <c>(bbox.x, bbox.y)</c>
/// is the top-left corner, <c>(bbox.w, bbox.h)</c> the width and height.
/// </para>
/// </remarks>
public sealed class YoloxPostprocessFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "yolox_postprocess";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "YOLOX object-detection post-processing: decoder + class-aware NMS + reverse letterbox. "
        + "yolox_postprocess(raw FLOAT32[], labels STRING[], img Image, input_size INT, "
        + "conf_thresh FLOAT32, iou_thresh FLOAT32) → Array<LabeledDetection>. "
        + "Returns one detection per accepted bbox with the label string looked up "
        + "via labels[class_id].";

    /// <summary>Output named type — surfaces fields as bbox + label + score.</summary>
    public const string ResultNamedType = "LabeledDetection";

    private const int NumClasses = 80;
    private const int ValuesPerPrediction = 4 + 1 + NumClasses; // 85

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("raw",         DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("labels",      DataKindMatcher.Exact(DataKind.String),  IsArray: ArrayMatch.Array),
                new ParameterSpec("img",         DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("input_size",  DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("conf_thresh", DataKindMatcher.Exact(DataKind.Float32)),
                new ParameterSpec("iou_thresh",  DataKindMatcher.Exact(DataKind.Float32)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Struct))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<YoloxPostprocessFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[2].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.FromArray(DataKind.Struct, []));
        }

        float[] raw = ActivationOps.ReadFloat32Array(args[0]);
        string[] labels = ReadLabels(args[1]);
        SKBitmap img = args[2].AsImage();
        int inputSize = ReadIntArg(args[3], "input_size");
        float confThresh = args[4].AsFloat32();
        float iouThresh = args[5].AsFloat32();

        // Sanity-check the raw output shape against the input size's anchor count.
        AnchorGrid grid = AnchorGrid.For(inputSize);
        int expectedLength = grid.AnchorCount * ValuesPerPrediction;
        if (raw.Length != expectedLength)
        {
            throw new FunctionArgumentException("yolox_postprocess",
                $"raw tensor length {raw.Length} doesn't match input_size {inputSize}: "
                + $"expected {expectedLength} floats ({grid.AnchorCount} anchors × {ValuesPerPrediction}).");
        }

        // Compute the letterbox ratio used by yolox_preprocess.
        float ratio = MathF.Min(
            (float)inputSize / img.Width,
            (float)inputSize / img.Height);
        if (ratio <= 0f)
        {
            throw new FunctionArgumentException("yolox_postprocess",
                $"degenerate image dimensions: {img.Width}×{img.Height}.");
        }

        List<Detection> detections = DecodeAndNms(raw, grid, ratio, confThresh, iouThresh);
        return new ValueTask<ValueRef>(BuildDetectionArray(detections, labels, frame.Types));
    }

    /// <summary>
    /// Decodes per-anchor predictions, applies the confidence threshold, and
    /// runs class-aware NMS. Returns the final detection list in original-image
    /// pixel coordinates.
    /// </summary>
    private static List<Detection> DecodeAndNms(
        ReadOnlySpan<float> raw,
        AnchorGrid grid,
        float ratio,
        float confThresh,
        float iouThresh)
    {
        List<Detection> candidates = new();
        for (int anchor = 0; anchor < grid.AnchorCount; anchor++)
        {
            int anchorBase = anchor * ValuesPerPrediction;
            float stride = grid.Strides[anchor];

            float cx = (raw[anchorBase]     + grid.GridX[anchor]) * stride;
            float cy = (raw[anchorBase + 1] + grid.GridY[anchor]) * stride;
            float bw = MathF.Exp(raw[anchorBase + 2]) * stride;
            float bh = MathF.Exp(raw[anchorBase + 3]) * stride;
            float objectness = raw[anchorBase + 4];

            float bestClassScore = 0f;
            int bestClass = -1;
            for (int c = 0; c < NumClasses; c++)
            {
                float s = raw[anchorBase + 5 + c];
                if (s > bestClassScore)
                {
                    bestClassScore = s;
                    bestClass = c;
                }
            }

            float conf = objectness * bestClassScore;
            if (conf < confThresh || bestClass < 0) continue;

            float x = (cx - bw * 0.5f) / ratio;
            float y = (cy - bh * 0.5f) / ratio;
            float w = bw / ratio;
            float h = bh / ratio;

            candidates.Add(new Detection(bestClass, conf, x, y, w, h));
        }
        return ApplyNms(candidates, iouThresh);
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

    /// <summary>
    /// Builds the <c>Array&lt;LabeledDetection&gt;</c> output value. Stamps
    /// the nested <c>BoundingBox</c> and outer <c>LabeledDetection</c>
    /// TypeIds from the per-query registry so downstream consumers resolve
    /// field names through <see cref="NamedTypeRegistry"/>'s vocabulary.
    /// </summary>
    private static ValueRef BuildDetectionArray(
        List<Detection> detections, string[] labels, TypeRegistry? types)
    {
        ushort labeledDetectionTypeId = 0;
        ushort boundingBoxTypeId = 0;
        if (types is not null)
        {
            labeledDetectionTypeId = (ushort)types.GetTypeIdByName(ResultNamedType);
            boundingBoxTypeId = (ushort)types.GetTypeIdByName("BoundingBox");
        }

        if (detections.Count == 0)
        {
            return ValueRef.FromArray(DataKind.Struct, Array.Empty<ValueRef>());
        }

        ValueRef[] elements = new ValueRef[detections.Count];
        for (int i = 0; i < detections.Count; i++)
        {
            Detection d = detections[i];

            ValueRef[] bboxFields =
            [
                ValueRef.FromFloat32(d.X),
                ValueRef.FromFloat32(d.Y),
                ValueRef.FromFloat32(d.W),
                ValueRef.FromFloat32(d.H),
            ];
            ValueRef bbox = boundingBoxTypeId == 0
                ? ValueRef.FromStruct(bboxFields)
                : ValueRef.FromStruct(bboxFields, boundingBoxTypeId);

            string label = d.ClassId < labels.Length
                ? labels[d.ClassId]
                : $"class_{d.ClassId}";

            ValueRef[] outerFields =
            [
                bbox,
                ValueRef.FromString(label),
                ValueRef.FromFloat32(d.Score),
            ];
            elements[i] = labeledDetectionTypeId == 0
                ? ValueRef.FromStruct(outerFields)
                : ValueRef.FromStruct(outerFields, labeledDetectionTypeId);
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
            _ => throw new FunctionArgumentException("yolox_postprocess",
                $"{name} must be an integer kind, got {arg.Kind}."),
        };
        if (value <= 0)
        {
            throw new FunctionArgumentException("yolox_postprocess",
                $"{name} must be > 0, got {value}.");
        }
        return value;
    }

    private static string[] ReadLabels(ValueRef arg)
    {
        if (arg.IsNull)
        {
            throw new FunctionArgumentException("yolox_postprocess",
                "labels argument is null. Pass an Array<String> of class labels "
                + "(e.g. via read_string_list('coco-classes.json')).");
        }
        ReadOnlySpan<ValueRef> elements = arg.GetArrayElements();
        string[] result = new string[elements.Length];
        for (int i = 0; i < elements.Length; i++)
        {
            result[i] = elements[i].IsNull ? string.Empty : elements[i].AsString();
        }
        return result;
    }

    private readonly record struct Detection(int ClassId, float Score, float X, float Y, float W, float H);

    /// <summary>
    /// Per-anchor grid coordinates + strides for a given square input size.
    /// Anchors stack row-major across the three FPN strides (8, 16, 32);
    /// 3549 anchors for 416 input, 8400 for 640.
    /// </summary>
    private sealed class AnchorGrid
    {
        public int AnchorCount { get; }
        public float[] GridX { get; }
        public float[] GridY { get; }
        public float[] Strides { get; }

        private AnchorGrid(int anchorCount, float[] gridX, float[] gridY, float[] strides)
        {
            AnchorCount = anchorCount;
            GridX = gridX;
            GridY = gridY;
            Strides = strides;
        }

        private static readonly ConcurrentDictionary<int, AnchorGrid> Cache = new();

        public static AnchorGrid For(int inputSize) => Cache.GetOrAdd(inputSize, Build);

        private static AnchorGrid Build(int inputSize)
        {
            int s8 = inputSize / 8, s16 = inputSize / 16, s32 = inputSize / 32;
            int count = s8 * s8 + s16 * s16 + s32 * s32;
            float[] gridX = new float[count];
            float[] gridY = new float[count];
            float[] strides = new float[count];
            int cursor = 0;
            foreach ((int side, int stride) in new[] { (s8, 8), (s16, 16), (s32, 32) })
            {
                for (int y = 0; y < side; y++)
                {
                    for (int x = 0; x < side; x++)
                    {
                        gridX[cursor] = x;
                        gridY[cursor] = y;
                        strides[cursor] = stride;
                        cursor++;
                    }
                }
            }
            return new AnchorGrid(count, gridX, gridY, strides);
        }
    }
}
