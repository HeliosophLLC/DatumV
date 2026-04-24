namespace DatumIngest.Model;

/// <summary>
/// Process-wide vocabulary of well-known named composite types — the
/// reusable struct shapes that model contracts and SQL surfaces reference
/// by name (<c>ScoredClass</c>, <c>BoundingBox</c>, <c>ScoredDetection</c>, …).
/// </summary>
/// <remarks>
/// <para>
/// <strong>How it composes with <see cref="TypeRegistry"/>.</strong> The
/// per-query <c>TypeRegistry</c> pre-interns every entry in <see cref="Entries"/>
/// on construction (topological order — primitives → composites →
/// composites-of-composites) and exposes a name→TypeId lookup alongside
/// the existing structural intern. Callers that need a TypeId for a
/// declared annotation like <c>RETURNS ScoredClass</c> resolve the name
/// against the per-query registry; the registry returns the pre-interned
/// TypeId rather than re-building the descriptor.
/// </para>
/// <para>
/// <strong>Why static.</strong> The vocabulary is engine-defined — users
/// can't mint new entries from SQL today (CREATE TYPE for user-defined
/// types is a future possibility). Keeping it static + immutable means
/// every TypeRegistry sees the same names + shapes, and the language
/// server can surface them in completion without a runtime hop.
/// </para>
/// <para>
/// <strong>Field-naming conventions</strong> (locked in across the
/// vocabulary so consumers don't re-bikeshed):
/// <list type="bullet">
/// <item><c>class: Int32</c> for finite ordered label sets (ImageNet,
/// COCO indices); <c>label: String</c> for open-vocabulary text labels.</item>
/// <item><c>score: Float32</c> for confidence / probability everywhere
/// except <c>Keypoint</c>, which uses <c>confidence</c> to match pose-
/// estimation library convention.</item>
/// <item><c>bbox: BoundingBox</c> nested struct (xywh, pixel-space, top-
/// left origin).</item>
/// <item><c>mask: Image</c> for segmentation masks.</item>
/// <item><c>start_ms / end_ms: Int64</c> for temporal spans.</item>
/// <item>Score field goes last so projection-of-leading-fields reads
/// the natural shape (<c>det.class</c>, <c>det.bbox</c>).</item>
/// </list>
/// </para>
/// </remarks>
public static class NamedTypeRegistry
{
    /// <summary>
    /// Definition of a single named type — its display name, its
    /// pre-computed shape description (for <c>system.types</c>), and the
    /// recipe for interning it into a fresh <see cref="TypeRegistry"/>.
    /// </summary>
    /// <param name="Name">Canonical name (case-sensitive in the registry; case-insensitive in lookups).</param>
    /// <param name="Description">Human-readable struct shape, e.g. <c>"Struct&lt;class: Int32, score: Float32&gt;"</c>.</param>
    /// <param name="Build">
    /// Recipe that interns this type's shape into the supplied registry.
    /// Receives the registry plus a snapshot of the names interned so far
    /// in the topological pre-pass — composite types reference their
    /// dependencies by name (<c>byName["BoundingBox"]</c>).
    /// </param>
    public sealed record NamedTypeDefinition(
        string Name,
        string Description,
        Func<TypeRegistry, IReadOnlyDictionary<string, int>, int> Build);

    /// <summary>
    /// The 17-entry vocabulary, ordered topologically: primitives
    /// composed into composites are listed before the composites that
    /// reference them (<c>BoundingBox</c> before <c>ScoredDetection</c>,
    /// etc.). The <see cref="TypeRegistry"/> constructor iterates this
    /// list in order; each entry's <see cref="NamedTypeDefinition.Build"/>
    /// callback can assume every earlier entry has already been interned
    /// and its TypeId is available in the name index.
    /// </summary>
    public static IReadOnlyList<NamedTypeDefinition> Entries { get; } =
    [
        // ─── primitives (no dependencies) ─────────────────────────────────

        new(
            "BoundingBox",
            "Struct<x: Float32, y: Float32, w: Float32, h: Float32>",
            (reg, _) => reg.InternStructType(
            [
                new("x", reg.InternScalarType(DataKind.Float32)),
                new("y", reg.InternScalarType(DataKind.Float32)),
                new("w", reg.InternScalarType(DataKind.Float32)),
                new("h", reg.InternScalarType(DataKind.Float32)),
            ])),

        new(
            "ScoredClass",
            "Struct<class: Int32, score: Float32>",
            (reg, _) => reg.InternStructType(
            [
                new("class", reg.InternScalarType(DataKind.Int32)),
                new("score", reg.InternScalarType(DataKind.Float32)),
            ])),

        new(
            "ScoredLabel",
            "Struct<label: String, score: Float32>",
            (reg, _) => reg.InternStructType(
            [
                new("label", reg.InternScalarType(DataKind.String)),
                new("score", reg.InternScalarType(DataKind.Float32)),
            ])),

        new(
            "BinaryScore",
            "Struct<value: Boolean, score: Float32>",
            (reg, _) => reg.InternStructType(
            [
                new("value", reg.InternScalarType(DataKind.Boolean)),
                new("score", reg.InternScalarType(DataKind.Float32)),
            ])),

        new(
            "ScoredIndex",
            "Struct<index: Int32, score: Float32>",
            (reg, _) => reg.InternStructType(
            [
                new("index", reg.InternScalarType(DataKind.Int32)),
                new("score", reg.InternScalarType(DataKind.Float32)),
            ])),

        new(
            "ScoredToken",
            "Struct<token: String, label: String, score: Float32>",
            (reg, _) => reg.InternStructType(
            [
                new("token", reg.InternScalarType(DataKind.String)),
                new("label", reg.InternScalarType(DataKind.String)),
                new("score", reg.InternScalarType(DataKind.Float32)),
            ])),

        new(
            "Keypoint",
            "Struct<x: Float32, y: Float32, confidence: Float32>",
            (reg, _) => reg.InternStructType(
            [
                new("x", reg.InternScalarType(DataKind.Float32)),
                new("y", reg.InternScalarType(DataKind.Float32)),
                new("confidence", reg.InternScalarType(DataKind.Float32)),
            ])),

        new(
            "ScoredMask",
            "Struct<mask: Image, class: Int32, score: Float32>",
            (reg, _) => reg.InternStructType(
            [
                new("mask", reg.InternScalarType(DataKind.Image)),
                new("class", reg.InternScalarType(DataKind.Int32)),
                new("score", reg.InternScalarType(DataKind.Float32)),
            ])),

        new(
            "TimedSpan",
            "Struct<start_ms: Int64, end_ms: Int64>",
            (reg, _) => reg.InternStructType(
            [
                new("start_ms", reg.InternScalarType(DataKind.Int64)),
                new("end_ms", reg.InternScalarType(DataKind.Int64)),
            ])),

        new(
            "TimedText",
            "Struct<text: String, start_ms: Int64, end_ms: Int64>",
            (reg, _) => reg.InternStructType(
            [
                new("text", reg.InternScalarType(DataKind.String)),
                new("start_ms", reg.InternScalarType(DataKind.Int64)),
                new("end_ms", reg.InternScalarType(DataKind.Int64)),
            ])),

        new(
            "TimedClass",
            "Struct<class: Int32, start_ms: Int64, end_ms: Int64, score: Float32>",
            (reg, _) => reg.InternStructType(
            [
                new("class", reg.InternScalarType(DataKind.Int32)),
                new("start_ms", reg.InternScalarType(DataKind.Int64)),
                new("end_ms", reg.InternScalarType(DataKind.Int64)),
                new("score", reg.InternScalarType(DataKind.Float32)),
            ])),

        // ─── composites that reference BoundingBox ───────────────────────

        new(
            "RegionScore",
            "Struct<bbox: BoundingBox, score: Float32>",
            (reg, byName) => reg.InternStructType(
            [
                new("bbox", byName["BoundingBox"]),
                new("score", reg.InternScalarType(DataKind.Float32)),
            ])),

        new(
            "ScoredDetection",
            "Struct<bbox: BoundingBox, class: Int32, score: Float32>",
            (reg, byName) => reg.InternStructType(
            [
                new("bbox", byName["BoundingBox"]),
                new("class", reg.InternScalarType(DataKind.Int32)),
                new("score", reg.InternScalarType(DataKind.Float32)),
            ])),

        new(
            "LabeledDetection",
            "Struct<bbox: BoundingBox, label: String, score: Float32>",
            (reg, byName) => reg.InternStructType(
            [
                new("bbox", byName["BoundingBox"]),
                new("label", reg.InternScalarType(DataKind.String)),
                new("score", reg.InternScalarType(DataKind.Float32)),
            ])),

        new(
            "FaceDetection",
            "Struct<bbox: BoundingBox, label: String, landmarks: Array<Point2D>, score: Float32>",
            (reg, byName) => reg.InternStructType(
            [
                new("bbox", byName["BoundingBox"]),
                new("label", reg.InternScalarType(DataKind.String)),
                new("landmarks", reg.InternArrayType(DataKind.Point2D)),
                new("score", reg.InternScalarType(DataKind.Float32)),
            ])),

        new(
            "OcrLine",
            "Struct<bbox: BoundingBox, text: String, score: Float32>",
            (reg, byName) => reg.InternStructType(
            [
                new("bbox", byName["BoundingBox"]),
                new("text", reg.InternScalarType(DataKind.String)),
                new("score", reg.InternScalarType(DataKind.Float32)),
            ])),

        // ─── multimodal embedding pair (CLIP-style dual encoder) ─────────

        new(
            "DualEmbedding",
            "Struct<image: Array<Float32>, text: Array<Float32>>",
            (reg, _) => reg.InternStructType(
            [
                new("image", reg.InternArrayType(DataKind.Float32)),
                new("text", reg.InternArrayType(DataKind.Float32)),
            ])),
    ];
}
