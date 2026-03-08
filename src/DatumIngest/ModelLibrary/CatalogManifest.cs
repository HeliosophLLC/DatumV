// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DatumIngest.ModelLibrary;

// POCOs for models/catalog.json. Shape matches the JSON exactly so the
// same types double as API DTOs (NSwag emits matching TypeScript). Keep
// nullability tight — the manifest is authored by hand and surface-area
// errors should fail loudly on load.

public sealed record CatalogManifest(
    int SchemaVersion,
    IReadOnlyDictionary<string, CatalogLicense> Licenses,
    CatalogTiers Tiers,
    IReadOnlyList<CatalogModel> Models);

public sealed record CatalogLicense(
    string Title,
    string Spdx,
    string CanonicalUrl,
    string TextFile,
    string Summary,
    bool RequiresAcceptance);

public sealed record CatalogTiers(
    IReadOnlyList<string> Starter,
    IReadOnlyList<string> Recommended);

public sealed record CatalogModel(
    string Id,
    string DisplayName,
    string Description,
    string Task,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> LicenseIds,
    // Copyright holders / contributors the upstream license requires us
    // to credit. Order is primary author first, then derivative
    // contributors (fine-tuner, quantizer, ONNX exporter). Surfaced in
    // the model card; HF READMEs inherit from this list.
    IReadOnlyList<string> Attributions,
    CatalogHardware Hardware,
    // Ordered list of sources the downloader will try in sequence. The first
    // source that can list + deliver every expected file wins. Subsequent
    // entries are fallbacks tried on per-source failure (404, network, hash
    // mismatch, partial file inventory). Policy is "Heliosoph-mirror first":
    // the pinned-sha Heliosoph mirror leads for reproducibility, and (when
    // present) the upstream channel trails as a hedge against mirror
    // unavailability. Always at least one entry.
    IReadOnlyList<CatalogSource> Sources,
    int ApproxSizeMb,
    // Marks entries whose Source.Repo is not yet uploaded. The downloader
    // refuses to install placeholders so a half-finished upload doesn't
    // surface as a broken model.
    bool Placeholder = false,
    // The HF source repo requires a logged-in token to download (gated
    // repos like Meta-Llama). Independent of license acceptance.
    bool RequiresHfLogin = false,
    // Path (relative to the catalog.json directory) to a .sql file that
    // registers this model into the SQL catalog after download completes.
    // The file may contain one or more CREATE MODEL statements; the
    // probe convention is that the SQL registers a model whose qualified
    // name is `public.<Id with '-' replaced by '_'>` — that name is what
    // the installer looks up to decide "installed vs only downloaded."
    // Additional CREATE MODEL statements in the same file are registered
    // for the model's own use (sub-models, helpers) but aren't tracked
    // independently by the catalog. Null for models that need no SQL
    // glue (today: most models are still consumed via the built-in IModel
    // path and don't need an installSql entry).
    string? InstallSql = null);

public sealed record CatalogHardware(
    int MinRamMb,
    int MinVramMb,
    // Free-form for now: "cpu" | "gpu" | "any" — informational, not enforced.
    string Preferred);

// Discriminated union over source channels. JSON wire form carries a
// `type` discriminator; STJ resolves the right concrete subtype per entry.
// New source kinds are added by:
//   1. Declaring a record deriving from CatalogSource,
//   2. Adding it to the [JsonDerivedType] list below,
//   3. Registering an IModelSourceClient implementation whose
//      SupportedType returns the same discriminator string.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HuggingFaceSource), "huggingface")]
[JsonDerivedType(typeof(GithubReleaseSource), "github-release")]
[JsonDerivedType(typeof(HttpsSource), "https")]
public abstract record CatalogSource;

// Files come from a HuggingFace Hub model repo at the given revision.
// `Include` is a glob list filtered against the tree API's file listing —
// most catalog entries pin one or two files; whole-repo entries (SD
// bundles) use ["**/*"].
public sealed record HuggingFaceSource(
    string Repo,
    // Either "main" or a 40-char commit sha. Pinned shas give
    // reproducibility; "main" is acceptable for placeholder entries that
    // haven't been uploaded yet — the downloader treats `placeholder: true`
    // on the parent CatalogModel as the gate.
    string Revision,
    IReadOnlyList<string> Include) : CatalogSource;

// Files come from a GitHub release. `Repo` is "owner/name"; `Tag` is the
// release tag (e.g. "v0.0.0"); `Files` is the literal asset filename list
// (no globs — GitHub releases are flat). No hash verification beyond
// HTTPS — GitHub releases don't surface a per-asset checksum API.
public sealed record GithubReleaseSource(
    string Repo,
    string Tag,
    IReadOnlyList<string> Files) : CatalogSource;

// Files come from one-off HTTPS URLs (Qualcomm AI Hub S3, custom mirrors,
// etc.). Each `HttpsFile` carries the full URL and the destination filename
// (relative to the model directory). No hash verification beyond HTTPS.
public sealed record HttpsSource(
    IReadOnlyList<HttpsFile> Urls) : CatalogSource;

public sealed record HttpsFile(string Url, string DestFile);
