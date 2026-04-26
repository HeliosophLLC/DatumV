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
/// <item><c>Family</c> — coarse grouping (Text / Image / Audio / Video / Multimodal / Structured)
/// surfaced on the model-browser filter UI.</item>
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
    /// Coarse grouping over <see cref="TaskContract"/> entries. Surfaced on
    /// the model-browser front-end as faceted-filter section headers and on
    /// the <c>system.task_contracts</c> virtual table's <c>family</c> column.
    /// </summary>
    public enum TaskFamily
    {
        /// <summary>Text-in / text-out contracts (embedding, classification, generation, …).</summary>
        Text,
        /// <summary>Image-in contracts (classification, detection, segmentation, depth, 3D, restoration, …).</summary>
        Image,
        /// <summary>Audio-in or audio-out contracts (classification, embedding, ASR, TTS, …).</summary>
        Audio,
        /// <summary>Video-in contracts.</summary>
        Video,
        /// <summary>Cross-modal contracts (image + text, text → image, VQA, CLIP, …).</summary>
        Multimodal,
        /// <summary>Tabular / time-series contracts.</summary>
        Structured,
    }

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
    /// <param name="Family">Coarse grouping for filter UI + catalog inspection.</param>
    /// <param name="InputKinds">Ordered parameter slots; one per call-site argument.</param>
    /// <param name="ReturnKind">Return-value slot.</param>
    /// <param name="Description">Human-readable summary.</param>
    public sealed record TaskContract(
        string Name,
        TaskFamily Family,
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
        new("TextEmbedder", TaskFamily.Text,
            [Scalar(DataKind.String)],
            ArrayOf(DataKind.Float32),
            "Text → vector embedding. L2-normalized by convention."),
        new("TextClassifier", TaskFamily.Text,
            [Scalar(DataKind.String)],
            Named("ScoredClass"),
            "Text → single class with confidence."),
        new("LabeledTextClassifier", TaskFamily.Text,
            [Scalar(DataKind.String)],
            Named("ScoredLabel"),
            "Text → single class-label string with confidence. "
            + "Like TextClassifier but emits a human-readable label (e.g. 'positive', "
            + "'toxic') instead of an integer class id."),
        new("TextMultiClassifier", TaskFamily.Text,
            [Scalar(DataKind.String)],
            ArrayOfNamed("ScoredClass"),
            "Text → multiple classes with confidence (multi-label)."),
        new("LabeledTextMultiClassifier", TaskFamily.Text,
            [Scalar(DataKind.String)],
            ArrayOfNamed("ScoredLabel"),
            "Text → multiple class-label strings with confidence (multi-label). "
            + "Like TextMultiClassifier but emits human-readable labels (e.g. 'toxic', "
            + "'severe_toxic') instead of integer class ids. Each label is scored "
            + "independently (sigmoid head, not softmax)."),
        new("BinaryTextClassifier", TaskFamily.Text,
            [Scalar(DataKind.String)],
            Named("BinaryScore"),
            "Text → boolean classification with confidence (spam/toxicity/polarity)."),
        new("TokenClassifier", TaskFamily.Text,
            [Scalar(DataKind.String)],
            ArrayOfNamed("ScoredToken"),
            "Text → per-token labels (NER, POS tagging)."),
        new("TextPairScorer", TaskFamily.Text,
            [Scalar(DataKind.String), Scalar(DataKind.String)],
            Scalar(DataKind.Float32),
            "Two texts → similarity score (cross-encoder sentence similarity)."),
        new("TextReranker", TaskFamily.Text,
            [Scalar(DataKind.String), ArrayOf(DataKind.String)],
            ArrayOfNamed("ScoredIndex"),
            "Query + candidate texts → reranked indices with scores."),
        new("TextGenerator", TaskFamily.Text,
            [Scalar(DataKind.String)],
            Scalar(DataKind.String),
            "Text → text. LLM completion."),
        new("Translator", TaskFamily.Text,
            [Scalar(DataKind.String)],
            Scalar(DataKind.String),
            "Text → translated text."),
        new("TextSummarizer", TaskFamily.Text,
            [Scalar(DataKind.String)],
            Scalar(DataKind.String),
            "Text → summary."),
        new("TextEditor", TaskFamily.Text,
            [Scalar(DataKind.String), Scalar(DataKind.String)],
            Scalar(DataKind.String),
            "Text + instruction → edited text (instruction-tuned LLM)."),

        // ─── image: classification + embedding + tagging ────────────────
        new("ImageClassifier", TaskFamily.Image,
            [Scalar(DataKind.Image)],
            Named("ScoredClass"),
            "Image → single class with confidence."),
        new("LabeledImageClassifier", TaskFamily.Image,
            [Scalar(DataKind.Image)],
            Named("ScoredLabel"),
            "Image → single class-label string with confidence. "
            + "Like ImageClassifier but emits a human-readable label (e.g. 'tabby cat') "
            + "instead of an integer class id."),
        new("ImageMultiClassifier", TaskFamily.Image,
            [Scalar(DataKind.Image)],
            ArrayOfNamed("ScoredClass"),
            "Image → multiple classes with confidence (multi-label / top-k)."),
        new("BinaryImageClassifier", TaskFamily.Image,
            [Scalar(DataKind.Image)],
            Named("BinaryScore"),
            "Image → boolean classification (NSFW / defect / pass-fail)."),
        new("ImageTagger", TaskFamily.Image,
            [Scalar(DataKind.Image)],
            ArrayOfNamed("ScoredLabel"),
            "Image → open-vocabulary tags with scores."),
        new("ImageEmbedder", TaskFamily.Image,
            [Scalar(DataKind.Image)],
            ArrayOf(DataKind.Float32),
            "Image → vector embedding. L2-normalized by convention."),
        new("ImageCaptioner", TaskFamily.Image,
            [Scalar(DataKind.Image)],
            Scalar(DataKind.String),
            "Image → descriptive caption."),

        // ─── image: detection + segmentation ────────────────────────────
        new("ObjectDetector", TaskFamily.Image,
            [Scalar(DataKind.Image)],
            ArrayOfNamed("ScoredDetection"),
            "Image → bounding boxes with classes + confidences."),
        new("LabeledObjectDetector", TaskFamily.Image,
            [Scalar(DataKind.Image)],
            ArrayOfNamed("LabeledDetection"),
            "Image → bounding boxes with class-label strings + confidences. "
            + "Like ObjectDetector but emits human-readable labels (e.g. 'person', 'car') "
            + "instead of integer class ids."),
        new("RegionLocalizer", TaskFamily.Image,
            [Scalar(DataKind.Image)],
            ArrayOfNamed("RegionScore"),
            "Image → region bounding boxes without classes."),
        new("TextDetector", TaskFamily.Image,
            [Scalar(DataKind.Image)],
            ArrayOfNamed("RegionScore"),
            "Image → text region bounding boxes (no recognition)."),
        new("TextRecognizer", TaskFamily.Image,
            [Scalar(DataKind.Image)],
            Scalar(DataKind.String),
            "Image (single text line crop) → recognized text."),
        new("TextOCR", TaskFamily.Image,
            [Scalar(DataKind.Image)],
            ArrayOfNamed("OcrLine"),
            "Image → text bounding boxes with recognized text (end-to-end OCR)."),
        new("FaceDetector", TaskFamily.Image,
            [Scalar(DataKind.Image)],
            ArrayOfNamed("FaceDetection"),
            "Image → face bounding boxes with landmarks + confidence."),
        new("KeypointDetector", TaskFamily.Image,
            [Scalar(DataKind.Image)],
            ArrayOfNamed("Keypoint"),
            "Image → 2D keypoints (pose, landmarks) with confidence."),
        new("SemanticSegmenter", TaskFamily.Image,
            [Scalar(DataKind.Image)],
            Scalar(DataKind.Image),
            "Image → per-pixel class label as grayscale Image."),
        new("InstanceSegmenter", TaskFamily.Image,
            [Scalar(DataKind.Image)],
            ArrayOfNamed("ScoredMask"),
            "Image → per-instance masks with classes + confidence."),
        new("PointSegmenter", TaskFamily.Image,
            [Scalar(DataKind.Image), Scalar(DataKind.Point2D)],
            Scalar(DataKind.Image),
            "Image + point prompt → segmentation mask (SAM-style)."),
        new("BoxSegmenter", TaskFamily.Image,
            [Scalar(DataKind.Image), Named("BoundingBox")],
            Scalar(DataKind.Image),
            "Image + box prompt → segmentation mask (SAM-style)."),
        new("BackgroundRemover", TaskFamily.Image,
            [Scalar(DataKind.Image)],
            Scalar(DataKind.Image),
            "Image → foreground with transparent background. "
            + "Salient-object binary segmenter produces the alpha mask."),
        new("DepthEstimator", TaskFamily.Image,
            [Scalar(DataKind.Image)],
            Scalar(DataKind.Image),
            "Image → per-pixel depth as grayscale Image."),
        new("DepthEstimatorMetric", TaskFamily.Image,
            [Scalar(DataKind.Image)],
            ArrayOf(DataKind.Float32),
            "Image → per-pixel **metric** depth as a shape-aware Float32 array. "
                + "Bigger value = farther (units: meters for ZoeDepth / GLPN-NYU)."),
        new("SurfaceNormalEstimator", TaskFamily.Image,
            [Scalar(DataKind.Image)],
            Scalar(DataKind.Image),
            "Image → per-pixel surface normals encoded as an RGB Image "
            + "(R/G/B channels store nx/ny/nz, remapped from [-1, 1] to [0, 255]). "
            + "The geometric complement to DepthEstimator — depth gives position, "
            + "normals give orientation."),
        new("StereoDepthEstimator", TaskFamily.Image,
            [Scalar(DataKind.Image), Scalar(DataKind.Image)],
            Scalar(DataKind.Image),
            "Rectified stereo pair (left, right) → disparity / depth map. "
            + "Geometric (not learned-prior) so absolute scale is recoverable "
            + "from the known baseline."),

        // ─── image: 3D reconstruction ───────────────────────────────────
        new("MeshFromImage", TaskFamily.Image,
            [Scalar(DataKind.Image)],
            Scalar(DataKind.Mesh),
            "Single image → 3D triangle Mesh. The model hallucinates the "
            + "back / occluded surfaces of the depicted object (complementing "
            + "DepthEstimator + mesh_from_depth_*, which only capture what the "
            + "camera sees). Output is a watertight mesh ready for browser "
            + "rendering or 3D printing. Triplane / NeRF architectures (TripoSR, "
            + "SF3D, InstantMesh, Hunyuan3D) all fit this contract."),

        // ─── image: generation + transformation ─────────────────────────
        new("ImageUpscaler", TaskFamily.Image,
            [Scalar(DataKind.Image)],
            Scalar(DataKind.Image),
            "Image → higher-resolution Image (super-resolution)."),
        new("ImageRestorer", TaskFamily.Image,
            [Scalar(DataKind.Image)],
            Scalar(DataKind.Image),
            "Image → restored Image (denoise / dehaze / deblur)."),
        new("ImageColorizer", TaskFamily.Image,
            [Scalar(DataKind.Image)],
            Scalar(DataKind.Image),
            "Grayscale Image → colorized Image."),
        new("ImageStyleTransfer", TaskFamily.Image,
            [Scalar(DataKind.Image), Scalar(DataKind.Image)],
            Scalar(DataKind.Image),
            "Content image + style image → stylized image."),
        new("ImageEditor", TaskFamily.Image,
            [Scalar(DataKind.Image), Scalar(DataKind.String)],
            Scalar(DataKind.Image),
            "Image + edit instruction → edited image."),

        // ─── audio ──────────────────────────────────────────────────────
        new("AudioClassifier", TaskFamily.Audio,
            [Scalar(DataKind.Audio)],
            Named("ScoredClass"),
            "Audio → single class with confidence (sound event detection)."),
        new("AudioMultiClassifier", TaskFamily.Audio,
            [Scalar(DataKind.Audio)],
            ArrayOfNamed("ScoredClass"),
            "Audio → multiple classes with confidence."),
        new("AudioEmbedder", TaskFamily.Audio,
            [Scalar(DataKind.Audio)],
            ArrayOf(DataKind.Float32),
            "Audio → vector embedding."),
        new("AudioToText", TaskFamily.Audio,
            [Scalar(DataKind.Audio)],
            Scalar(DataKind.String),
            "Audio → transcribed text."),
        new("AudioToTextTimed", TaskFamily.Audio,
            [Scalar(DataKind.Audio)],
            ArrayOfNamed("TimedText"),
            "Audio → transcribed text with word-level timing."),
        new("TextToAudio", TaskFamily.Audio,
            [Scalar(DataKind.String)],
            Scalar(DataKind.Audio),
            "Text → speech audio (TTS)."),
        new("VoiceCloner", TaskFamily.Audio,
            [Scalar(DataKind.Audio), Scalar(DataKind.String)],
            Scalar(DataKind.Audio),
            "Voice sample + text → speech in that voice."),
        new("AudioRestorer", TaskFamily.Audio,
            [Scalar(DataKind.Audio)],
            Scalar(DataKind.Audio),
            "Audio → restored audio (denoise / dereverb / enhance)."),
        new("VoiceActivityDetector", TaskFamily.Audio,
            [Scalar(DataKind.Audio)],
            ArrayOf(DataKind.Float32),
            "Audio → per-frame voice/no-voice probability vector. "
            + "Standard preprocessor in front of ASR pipelines — emit one "
            + "score per fixed-size frame (e.g. 30 ms at 16 kHz)."),

        // ─── video ──────────────────────────────────────────────────────
        new("VideoClassifier", TaskFamily.Video,
            [Scalar(DataKind.Video)],
            Named("ScoredClass"),
            "Video → single class with confidence (action recognition)."),
        new("VideoSegmentClassifier", TaskFamily.Video,
            [Scalar(DataKind.Video)],
            ArrayOfNamed("TimedClass"),
            "Video → temporally-localized classes (temporal action localization)."),
        new("VideoEmbedder", TaskFamily.Video,
            [Scalar(DataKind.Video)],
            ArrayOf(DataKind.Float32),
            "Video → vector embedding."),

        // ─── multimodal ─────────────────────────────────────────────────
        new("VisualQA", TaskFamily.Multimodal,
            [Scalar(DataKind.Image), Scalar(DataKind.String)],
            Scalar(DataKind.String),
            "Image + question → answer (VLM)."),
        new("ImageTextSimilarity", TaskFamily.Multimodal,
            [Scalar(DataKind.Image), Scalar(DataKind.String)],
            Scalar(DataKind.Float32),
            "Image + text → similarity score (CLIP-style)."),
        new("ImageTextEmbedder", TaskFamily.Multimodal,
            [Scalar(DataKind.Image), Scalar(DataKind.String)],
            Named("DualEmbedding"),
            "Image + text → joint pair of embeddings (image + text). "
            + "CLIP-style dual encoder where downstream code consumes the two "
            + "vectors directly (cross-modal search, zero-shot classification)."),
        new("ZeroShotImageClassifier", TaskFamily.Multimodal,
            [Scalar(DataKind.Image), ArrayOf(DataKind.String)],
            ArrayOfNamed("ScoredClass"),
            "Image + candidate labels → scored class picks (CLIP zero-shot)."),
        new("ZeroShotObjectDetector", TaskFamily.Multimodal,
            [Scalar(DataKind.Image), ArrayOf(DataKind.String)],
            ArrayOfNamed("LabeledDetection"),
            "Image + candidate labels → labeled bounding boxes (OWL-ViT / GroundingDINO)."),
        new("TextToImage", TaskFamily.Multimodal,
            [Scalar(DataKind.String)],
            Scalar(DataKind.Image),
            "Text prompt → generated image."),
        new("ImageToImage", TaskFamily.Multimodal,
            [Scalar(DataKind.Image), Scalar(DataKind.String)],
            Scalar(DataKind.Image),
            "Image + prompt → transformed image (diffusion img2img)."),

        // ─── structured (tabular / time series) ─────────────────────────
        new("TabularClassifier", TaskFamily.Structured,
            [ArrayOf(DataKind.Float32)],
            Named("ScoredClass"),
            "Feature vector → single class with confidence."),
        new("TabularRegressor", TaskFamily.Structured,
            [ArrayOf(DataKind.Float32)],
            Scalar(DataKind.Float32),
            "Feature vector → continuous prediction."),
        new("TimeSeriesClassifier", TaskFamily.Structured,
            [ArrayOf(DataKind.Float32)],
            Named("ScoredClass"),
            "Time series → single class with confidence."),
        new("TimeSeriesForecaster", TaskFamily.Structured,
            [ArrayOf(DataKind.Float32)],
            ArrayOf(DataKind.Float32),
            "Time series → forecasted values."),
        new("TimeSeriesAnomalyDetector", TaskFamily.Structured,
            [ArrayOf(DataKind.Float32)],
            Scalar(DataKind.Float32),
            "Time series → anomaly score."),
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
