using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions.Scalar.Activation;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

using SkiaSharp;

namespace Heliosoph.DatumV.Functions.Scalar.Image;

/// <summary>
/// RT-DETR post-processing: sigmoid + per-query argmax + confidence filter +
/// box denormalization. Mirrors HuggingFace transformers'
/// <c>RTDetrImageProcessor.post_process_object_detection</c>. No NMS —
/// RT-DETR's set-prediction loss (Hungarian matching) trains the queries
/// to be non-overlapping, so the standard postprocess only needs the
/// confidence filter.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Signature.</strong>
/// <c>rtdetr_postprocess(logits FLOAT32[], boxes FLOAT32[], labels STRING[],
/// img Image, conf_thresh FLOAT32) → Array&lt;LabeledDetection&gt;</c>.
/// <c>logits</c> is the model's raw class-logits output flattened from
/// shape <c>[1, num_queries, num_classes]</c>; <c>boxes</c> is the
/// per-query bounding-box prediction flattened from
/// <c>[1, num_queries, 4]</c> in normalized <c>[cx, cy, w, h]</c> format.
/// <c>labels</c>'s length determines <c>num_classes</c>;
/// <c>num_queries</c> is derived as <c>logits.Length / num_classes</c>.
/// </para>
/// <para>
/// <strong>Algorithm.</strong>
/// <list type="number">
///   <item><strong>Sigmoid + argmax per query.</strong> Per-class
///         probabilities are independent (focal-loss training); for each
///         query take <c>(best_score, best_class) = argmax sigmoid(logits[q, :])</c>.</item>
///   <item><strong>Confidence filter.</strong> Drop any query with
///         <c>best_score &lt; conf_thresh</c>.</item>
///   <item><strong>Denormalize boxes.</strong> RT-DETR predicts boxes in
///         <c>[cx, cy, w, h]</c> format normalized to <c>[0, 1]</c> against
///         the original image dimensions. Multiply by <c>(img.W, img.H)</c>
///         and convert to <c>[x, y, w, h]</c> with <c>(x, y)</c> as
///         top-left.</item>
/// </list>
/// </para>
/// <para>
/// <strong>Output struct.</strong> <c>LabeledDetection</c> from
/// <see cref="NamedTypeRegistry"/>:
/// <c>Struct&lt;bbox: BoundingBox, label: String, score: Float32&gt;</c>.
/// Coordinates are in original-image pixel space; <c>(bbox.x, bbox.y)</c>
/// is the top-left corner.
/// </para>
/// </remarks>
public sealed class RtdetrPostprocessFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "rtdetr_postprocess";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "RT-DETR object-detection post-processing: sigmoid + per-query argmax + "
        + "confidence filter + box denormalization. No NMS (RT-DETR is set-prediction). "
        + "rtdetr_postprocess(logits FLOAT32[], boxes FLOAT32[], labels STRING[], img Image, "
        + "conf_thresh FLOAT32) → Array<LabeledDetection>. Boxes are returned in "
        + "original-image pixel coordinates.";

    /// <summary>Output named type — surfaces fields as bbox + label + score.</summary>
    public const string ResultNamedType = "LabeledDetection";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("logits", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array,
                    Metadata: new ParameterMetadata(
                        Description: "Flat RT-DETR class-logits tensor of shape [num_queries × num_classes].")),
                new ParameterSpec("boxes",  DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array,
                    Metadata: new ParameterMetadata(
                        Description: "Flat RT-DETR box tensor of shape [num_queries × 4] in normalized [cx, cy, w, h].")),
                new ParameterSpec("labels", DataKindMatcher.Exact(DataKind.String),  IsArray: ArrayMatch.Array,
                    Metadata: new ParameterMetadata(
                        Description: "Class label vocabulary indexed by class id. Length determines num_classes.")),
                new ParameterSpec("img",    DataKindMatcher.Exact(DataKind.Image),
                    Metadata: new ParameterMetadata(
                        Description: "Original input image. Used to denormalize box coordinates back to pixel space.")),
                new ParameterSpec("conf_thresh", DataKindMatcher.Exact(DataKind.Float32),
                    Metadata: new ParameterMetadata(
                        Check: new BetweenCheck(0.0m, 1.0m),
                        Step: 0.05m,
                        Description: "Per-query max-class-probability floor. Detections below this are dropped.")),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Struct))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<RtdetrPostprocessFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        // Null logits / boxes / img → no detections. Mirrors yolox_postprocess
        // for consistency with the rest of the detector family.
        if (args[0].IsNull || args[1].IsNull || args[3].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.FromArray(DataKind.Struct, []));
        }

        float[] logits = ActivationOps.ReadFloat32Array(args[0]);
        float[] boxes  = ActivationOps.ReadFloat32Array(args[1]);
        string[] labels = ReadLabels(args[2]);
        SKBitmap img = args[3].AsImage();
        float confThresh = args[4].AsFloat32();

        if (labels.Length == 0)
        {
            throw new FunctionArgumentException("rtdetr_postprocess",
                "labels list must not be empty — needed to determine num_classes.");
        }

        int numClasses = labels.Length;
        if (logits.Length % numClasses != 0)
        {
            throw new FunctionArgumentException("rtdetr_postprocess",
                $"logits length {logits.Length} is not divisible by labels.Length ({numClasses}); "
                + "expected [num_queries × num_classes] flattened.");
        }
        int numQueries = logits.Length / numClasses;

        if (boxes.Length != numQueries * 4)
        {
            throw new FunctionArgumentException("rtdetr_postprocess",
                $"boxes length {boxes.Length} doesn't match num_queries={numQueries}: "
                + $"expected {numQueries * 4} floats (4 per query).");
        }

        if (img.Width <= 0 || img.Height <= 0)
        {
            throw new FunctionArgumentException("rtdetr_postprocess",
                $"degenerate image dimensions: {img.Width}×{img.Height}.");
        }

        List<Detection> detections = DecodeDetections(
            logits, boxes, numQueries, numClasses, img.Width, img.Height, confThresh);
        return new ValueTask<ValueRef>(BuildDetectionArray(detections, labels, frame.Types));
    }

    /// <summary>
    /// Walks every query, applies sigmoid + argmax, filters by confidence,
    /// and denormalizes the surviving boxes into original-image pixel space.
    /// </summary>
    private static List<Detection> DecodeDetections(
        ReadOnlySpan<float> logits,
        ReadOnlySpan<float> boxes,
        int numQueries,
        int numClasses,
        int imgWidth,
        int imgHeight,
        float confThresh)
    {
        List<Detection> kept = new();
        for (int q = 0; q < numQueries; q++)
        {
            int logitBase = q * numClasses;
            int boxBase = q * 4;

            float bestProb = -1f;
            int bestClass = -1;
            for (int c = 0; c < numClasses; c++)
            {
                // Sigmoid(x) = 1 / (1 + exp(-x)). Numerically stable for the
                // moderate-magnitude logits RT-DETR emits; no need for the
                // log-space gymnastics multilabel_classify uses.
                float p = 1f / (1f + MathF.Exp(-logits[logitBase + c]));
                if (p > bestProb)
                {
                    bestProb = p;
                    bestClass = c;
                }
            }

            if (bestProb < confThresh || bestClass < 0) continue;

            // [cx, cy, w, h] normalized → [x, y, w, h] in pixel space.
            float cx = boxes[boxBase] * imgWidth;
            float cy = boxes[boxBase + 1] * imgHeight;
            float bw = boxes[boxBase + 2] * imgWidth;
            float bh = boxes[boxBase + 3] * imgHeight;

            float x = cx - bw * 0.5f;
            float y = cy - bh * 0.5f;

            kept.Add(new Detection(bestClass, bestProb, x, y, bw, bh));
        }
        return kept;
    }

    /// <summary>
    /// Builds the <c>Array&lt;LabeledDetection&gt;</c> output value. Stamps
    /// the nested <c>BoundingBox</c> and outer <c>LabeledDetection</c>
    /// TypeIds from the per-query registry — same shape as the
    /// <see cref="YoloxPostprocessFunction"/> output so consumers don't have
    /// to discriminate by detector family.
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

    private static string[] ReadLabels(ValueRef arg)
    {
        if (arg.IsNull)
        {
            throw new FunctionArgumentException("rtdetr_postprocess",
                "labels argument must not be null.");
        }
        if (arg.Materialized is string[] direct) return direct;
        ReadOnlySpan<ValueRef> elements = arg.GetArrayElements();
        string[] copy = new string[elements.Length];
        for (int i = 0; i < elements.Length; i++) copy[i] = elements[i].AsString();
        return copy;
    }

    private readonly record struct Detection(int ClassId, float Score, float X, float Y, float W, float H);
}
