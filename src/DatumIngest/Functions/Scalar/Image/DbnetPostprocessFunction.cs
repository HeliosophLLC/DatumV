using DatumIngest.Execution;
using DatumIngest.Functions.Scalar.Activation;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Image;

/// <summary>
/// DBNet-family post-processing for text-detection probability maps:
/// threshold → 4-connectivity BFS → component-score filter → polygon-offset
/// unclip → scale back to original-image coordinates. Returns one struct
/// per accepted text region with the constant <c>label = "text"</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Input.</strong> A flat Float32 probability map of length
/// <c>h × w</c> in row-major order (the standard ONNX <c>[1, 1, H, W]</c>
/// output flattened). Pair with <see cref="ImageResizeToStrideFunction"/> +
/// <see cref="ImageToTensorChwFunction"/> + <c>infer()</c> to produce
/// the input from an image.
/// </para>
/// <para>
/// <strong>Algorithm.</strong> Identical to <c>PpOcrDetectionModel.FindRegions</c>:
/// <list type="number">
///   <item>Per-pixel threshold at <c>pixel_threshold</c> (PaddleOCR default 0.3).</item>
///   <item>BFS over surviving pixels with 4-connectivity; each connected
///         component yields a tight bounding box and mean probability.</item>
///   <item>Reject components whose mean probability is below
///         <c>box_score_threshold</c> (PaddleOCR default 0.6).</item>
///   <item>Reject components whose tight bbox is smaller than
///         <c>min_size</c> pixels on either side (default 3).</item>
///   <item>DBNet polygon-offset unclip in the axis-aligned case:
///         <c>distance = area × unclip_ratio / perimeter</c>, expand each side.</item>
///   <item>Multiply by <c>(scale_x, scale_y)</c> to map back to original-image
///         pixel space, then clip to the original-image bounds.</item>
/// </list>
/// </para>
/// <para>
/// <strong>Scale factors.</strong> <c>scale_x = origW / resizedW</c> and
/// <c>scale_y = origH / resizedH</c>. The body's caller computes these
/// using <c>image_width</c> / <c>image_height</c> on both the original
/// image and the <c>image_resize_to_stride</c> output. Each scale factor
/// also doubles as the upper bound on output x/y coordinates after the
/// unclip+scale-back step.
/// </para>
/// <para>
/// <strong>Output struct.</strong> <c>label = "text"</c> is constant —
/// PaddleOCR's detection model is single-class. <c>score</c> is the
/// component's mean probability (pre-unclip). Coordinates are in
/// original-image pixel space; <c>(x, y)</c> is the top-left corner,
/// <c>(w, h)</c> the width and height. Sorted top-to-bottom then
/// left-to-right — natural reading order for documents / receipts.
/// </para>
/// </remarks>
public sealed class DbnetPostprocessFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "dbnet_postprocess";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "DBNet text-detection post-processing: threshold + BFS connected components + polygon unclip + scale-back. " +
        "dbnet_postprocess(prob_map FLOAT32[], h INT, w INT, scale_x FLOAT32, scale_y FLOAT32, " +
        "pixel_threshold FLOAT32, box_score_threshold FLOAT32, min_size INT, unclip_ratio FLOAT32) " +
        "→ Array<Struct<label, score, x, y, w, h>>. " +
        "Use scale_x/scale_y = origDim/resizedDim to map boxes back to source-pixel coords.";

    /// <summary>
    /// Output struct schema. Surfaced as the function's
    /// <see cref="OutputFields"/> so downstream consumers (the per-query
    /// <see cref="TypeRegistry"/>, the lowered Project's typeId resolution)
    /// see the field names without inspecting individual values.
    /// </summary>
    public static IReadOnlyList<ColumnInfo> OutputFields { get; } =
    [
        new ColumnInfo("label", DataKind.String, nullable: false),
        new ColumnInfo("score", DataKind.Float32, nullable: false),
        new ColumnInfo("x",     DataKind.Float32, nullable: false),
        new ColumnInfo("y",     DataKind.Float32, nullable: false),
        new ColumnInfo("w",     DataKind.Float32, nullable: false),
        new ColumnInfo("h",     DataKind.Float32, nullable: false),
    ];

    /// <summary>
    /// Constant label cached once. Every detection carries it so downstream
    /// SQL that joins/groups by class name works the same way it would for
    /// a multi-class detector's output (where label varies per row).
    /// </summary>
    private static readonly ValueRef TextLabel = ValueRef.FromString("text");

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("prob_map",            DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("h",                   DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("w",                   DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("scale_x",             DataKindMatcher.Exact(DataKind.Float32)),
                new ParameterSpec("scale_y",             DataKindMatcher.Exact(DataKind.Float32)),
                new ParameterSpec("pixel_threshold",     DataKindMatcher.Exact(DataKind.Float32)),
                new ParameterSpec("box_score_threshold", DataKindMatcher.Exact(DataKind.Float32)),
                new ParameterSpec("min_size",            DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("unclip_ratio",        DataKindMatcher.Exact(DataKind.Float32)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Struct))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<DbnetPostprocessFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.Struct));
        }

        float[] probMap = ActivationOps.ReadFloat32Array(args[0]);
        int h = args[1].ToInt32();
        int w = args[2].ToInt32();
        float scaleX = args[3].ToFloat();
        float scaleY = args[4].ToFloat();
        float pixelThreshold = args[5].ToFloat();
        float boxScoreThreshold = args[6].ToFloat();
        int minSize = args[7].ToInt32();
        float unclipRatio = args[8].ToFloat();

        if (h <= 0 || w <= 0)
        {
            throw new FunctionArgumentException(Name,
                $"h and w must be positive, got [{h}, {w}].");
        }
        if (probMap.Length != h * w)
        {
            throw new FunctionArgumentException(Name,
                $"prob_map length must equal h × w = {h * w}, got {probMap.Length}.");
        }
        if (scaleX <= 0f || scaleY <= 0f)
        {
            throw new FunctionArgumentException(Name,
                $"scale_x and scale_y must be positive, got [{scaleX}, {scaleY}].");
        }
        if (minSize < 1)
        {
            throw new FunctionArgumentException(Name,
                $"min_size must be at least 1, got {minSize}.");
        }

        // Output-bounds for clipping: scale factor × resized-dim is the
        // implied original-image dim. We don't need the user to pass the
        // original dims separately — the scale factors carry the mapping.
        float origW = scaleX * w;
        float origH = scaleY * h;

        List<TextRegion> regions = FindRegions(
            probMap, w, h,
            pixelThreshold, boxScoreThreshold, minSize, unclipRatio,
            scaleX, scaleY, origW, origH,
            cancellationToken);

        return new ValueTask<ValueRef>(BuildDetectionArray(regions, frame.Types));
    }

    /// <summary>
    /// BFS-based connected-components labeling over a thresholded
    /// probability map, with per-component bbox + mean score. Each
    /// surviving component is DBNet-unclipped and mapped back to
    /// original-image coordinates. Mirrors
    /// <c>PpOcrDetectionModel.FindRegions</c> 1:1 — same component shapes,
    /// same accept/reject rules, same unclip formula — so the SQL-defined
    /// model produces equivalent boxes to the C# IModel.
    /// </summary>
    private static List<TextRegion> FindRegions(
        ReadOnlySpan<float> probMap,
        int width, int height,
        float pixelThreshold, float boxScoreThreshold,
        int minSize, float unclipRatio,
        float scaleBackX, float scaleBackY,
        float origW, float origH,
        CancellationToken cancellationToken)
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

            cancellationToken.ThrowIfCancellationRequested();

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

                // 4-connectivity. The "else { visited[n] = true; }" branches
                // mark pixels that failed the threshold as visited so the
                // outer seed loop doesn't re-visit them.
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

            // DBNet polygon-offset unclip, axis-aligned form:
            // distance = area × unclip_ratio / perimeter; expand each side by distance.
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

        // Top-to-bottom, then left-to-right — natural reading order for
        // receipt-style documents. Consumers can re-sort.
        regions.Sort((a, b) =>
        {
            int cmp = a.Y.CompareTo(b.Y);
            return cmp != 0 ? cmp : a.X.CompareTo(b.X);
        });

        return regions;
    }

    /// <summary>
    /// Wraps the regions list as an <c>Array&lt;Struct&gt;</c> ValueRef.
    /// Interns the struct schema in the per-query <see cref="TypeRegistry"/>
    /// so the output's field names survive through the materialisation
    /// boundary (downstream <c>det['label']</c> / <c>det['score']</c>
    /// accesses resolve via the registry).
    /// </summary>
    private static ValueRef BuildDetectionArray(List<TextRegion> regions, TypeRegistry? types)
    {
        if (regions.Count == 0)
        {
            return ValueRef.FromArray(DataKind.Struct, Array.Empty<ValueRef>());
        }

        ushort structTypeId = 0;
        if (types is not null)
        {
            structTypeId = (ushort)types.InternStructFromColumnInfoFields(OutputFields);
        }

        ValueRef[] elements = new ValueRef[regions.Count];
        for (int i = 0; i < regions.Count; i++)
        {
            TextRegion r = regions[i];
            ValueRef[] fields =
            [
                TextLabel,
                ValueRef.FromFloat32(r.Score),
                ValueRef.FromFloat32(r.X),
                ValueRef.FromFloat32(r.Y),
                ValueRef.FromFloat32(r.W),
                ValueRef.FromFloat32(r.H),
            ];
            elements[i] = structTypeId == 0
                ? ValueRef.FromStruct(fields)
                : ValueRef.FromStruct(fields, structTypeId);
        }
        return ValueRef.FromArray(DataKind.Struct, elements);
    }

    private readonly record struct TextRegion(float Score, float X, float Y, float W, float H);
}
