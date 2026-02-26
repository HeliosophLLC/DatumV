using System.Numerics;

using DatumIngest.Execution;
using DatumIngest.Functions.Scalar.Activation;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Vector;

/// <summary>
/// <c>mask_to_polygon(mask FLOAT32[], width INT, height INT, threshold FLOAT32) → Array&lt;Point2D&gt;</c>.
/// Converts a 2D binary-segmentation mask into a smooth polygon outline
/// suitable for overlay rendering, COCO-style export, or geometric
/// computation. Returns the longest closed contour, simplified via
/// Douglas–Peucker.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Algorithm.</strong> Marching Squares with sub-pixel linear
/// interpolation against <c>threshold</c>. Saddle-point cases (5 and 10)
/// disambiguate via the 2×2 cell-centre average, the standard convention.
/// The line segments output by MS get walked into a closed loop, then
/// fed through Douglas-Peucker simplification at ε=0.5 pixels — typically
/// cuts the vertex count by ~80% with no visible quality loss.
/// </para>
/// <para>
/// <strong>Single-contour v1.</strong> Multi-component masks (rare for
/// instance segmentation, possible for semantic seg) return only the
/// longest contour. If you need per-component polygons, pre-filter the
/// mask upstream. Holes (donuts) likewise collapse — the function returns
/// the outer boundary only.
/// </para>
/// <para>
/// <strong>Coordinate space.</strong> Output X / Y are in pixel space
/// matching the input <c>width</c> × <c>height</c>. Sub-pixel precision
/// is preserved (positions are floats, not integers).
/// </para>
/// </remarks>
public sealed class MaskToPolygonFunction : IFunction, IScalarFunction
{
    private const float SimplifyEpsilon = 0.5f;

    /// <inheritdoc />
    public static string Name => "mask_to_polygon";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;

    /// <inheritdoc />
    public static string Description =>
        "Extracts the longest closed contour from a 2D binary-segmentation mask: " +
        "mask_to_polygon(mask FLOAT32[], width INT, height INT, threshold FLOAT32) → Array<Point2D>. " +
        "Uses Marching Squares (sub-pixel linear interpolation) + Douglas–Peucker simplification. " +
        "Returns the outer boundary of the longest connected region; multi-component masks need pre-filtering.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("mask",      DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("width",     DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("height",    DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("threshold", DataKindMatcher.Exact(DataKind.Float32)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Point2D))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<MaskToPolygonFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull)
        {
            return new(ValueRef.NullArray(DataKind.Point2D));
        }

        int width = args[1].ToInt32();
        int height = args[2].ToInt32();
        if (width <= 1 || height <= 1)
        {
            throw new FunctionArgumentException(Name,
                $"mask must be at least 2×2 (got {width}×{height}). Marching Squares operates on 2×2 cell windows.");
        }

        float threshold = (float)args[3].ToDouble();
        float[] mask = ActivationOps.ReadFloat32Array(args[0]);
        if (mask.Length != width * height)
        {
            throw new FunctionArgumentException(Name,
                $"mask length must equal width × height = {width * height}, got {mask.Length}.");
        }

        List<Vector2> polygon = ExtractLongestContour(mask, width, height, threshold);
        Vector2[] simplified = polygon.Count > 2
            ? DouglasPeuckerClosed(polygon, SimplifyEpsilon)
            : polygon.ToArray();

        return new(ValueRef.FromPrimitiveArray(simplified, DataKind.Point2D));
    }

    // ─── Marching Squares ────────────────────────────────────────────────────

    /// <summary>
    /// Walks the mask cell-by-cell, emits a line segment per active 2×2 cell
    /// per the MS table, and stitches the segments into closed loops. Returns
    /// the longest loop's vertices in walk order. Empty list when the mask
    /// has no contours (all-on or all-off above threshold).
    /// </summary>
    private static List<Vector2> ExtractLongestContour(float[] mask, int width, int height, float threshold)
    {
        // Each MS edge is identified by (kind, x, y):
        //   H, x, y = top edge of cell (x, y), spanning [(x, y), (x+1, y)]
        //   V, x, y = left edge of cell (x, y), spanning [(x, y), (x, y+1)]
        // Adjacent cells share edges so segment endpoints match exactly.
        Dictionary<long, List<Segment>> segmentsByEdge = new();
        int cw = width - 1;
        int ch = height - 1;

        for (int y = 0; y < ch; y++)
        {
            for (int x = 0; x < cw; x++)
            {
                float tl = mask[y       * width + x    ];
                float tr = mask[y       * width + x + 1];
                float br = mask[(y + 1) * width + x + 1];
                float bl = mask[(y + 1) * width + x    ];

                int caseIdx = 0;
                if (tl >= threshold) caseIdx |= 8;
                if (tr >= threshold) caseIdx |= 4;
                if (br >= threshold) caseIdx |= 2;
                if (bl >= threshold) caseIdx |= 1;
                if (caseIdx == 0 || caseIdx == 15) continue;

                // Resolve saddle ambiguity (cases 5 and 10) via cell-centre
                // value. The "inside" of the contour stays on whichever side
                // the centre falls.
                bool inside = (tl + tr + br + bl) * 0.25f >= threshold;

                EmitSegments(caseIdx, inside, x, y, tl, tr, br, bl, threshold, segmentsByEdge);
            }
        }

        // Walk segments into closed loops. Each non-saddle cell contributes 1
        // segment, saddles contribute 2. Edges are shared between adjacent
        // cells, so a polygon's segments form a connected chain by
        // construction.
        List<List<Vector2>> contours = new();
        foreach (List<Segment> bucket in segmentsByEdge.Values)
        {
            for (int i = 0; i < bucket.Count; i++)
            {
                Segment seg = bucket[i];
                if (seg.Consumed) continue;
                List<Vector2> contour = WalkContour(seg, segmentsByEdge);
                if (contour.Count >= 3) contours.Add(contour);
            }
        }

        if (contours.Count == 0) return new();

        List<Vector2> longest = contours[0];
        for (int i = 1; i < contours.Count; i++)
        {
            if (contours[i].Count > longest.Count) longest = contours[i];
        }
        return longest;
    }

    private static void EmitSegments(
        int caseIdx, bool inside,
        int cx, int cy,
        float tl, float tr, float br, float bl,
        float threshold,
        Dictionary<long, List<Segment>> segmentsByEdge)
    {
        // Edge identifiers for this cell:
        long topEdge    = EdgeKey('H', cx,     cy    );
        long bottomEdge = EdgeKey('H', cx,     cy + 1);
        long leftEdge   = EdgeKey('V', cx,     cy    );
        long rightEdge  = EdgeKey('V', cx + 1, cy    );

        // Interpolated points on each cell edge.
        Vector2 topPt    = new(cx + Interp(tl, tr, threshold), cy            );
        Vector2 bottomPt = new(cx + Interp(bl, br, threshold), cy + 1        );
        Vector2 leftPt   = new(cx,                              cy + Interp(tl, bl, threshold));
        Vector2 rightPt  = new(cx + 1,                          cy + Interp(tr, br, threshold));

        // 16-case Marching Squares table. Bit ordering: bit3=TL, bit2=TR,
        // bit1=BR, bit0=BL. Each emitted segment connects two cell edges.
        switch (caseIdx)
        {
            case 1:  AddSegment(segmentsByEdge, leftEdge, leftPt, bottomEdge, bottomPt); break;
            case 2:  AddSegment(segmentsByEdge, bottomEdge, bottomPt, rightEdge, rightPt); break;
            case 3:  AddSegment(segmentsByEdge, leftEdge, leftPt, rightEdge, rightPt); break;
            case 4:  AddSegment(segmentsByEdge, topEdge, topPt, rightEdge, rightPt); break;
            case 5:
                // Saddle: TR + BL inside, TL + BR outside.
                if (inside)
                {
                    AddSegment(segmentsByEdge, leftEdge, leftPt, topEdge, topPt);
                    AddSegment(segmentsByEdge, bottomEdge, bottomPt, rightEdge, rightPt);
                }
                else
                {
                    AddSegment(segmentsByEdge, leftEdge, leftPt, bottomEdge, bottomPt);
                    AddSegment(segmentsByEdge, topEdge, topPt, rightEdge, rightPt);
                }
                break;
            case 6:  AddSegment(segmentsByEdge, topEdge, topPt, bottomEdge, bottomPt); break;
            case 7:  AddSegment(segmentsByEdge, leftEdge, leftPt, topEdge, topPt); break;
            case 8:  AddSegment(segmentsByEdge, topEdge, topPt, leftEdge, leftPt); break;
            case 9:  AddSegment(segmentsByEdge, topEdge, topPt, bottomEdge, bottomPt); break;
            case 10:
                // Saddle: TL + BR inside, TR + BL outside.
                if (inside)
                {
                    AddSegment(segmentsByEdge, topEdge, topPt, rightEdge, rightPt);
                    AddSegment(segmentsByEdge, leftEdge, leftPt, bottomEdge, bottomPt);
                }
                else
                {
                    AddSegment(segmentsByEdge, topEdge, topPt, leftEdge, leftPt);
                    AddSegment(segmentsByEdge, bottomEdge, bottomPt, rightEdge, rightPt);
                }
                break;
            case 11: AddSegment(segmentsByEdge, topEdge, topPt, rightEdge, rightPt); break;
            case 12: AddSegment(segmentsByEdge, leftEdge, leftPt, rightEdge, rightPt); break;
            case 13: AddSegment(segmentsByEdge, bottomEdge, bottomPt, rightEdge, rightPt); break;
            case 14: AddSegment(segmentsByEdge, leftEdge, leftPt, bottomEdge, bottomPt); break;
        }
    }

    private static void AddSegment(
        Dictionary<long, List<Segment>> segmentsByEdge,
        long edgeA, Vector2 pointA,
        long edgeB, Vector2 pointB)
    {
        Segment seg = new(edgeA, pointA, edgeB, pointB);
        if (!segmentsByEdge.TryGetValue(edgeA, out List<Segment>? bucketA))
        {
            bucketA = new List<Segment>(2);
            segmentsByEdge[edgeA] = bucketA;
        }
        bucketA.Add(seg);
        if (!segmentsByEdge.TryGetValue(edgeB, out List<Segment>? bucketB))
        {
            bucketB = new List<Segment>(2);
            segmentsByEdge[edgeB] = bucketB;
        }
        bucketB.Add(seg);
    }

    /// <summary>
    /// Walks segments end-to-end starting from <paramref name="seed"/>, marking
    /// each as consumed, until the walk closes back on the starting edge.
    /// Returns the polygon's vertices in walk order.
    /// </summary>
    private static List<Vector2> WalkContour(Segment seed, Dictionary<long, List<Segment>> segmentsByEdge)
    {
        List<Vector2> result = new();
        seed.Consumed = true;
        long startEdge = seed.EdgeA;
        long currentEdge = seed.EdgeB;
        Vector2 currentPoint = seed.PointB;
        result.Add(seed.PointA);
        result.Add(seed.PointB);

        while (currentEdge != startEdge)
        {
            if (!segmentsByEdge.TryGetValue(currentEdge, out List<Segment>? candidates)) break;
            Segment? next = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (!candidates[i].Consumed)
                {
                    next = candidates[i];
                    break;
                }
            }
            if (next is null) break;

            next.Consumed = true;
            if (next.EdgeA == currentEdge)
            {
                currentPoint = next.PointB;
                currentEdge = next.EdgeB;
            }
            else
            {
                currentPoint = next.PointA;
                currentEdge = next.EdgeA;
            }
            result.Add(currentPoint);
        }

        // Strip the duplicated start vertex (we appended both seed.PointA and
        // seed.PointB; if the walk closed, the last appended point equals
        // PointA's position at startEdge).
        if (result.Count > 1 && result[0] == result[^1])
        {
            result.RemoveAt(result.Count - 1);
        }
        return result;
    }

    /// <summary>
    /// Encodes an MS edge identifier as a 64-bit key for dictionary lookup.
    /// 'H' / 'V' encoded in the top byte, X and Y as 24-bit ints —
    /// sufficient for masks up to 16 million pixels per side.
    /// </summary>
    private static long EdgeKey(char kind, int x, int y)
    {
        long k = kind == 'H' ? 0L : 1L;
        return (k << 56) | ((long)(x & 0xFFFFFF) << 24) | (long)(y & 0xFFFFFF);
    }

    /// <summary>
    /// Linear interpolation factor for the iso-contour crossing on the edge
    /// between corners with values <paramref name="a"/> and <paramref name="b"/>.
    /// Returns a fraction in [0, 1] indicating how far from <paramref name="a"/>
    /// the contour crosses. Clamped to avoid edge-case 0/0 when the two
    /// corners happen to share a value.
    /// </summary>
    private static float Interp(float a, float b, float threshold)
    {
        float denom = b - a;
        if (denom == 0f) return 0.5f;
        float t = (threshold - a) / denom;
        if (t < 0f) return 0f;
        if (t > 1f) return 1f;
        return t;
    }

    private sealed class Segment
    {
        public Segment(long edgeA, Vector2 pointA, long edgeB, Vector2 pointB)
        {
            EdgeA = edgeA;
            PointA = pointA;
            EdgeB = edgeB;
            PointB = pointB;
        }
        public long EdgeA { get; }
        public Vector2 PointA { get; }
        public long EdgeB { get; }
        public Vector2 PointB { get; }
        public bool Consumed { get; set; }
    }

    // ─── Douglas-Peucker simplification ──────────────────────────────────────

    /// <summary>
    /// Douglas–Peucker simplification for a closed polygon. The standard
    /// open-polyline DP only knows about two endpoints, so for closed
    /// polygons we first split at the two vertices farthest from each
    /// other and run DP on both halves. This avoids a degenerate "first
    /// and last vertices arbitrarily preserved" artefact when the natural
    /// starting vertex is mid-curve.
    /// </summary>
    private static Vector2[] DouglasPeuckerClosed(List<Vector2> points, float epsilon)
    {
        // Find the two points farthest apart by index distance — cheaper
        // than O(n²) farthest-pair and good enough for natural-shaped
        // contours.
        int n = points.Count;
        int half = n / 2;
        // First, pick the seed: vertex farthest from index-0 by Euclidean
        // distance. Then the seed's antipode is whichever index is
        // farthest from the seed.
        int seed = 0;
        float seedDist = 0f;
        for (int i = 1; i < n; i++)
        {
            float d = Vector2.DistanceSquared(points[0], points[i]);
            if (d > seedDist) { seedDist = d; seed = i; }
        }
        int antipode = 0;
        float antipodeDist = 0f;
        for (int i = 0; i < n; i++)
        {
            float d = Vector2.DistanceSquared(points[seed], points[i]);
            if (d > antipodeDist) { antipodeDist = d; antipode = i; }
        }
        if (seed == antipode)
        {
            // Degenerate: all points coincident. Return as-is.
            return points.ToArray();
        }

        int lo = System.Math.Min(seed, antipode);
        int hi = System.Math.Max(seed, antipode);
        List<Vector2> firstHalf = points.GetRange(lo, hi - lo + 1);
        List<Vector2> secondHalf = new(n - (hi - lo));
        for (int i = hi; i < n; i++) secondHalf.Add(points[i]);
        for (int i = 0; i <= lo; i++) secondHalf.Add(points[i]);

        List<Vector2> simplifiedA = new();
        DouglasPeuckerOpen(firstHalf, 0, firstHalf.Count - 1, epsilon, simplifiedA);
        simplifiedA.Add(firstHalf[^1]);

        List<Vector2> simplifiedB = new();
        DouglasPeuckerOpen(secondHalf, 0, secondHalf.Count - 1, epsilon, simplifiedB);
        // Skip simplifiedB's first vertex — it's the same as simplifiedA's last.
        // Skip simplifiedB's last vertex — it closes back to simplifiedA's first.

        List<Vector2> result = new(simplifiedA.Count + simplifiedB.Count);
        result.AddRange(simplifiedA);
        for (int i = 1; i < simplifiedB.Count; i++) result.Add(simplifiedB[i]);
        result.Add(secondHalf[^1]);
        // Result currently double-closes (start ≈ end). Drop the duplicate.
        if (result.Count > 1 && result[0] == result[^1])
        {
            result.RemoveAt(result.Count - 1);
        }
        return result.ToArray();
    }

    private static void DouglasPeuckerOpen(
        List<Vector2> points, int startIdx, int endIdx, float epsilon, List<Vector2> output)
    {
        // Find the point farthest from the line(start, end). If it's
        // farther than ε we keep it and recurse on both halves; otherwise
        // we drop everything between start and end.
        Vector2 a = points[startIdx];
        Vector2 b = points[endIdx];
        float maxDist = 0f;
        int maxIdx = -1;
        for (int i = startIdx + 1; i < endIdx; i++)
        {
            float d = PerpendicularDistance(points[i], a, b);
            if (d > maxDist)
            {
                maxDist = d;
                maxIdx = i;
            }
        }

        if (maxDist > epsilon && maxIdx > 0)
        {
            DouglasPeuckerOpen(points, startIdx, maxIdx, epsilon, output);
            DouglasPeuckerOpen(points, maxIdx, endIdx, epsilon, output);
        }
        else
        {
            output.Add(points[startIdx]);
        }
    }

    /// <summary>
    /// Perpendicular distance from <paramref name="p"/> to the line through
    /// <paramref name="a"/> and <paramref name="b"/>. Falls back to point-
    /// to-point distance when a == b (degenerate line).
    /// </summary>
    private static float PerpendicularDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float abLenSq = ab.LengthSquared();
        if (abLenSq == 0f) return Vector2.Distance(p, a);
        // 2D cross magnitude / |ab|.
        float cross = ab.X * (p.Y - a.Y) - ab.Y * (p.X - a.X);
        return System.Math.Abs(cross) / MathF.Sqrt(abLenSq);
    }
}
