// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Heliosoph.DatumV.ModelLibrary;

// POCOs for models/catalog.json. Mirrors the entry/variant hierarchy on
// the datasets side: a CatalogEntry is the user-facing identity
// (e.g. "YOLOX", "Florence-2 base-ft"), and owns one or more
// CatalogVariants — the install-handle-bearing cuts (e.g. yolox-s,
// yolox-nano; florence-2-base-ft-fp16, florence-2-base-ft-quantized).
// Entry-level fields (summary, description, tasks, attribution, card,
// hero) describe the family as a whole; variant-level fields (id,
// hardware, install sources, sizes, versions) describe the specific cut
// the user downloads. Install / uninstall / probe still key on variant
// id so the orchestrator's wire surface keys on one stable globally-
// unique handle.

public sealed record CatalogManifest(
    int SchemaVersion,
    IReadOnlyList<CatalogEntry> Entries);

public sealed record CatalogLicense(
    string Title,
    string Spdx,
    string CanonicalUrl,
    string TextFile,
    string Summary,
    bool RequiresAcceptance);

public sealed record CatalogEntry(
    // Entry identity. Doubles as the URL-routable key for the card +
    // hero + asset endpoints (URL-encoded by callers) and as the
    // human-readable title shown in the list row. Must be unique
    // across the manifest.
    string Name,
    // Plain-English headline for the list row and the detail card.
    string Summary,
    // Long-form prose body for the detail card. The entry card markdown
    // (when present) is preferred over this for rendering;
    // Description is the fallback when no card file ships.
    string Description,
    // Task contracts variants of this entry implement, by name from
    // <see cref="Heliosoph.DatumV.Catalog.Registries.TaskTypeRegistry"/>.
    // Validated at catalog-load time — unknown names fail loudly. Order is
    // "primary use first" so single-task consumers reading `Tasks[0]` get
    // the entry's main role.
    IReadOnlyList<string> Tasks,
    // Free-form tag vocabulary used by the model browser's filter chips.
    // Entry-level tags describe the family ("embeddings", "vlm",
    // "detection"); per-axis tags (fp16/int8, edge/gpu) live on each
    // variant.
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> LicenseIds,
    // Copyright holders / contributors the upstream license requires us
    // to credit. Order is primary author first, then derivative
    // contributors (fine-tuner, quantizer, ONNX exporter). Surfaced in
    // the model card; HF READMEs inherit from this list.
    IReadOnlyList<string> Attributions,
    // One or more install-handle-bearing cuts. The detail pane renders a
    // tab strip with one chip per variant when this list has more than
    // one element; a singleton variant is rendered without the tab
    // strip.
    IReadOnlyList<CatalogVariant> Variants,
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

public sealed record CatalogVariant(
    // Globally-unique install handle. The downloader / installer /
    // residency manager all key on this. Must be a stable string the
    // user references in queries and dialogs (e.g. `yolox-s`,
    // `florence-2-base-ft-fp16`). Validated unique across the whole
    // manifest at load time.
    string Id,
    // Variant-specific subtitle shown on the variant tab strip and the
    // active-variant header in the detail pane (e.g. "S (recommended)",
    // "fp16 (GPU)", "INT8 (CPU)"). When the entry only has one variant,
    // the renderer omits the tab strip and shows just this label.
    string DisplayName,
    // Per-variant blurb. The entry-level Summary remains the headline.
    // Optional — when omitted, the detail pane shows only the variant
    // displayName + size badge.
    string? Summary,
    // Free-form variant-axis tags (fp16, int8, edge, gpu, cpu). Used by
    // the renderer for badge chips alongside the variant displayName.
    IReadOnlyList<string> Tags,
    CatalogHardware Hardware,
    int ApproxSizeMb,
    // Per-variant version history. Element [0] is the recommended
    // version — fresh installs and install-from-modal always pick [0].
    // Older entries are kept so users can revert; per-version
    // deprecation marks specific cuts as known-broken. Always at
    // least one entry; element [0] may not be entry-level deprecated
    // (validated at load).
    IReadOnlyList<CatalogVersion> Versions,
    // Marks variants whose primary source is not yet uploaded. The
    // downloader refuses to install placeholders so a half-finished
    // upload doesn't surface as a broken model.
    bool Placeholder = false,
    // The source repo requires a logged-in token to download (gated
    // repos like Meta-Llama). Independent of license acceptance.
    bool RequiresHfLogin = false,
    // Discriminator over what the engine does after files are downloaded.
    // Default "onnx" means "files-only install + optional installSql
    // registration." "python" means the variant also requires a managed
    // Python venv + a worker script; the engine sets up the venv via
    // PythonEnvironmentManager and registers a PythonBackedModel rather
    // than running installSql.
    string Kind = "onnx",
    // Python-specific install + dispatch config. Required when Kind ==
    // "python"; must be null otherwise.
    CatalogPythonSpec? Python = null,
    // Variant-level deprecation: this whole cut is superseded by
    // something newer. Pre-flight surfaces this with the
    // <see cref="SupersededBy"/> pointer when present. `versions[0]`
    // of a deprecated variant remains installable so existing pinned
    // queries keep working.
    bool Deprecated = false,
    // Optional variant id pointer to the successor variant, surfaced
    // alongside <see cref="Deprecated"/>.
    string? SupersededBy = null)
{
    /// <summary>
    /// Shorthand for <c>Versions[0].Sources</c>. The manifest validator
    /// guarantees <c>Versions</c> is non-empty.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<CatalogSource> Sources => Versions[0].Sources;

    /// <summary>
    /// Shorthand for <c>Versions[0].InstallSql</c>.
    /// </summary>
    [JsonIgnore]
    public string? InstallSql => Versions[0].InstallSql;
}

// One immutable, dated cut of a catalog variant. Versions are listed
// newest-first under CatalogVariant.Versions; element [0] is "current"
// and gets installed on fresh installs / install-from-modal. Per-version
// deprecation marks specific cuts as known-broken without retiring the
// variant as a whole.
public sealed record CatalogVersion(
    // Human-authored version identifier, opaque to the engine. The
    // memo recommends `YYYY-MM-DD` (or `YYYYMMDD` for pin syntax)
    // but any unique-per-variant string is acceptable.
    string Version,
    // Ordered list of sources the downloader will try in sequence
    // for this cut.
    IReadOnlyList<CatalogSource> Sources,
    // Path (relative to the catalog.json directory) to the .sql file
    // that registers this cut's model identifiers. Lives under the
    // per-variant folder `sql/<variant-id>/<version>.sql` so older
    // versions stay byte-stable when a new cut lands.
    string? InstallSql = null,
    // Declared set of model identifiers this cut's installSql registers.
    // Source of truth for version-switch DROP/CREATE OR REPLACE
    // accounting. Cross-checked against actual registrations at install
    // time — mismatch fails the install before <id>/active flips.
    IReadOnlyList<CatalogVersionModel>? Models = null,
    // Per-version deprecation: this specific cut has a known bug; the
    // variant itself is still good. Pre-flight surfaces the reason
    // when a query pins this version.
    bool Deprecated = false,
    string? DeprecationReason = null);

// One declared model identifier inside a <see cref="CatalogVersion"/>.
// Carries both the bare identifier the installSql registers when this
// is the active version (e.g. <c>foo</c>) and the suffixed identifier
// the installer rewrites to when this version is installed alongside
// a different active version via the `@&lt;version&gt;` pin syntax
// (e.g. <c>foo@20260529</c>).
//
// <see cref="PinnedAs"/> is optional in JSON; when omitted the engine
// materialises the default <c>&lt;identifier&gt;@&lt;digits-of-version&gt;</c>
// at catalog load time.
public sealed record CatalogVersionModel(
    string Identifier,
    string? PinnedAs = null)
{
    public string EffectivePinnedAs(string versionString)
    {
        if (!string.IsNullOrEmpty(PinnedAs)) { return PinnedAs; }
        Span<char> digits = stackalloc char[versionString.Length];
        int n = 0;
        for (int i = 0; i < versionString.Length; i++)
        {
            char c = versionString[i];
            if (c >= '0' && c <= '9') { digits[n++] = c; }
        }
        return $"{Identifier}@{digits[..n].ToString()}";
    }
}

// ───────────────────────── Python-backed model config ─────────────────────────

public sealed record CatalogPythonSpec(
    string WorkerScript,
    string PythonVersion,
    IReadOnlyList<string> Requirements,
    CatalogModelSignature Signature,
    IReadOnlyList<string>? ScaffoldArgs = null);

public sealed record CatalogModelSignature(
    IReadOnlyList<string> InputKinds,
    string OutputKind,
    bool IsDeterministic,
    IReadOnlyList<string>? OptionalArgKinds = null);

public sealed record CatalogHardware(
    int MinRamMb,
    int MinVramMb,
    string Preferred);

// Discriminated union over source channels. JSON wire form carries a
// `type` discriminator; STJ resolves the right concrete subtype per entry.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HuggingFaceSource), "huggingface")]
[JsonDerivedType(typeof(GithubReleaseSource), "github-release")]
[JsonDerivedType(typeof(HttpsSource), "https")]
public abstract record CatalogSource;

public sealed record HuggingFaceSource(
    string Repo,
    string Revision,
    IReadOnlyList<string> Include,
    string RepoType = "model") : CatalogSource;

public sealed record GithubReleaseSource(
    string Repo,
    string Tag,
    IReadOnlyList<string> Files) : CatalogSource;

public sealed record HttpsSource(
    IReadOnlyList<HttpsFile> Urls) : CatalogSource;

public sealed record HttpsFile(string Url, string DestFile);
