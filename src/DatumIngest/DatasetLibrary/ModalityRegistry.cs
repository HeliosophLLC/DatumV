// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member

namespace DatumIngest.DatasetLibrary;

// Canonical modality vocabulary for dataset entries. Modeled on
// HuggingFace's dataset facet — modality is intrinsic to the data
// ("this dataset contains images") whereas tasks are extrinsic ("you
// could train an object detector with it"). Datasets carry one or more
// modalities; the Datasets browser surfaces them as a left-rail sidebar
// filter the same way the Models browser surfaces task contracts.
//
// Adding a modality: append to <see cref="Canonical"/>. Authors
// reference the verbatim name in catalog.json under
// <c>modalities[]</c>. Case-insensitive match at load time. The
// frontend's i18n bundle resolves each name to a display label.
public static class ModalityRegistry
{
    /// <summary>
    /// Canonical modality names, in display order. Matches the
    /// HuggingFace facet vocabulary so authors familiar with HF land
    /// without re-learning labels. Comparison is case-insensitive at
    /// load time, but the canonical form (used in JSON authoring) is
    /// PascalCase.
    /// </summary>
    public static readonly IReadOnlyList<string> Canonical =
    [
        "Image",
        "Text",
        "Audio",
        "Video",
        "Tabular",
        "3D",
        "Geospatial",
        "Document",
        "TimeSeries",
    ];

    private static readonly HashSet<string> CanonicalLookup =
        new(Canonical, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="name"/> matches a canonical modality
    /// (case-insensitive).
    /// </summary>
    public static bool IsKnown(string name) => CanonicalLookup.Contains(name);
}
