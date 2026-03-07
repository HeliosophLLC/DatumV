using DatumIngest.Model;

namespace DatumIngest.Catalog.Registries;

/// <summary>
/// Engine-defined registry of task contracts — the typed interface a model
/// can declare via <c>IMPLEMENTS TaskName</c>. Mirrors
/// <see cref="NamedTypeRegistry"/>'s static + immutable shape: users can't
/// mint task types from SQL today (the vocabulary is the interop layer; if
/// every user could add one, the frontend couldn't rely on anything).
/// </summary>
/// <remarks>
/// <para>
/// Each contract is a tuple of:
/// <list type="bullet">
/// <item><c>Name</c> — canonical identifier (case-insensitive on lookup).</item>
/// <item><c>InputKinds</c> — ordered list of <c>(DataKind, IsArray)</c> tuples; one per parameter in declaration order. Parameter NAMES are not part of the contract — names are documentation. C# interface convention.</item>
/// <item><c>ReturnKind</c> — <c>(DataKind, IsArray)</c> plus an optional <c>NamedTypeName</c> when the return is a named struct (e.g. <c>"ScoredClass"</c>). For primitive returns NamedTypeName is null.</item>
/// <item><c>Description</c> — one-line human summary for <c>system.tasks</c> and language-server hover.</item>
/// </list>
/// </para>
/// <para>
/// The starting vocabulary is intentionally narrow — the contracts our two
/// existing SQL-defined models need (<c>TextEmbedder</c>, <c>TextDetector</c>)
/// plus a handful of the most common shapes. Extending the registry is a
/// one-line addition per contract; the comprehensive list lives in
/// <c>plans/model-task-types.md</c>'s appendix.
/// </para>
/// </remarks>
public static class TaskTypeRegistry
{
    /// <summary>
    /// (Kind, IsArray, optional named-type name) tuple used uniformly for
    /// parameter input kinds and return kinds. <c>NamedTypeName</c> is
    /// non-null when the slot references a named struct from
    /// <see cref="NamedTypeRegistry"/> (e.g. a return of
    /// <c>Array&lt;RegionScore&gt;</c> has Kind=Struct, IsArray=true,
    /// NamedTypeName="RegionScore").
    /// </summary>
    public sealed record TypeSlot(DataKind Kind, bool IsArray, string? NamedTypeName = null)
    {
        /// <summary>Human-readable slot description for diagnostics + system.tasks.</summary>
        public override string ToString() => (Kind, IsArray, NamedTypeName) switch
        {
            (_, false, null) => Kind.ToString(),
            (_, true, null) => $"Array<{Kind}>",
            (_, false, _) => NamedTypeName!,
            (_, true, _) => $"Array<{NamedTypeName}>",
        };

        /// <summary>Matches a candidate (kind, isArray, namedTypeName) against this slot.</summary>
        public bool Matches(DataKind kind, bool isArray, string? namedTypeName)
        {
            if (Kind != kind) return false;
            if (IsArray != isArray) return false;
            // Named-type match: if this slot declares a named type, the
            // candidate must also declare the same name (case-insensitive).
            // If this slot has NO named-type requirement (Kind=Float32,
            // Kind=Int32, etc.), candidate's NamedTypeName is irrelevant.
            if (NamedTypeName is not null)
            {
                return namedTypeName is not null
                    && string.Equals(NamedTypeName, namedTypeName, StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }
    }

    /// <summary>A single registered task contract.</summary>
    /// <param name="Name">Canonical identifier.</param>
    /// <param name="InputKinds">Ordered parameter slots; one per call-site argument.</param>
    /// <param name="ReturnKind">Return-value slot.</param>
    /// <param name="Description">Human-readable summary.</param>
    public sealed record TaskContract(
        string Name,
        IReadOnlyList<TypeSlot> InputKinds,
        TypeSlot ReturnKind,
        string Description);

    private static TypeSlot Scalar(DataKind k) => new(k, IsArray: false);
    private static TypeSlot ArrayOf(DataKind k) => new(k, IsArray: true);
    private static TypeSlot Named(string n) => new(DataKind.Struct, IsArray: false, NamedTypeName: n);
    private static TypeSlot ArrayOfNamed(string n) => new(DataKind.Struct, IsArray: true, NamedTypeName: n);

    /// <summary>
    /// Initial task vocabulary. Strict subset of the
    /// <c>plans/model-task-types.md</c> appendix — covers the two existing
    /// SQL-defined models (<c>TextEmbedder</c>, <c>TextDetector</c>) plus
    /// the most common adjacent shapes that future models will want.
    /// </summary>
    public static IReadOnlyList<TaskContract> Entries { get; } =
    [
        // ─── text ───────────────────────────────────────────────────────
        new(
            "TextEmbedder",
            [Scalar(DataKind.String)],
            ArrayOf(DataKind.Float32),
            "Text → vector embedding. L2-normalized by convention."),
        new(
            "TextClassifier",
            [Scalar(DataKind.String)],
            Named("ScoredClass"),
            "Text → single class with confidence."),
        new(
            "TextGenerator",
            [Scalar(DataKind.String)],
            Scalar(DataKind.String),
            "Text → text. LLM completion / translation / summary."),

        // ─── image ──────────────────────────────────────────────────────
        new(
            "ImageClassifier",
            [Scalar(DataKind.Image)],
            Named("ScoredClass"),
            "Image → single class with confidence."),
        new(
            "ImageEmbedder",
            [Scalar(DataKind.Image)],
            ArrayOf(DataKind.Float32),
            "Image → vector embedding. L2-normalized by convention."),
        new(
            "ImageCaptioner",
            [Scalar(DataKind.Image)],
            Scalar(DataKind.String),
            "Image → descriptive caption."),
        new(
            "ObjectDetector",
            [Scalar(DataKind.Image)],
            ArrayOfNamed("ScoredDetection"),
            "Image → bounding boxes with classes + confidences."),
        new(
            "TextDetector",
            [Scalar(DataKind.Image)],
            ArrayOfNamed("RegionScore"),
            "Image → text region bounding boxes (no recognition)."),
        new(
            "TextRecognizer",
            [Scalar(DataKind.Image)],
            Scalar(DataKind.String),
            "Image (single text line crop) → recognized text."),
        new(
            "FaceDetector",
            [Scalar(DataKind.Image)],
            ArrayOfNamed("FaceDetection"),
            "Image → face bounding boxes with landmarks + confidence."),
        new(
            "DepthEstimator",
            [Scalar(DataKind.Image)],
            Scalar(DataKind.Image),
            "Image → per-pixel depth as a grayscale Image."),

        // ─── audio ──────────────────────────────────────────────────────
        new(
            "AudioToText",
            [Scalar(DataKind.Audio)],
            Scalar(DataKind.String),
            "Audio → transcribed text."),
        new(
            "AudioEmbedder",
            [Scalar(DataKind.Audio)],
            ArrayOf(DataKind.Float32),
            "Audio → vector embedding."),
    ];

    /// <summary>
    /// Case-insensitive lookup by name. Returns <see langword="null"/> when
    /// no contract matches.
    /// </summary>
    public static TaskContract? TryGet(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        for (int i = 0; i < Entries.Count; i++)
        {
            if (string.Equals(Entries[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return Entries[i];
            }
        }
        return null;
    }
}
