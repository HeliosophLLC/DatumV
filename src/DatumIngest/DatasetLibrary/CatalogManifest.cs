// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

using System.Collections.Generic;
using System.Text.Json.Serialization;

using DatumIngest.ModelLibrary;

namespace DatumIngest.DatasetLibrary;

// POCOs for datasets/catalog.json.
//
// The schema models the conceptual hierarchy directly: a DatasetEntry is
// the user-facing identity (e.g. "COCO 2017"), and it owns one or more
// DatasetVariants — the install-handle-bearing cuts (e.g. test2017,
// val2017). Entry-level fields (summary, license, attribution, card,
// hero) describe the family as a whole; variant-level fields (id,
// sources, ingest jobs, sizes) describe the specific cut the user
// downloads. Install / uninstall / probe still key on variant id so the
// orchestrator's wire surface doesn't change.
//
// CatalogLicense + CatalogSource (HuggingFaceSource / GithubReleaseSource /
// HttpsSource) are imported verbatim from DatumIngest.ModelLibrary — those
// types are agnostic to whether the consumer is fetching weights or data.

public sealed record DatasetCatalogManifest(
    int SchemaVersion,
    IReadOnlyList<DatasetEntry> Datasets);

public sealed record DatasetEntry(
    // Entry identity. Doubles as the URL-routable key for the card +
    // hero + asset endpoints (URL-encoded by callers) and as the
    // human-readable title shown in the list row. Must be unique
    // across the manifest.
    string Name,
    // Plain-English headline for the list row and the detail card.
    string Summary,
    // Long-form prose body for the detail card. The family card
    // markdown (when present) is preferred over this for rendering;
    // Description is the fallback when no card file ships.
    string Description,
    // Modality vocabulary (HF-style: Image / Text / Audio / Video /
    // Tabular / 3D / Geospatial / Document / TimeSeries). Drives the
    // sidebar facet in the Datasets browser. Intrinsic to the data —
    // every variant shares the same modalities. Required + non-empty;
    // validated against the canonical set in
    // <see cref="ModalityRegistry.Canonical"/> at load time.
    IReadOnlyList<string> Modalities,
    IReadOnlyList<string> LicenseIds,
    // Copyright holders / contributors the upstream license requires us
    // to credit. Order is primary author first, then derivative
    // contributors. Surfaced on the detail card.
    IReadOnlyList<string> Attributions,
    // Optional "suitable for" hint — engine task contracts (referencing
    // <see cref="DatumIngest.Catalog.Registries.TaskTypeRegistry"/>)
    // this dataset commonly feeds. Renders as a chip strip on the
    // detail card so users searching "what can I train with this?"
    // see the bridge from data to model. Not exhaustive on purpose —
    // it's a discovery aid, not a contract. Validated at load:
    // unknown task names fail loudly.
    IReadOnlyList<string>? SuitableForTasks,
    // One or more install-handle-bearing cuts. The detail pane renders a
    // tab strip with one chip per variant when this list has more than
    // one element; a singleton variant is rendered without the tab
    // strip.
    IReadOnlyList<DatasetVariant> Variants,
    // SQL schema the installed variants bind into. Default `datasets` so
    // the common case reads as `datasets.<variantId>`; per-entry override
    // lets authors spread very large catalogs across multiple schemas
    // (e.g. `coco.train2017`, `image_classification.imagenet`) when they
    // outgrow one namespace. Validated as a single SQL identifier at
    // load time.
    string Schema = "datasets",
    // Optional path (relative to the manifest directory) to a markdown
    // body describing the entry. The renderer uses ReactMarkdown with a
    // URL-rewrite that points relative asset references at the
    // entry-asset endpoint, so authors can drop screenshots under
    // `cards/<name>/` and reference them as `<name>/screenshot.png`.
    string? CardFile = null,
    // Optional path (relative to the manifest directory) to a hero
    // image displayed at the top of the detail card. Lives as a
    // structured field rather than inline markdown so the renderer
    // owns positioning/aspect/crop. Missing files are tolerated
    // (warning logged); the renderer omits the hero band when absent.
    string? HeroImageFile = null);

public sealed record DatasetVariant(
    // SQL-resolvable identifier (e.g. `coco_test2017`). The install
    // service keys on this; the user references it as
    // `<schema>.<id>` (default schema `datasets`) in queries. Must be
    // unique across the manifest, not just within the parent entry, and
    // must be a valid SQL identifier (snake_case, no hyphens) since it
    // doubles as the bound table name.
    string Id,
    // Variant-specific subtitle. Shown on the variant tab and the
    // active-variant header in the detail pane (e.g. "test2017
    // (images)").
    string DisplayName,
    // Per-variant blurb shown when this variant is the active tab. The
    // entry-level Summary remains the headline. Optional — when
    // omitted, the detail pane shows only the variant displayName +
    // size badge.
    string? Summary,
    // Approximate total bytes of the raw archive(s) the user downloads
    // before extraction.
    long ApproxArchiveBytes,
    // Approximate total bytes of the ingested `.datum` (+ sidecar)
    // payload that lands in the catalog.
    long ApproxIngestedBytes,
    // Per-table sanity-check counts. Cross-verified against the actual
    // row counts after ingest; mismatch fails the install before the
    // catalog substrate registers the views.
    IReadOnlyDictionary<string, long>? ExpectedRowCounts,
    // The primary source repo requires a logged-in HF token to
    // download. Independent of license acceptance.
    bool RequiresHfLogin,
    // Per-variant version history. Element [0] is the recommended cut.
    IReadOnlyList<CatalogDatasetVersion> Versions)
{
    /// <summary>
    /// Shorthand for <c>Versions[0].Sources</c>. The manifest validator
    /// guarantees <c>Versions</c> is non-empty.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<CatalogSource> Sources => Versions[0].Sources;

    /// <summary>
    /// Shorthand for <c>Versions[0].Ingest</c>.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<CatalogIngestJob> Ingest => Versions[0].Ingest;
}

// One immutable, dated cut of a variant. Mirrors CatalogVersion on the
// model side. Listed newest-first under DatasetVariant.Versions; element
// [0] is "current" and gets installed on fresh installs / install-from-
// modal.
public sealed record CatalogDatasetVersion(
    // Human-authored version identifier, opaque to the engine.
    string Version,
    // Ordered list of sources the downloader will try in sequence for
    // this cut. Same shape + fallback semantics as the model-side
    // sources field — the install service reuses the existing
    // IModelSourceClient implementations to fetch files.
    IReadOnlyList<CatalogSource> Sources,
    // Jobs the installer runs after the source files are downloaded.
    // Each job's `sourcePath` names a file (relative to the raw cache
    // root) that the engine's FormatRegistry hands off to a deserializer
    // — e.g. MediaBagDeserializer streams one row per archive entry, so a
    // homogeneous media archive (ZIP of images, TAR.GZ of FLACs, …) produces
    // a one-row-per-file `.datum` without any explicit extraction step. The
    // output lands at `<ingestedRoot>/<id>/<version>/<tableName>.datum`.
    IReadOnlyList<CatalogIngestJob> Ingest,
    // Per-version deprecation.
    bool Deprecated = false,
    string? DeprecationReason = null);

// One ingest job inside a CatalogDatasetVersion. SourcePath is a path
// relative to the version's raw cache root pointing at the source the
// engine's FormatRegistry handles directly. TableName is the file stem
// of the produced `.datum`.
public sealed record CatalogIngestJob(
    string SourcePath,
    string TableName);
