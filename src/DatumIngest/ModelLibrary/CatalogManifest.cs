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
    IReadOnlyList<CatalogModel> Models,
    // Top-level per-task recommendation map: contract name (matching a
    // `TaskTypeRegistry` entry) → recommended model identifier (the
    // SQL-visible snake_case name registered by some catalog entry's
    // installSql). Powers the `tasks.<contract>(...)` dispatcher. Empty
    // (or absent in JSON) when no recommendations are wired yet — every
    // `tasks.x` call then fails preflight with "no recommended model
    // for contract X."
    CatalogTaskRecommendations? Tasks = null);

public sealed record CatalogTaskRecommendations(
    // contract name → recommended model identifier
    IReadOnlyDictionary<string, string> Recommended);

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
    // Plain-English "what does this do and why would I use it" summary —
    // the headline shown above the technical Description on the model
    // card. Authored for non-experts; complements (not replaces) the more
    // technical Description below. Required so the front-end never has to
    // fall back on Description for the headline copy.
    string Summary,
    string Description,
    // Task contracts this model implements, by name from
    // <see cref="DatumIngest.Catalog.Registries.TaskTypeRegistry"/>.
    // Most models implement exactly one; multi-task models (Florence-2,
    // CLIP, SAM) declare every contract they cover so the model browser's
    // task filter matches the model for each. Validated at catalog-load
    // time — unknown names fail loudly. Order is "primary use first" so
    // single-task consumers reading `Tasks[0]` get the model's main role.
    IReadOnlyList<string> Tasks,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> LicenseIds,
    // Copyright holders / contributors the upstream license requires us
    // to credit. Order is primary author first, then derivative
    // contributors (fine-tuner, quantizer, ONNX exporter). Surfaced in
    // the model card; HF READMEs inherit from this list.
    IReadOnlyList<string> Attributions,
    CatalogHardware Hardware,
    // Per-entry version history. Element [0] is the currently recommended
    // version — fresh installs and install-from-modal always pick [0].
    // Older entries are kept so users can revert; per-version
    // deprecation marks specific cuts as known-broken. Versioning
    // happens HERE rather than per-source so source + installSql + the
    // declared identifier set move together as one cut. Always at
    // least one entry; element [0] may not be entry-level deprecated
    // (validated at load).
    IReadOnlyList<CatalogVersion> Versions,
    int ApproxSizeMb,
    // Marks entries whose primary source is not yet uploaded. The
    // downloader refuses to install placeholders so a half-finished
    // upload doesn't surface as a broken model.
    bool Placeholder = false,
    // The source repo requires a logged-in token to download (gated
    // repos like Meta-Llama). Independent of license acceptance.
    bool RequiresHfLogin = false,
    // Discriminator over what the engine does after files are downloaded.
    // Default "onnx" means "files-only install + optional installSql
    // registration" — the v1 behaviour every existing entry expects.
    // "python" means the entry also requires a managed Python venv + a
    // worker script; the engine sets up the venv via
    // PythonEnvironmentManager and registers a PythonBackedModel rather
    // than running installSql. Other values may be added later (e.g.
    // "gguf") if their install behaviour ever diverges from the onnx
    // default; today GGUF entries are still "onnx" because they install
    // identically.
    string Kind = "onnx",
    // Python-specific install + dispatch config. Required when Kind ==
    // "python"; must be null otherwise. Carries the venv requirements,
    // worker script path, and the model's IModel-shaped signature so
    // catalog-driven registration can construct a PythonBackedModel
    // without needing an installSql sidecar.
    CatalogPythonSpec? Python = null,
    // Entry-level deprecation: the whole concept is superseded by
    // something newer. Pre-flight surfaces this with the
    // <see cref="SupersededBy"/> pointer when present. `versions[0]`
    // of a deprecated entry remains installable so existing pinned
    // queries keep working.
    bool Deprecated = false,
    // Optional catalog id pointer to the successor entry, surfaced
    // alongside <see cref="Deprecated"/>.
    string? SupersededBy = null)
{
    /// <summary>
    /// Shorthand for <c>Versions[0].Sources</c>. The catalog substrate
    /// guarantees <c>Versions</c> is non-empty (validated at load) and
    /// keeps the legacy "single source list per entry" API working for
    /// consumers that don't yet care about version history.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<CatalogSource> Sources => Versions[0].Sources;

    /// <summary>
    /// Shorthand for <c>Versions[0].InstallSql</c>. See <see cref="Sources"/>
    /// for the same compatibility rationale.
    /// </summary>
    [JsonIgnore]
    public string? InstallSql => Versions[0].InstallSql;
}

// One immutable, dated cut of a catalog entry. Versions are listed newest-
// first under CatalogModel.Versions; element [0] is "current" and gets
// installed on fresh installs / install-from-modal. Per-version
// deprecation marks specific cuts as known-broken without retiring the
// entry as a whole.
public sealed record CatalogVersion(
    // Human-authored version identifier, opaque to the engine. The
    // memo recommends `YYYY-MM-DD` (or `YYYYMMDD` for pin syntax)
    // but any unique-per-entry string is acceptable.
    string Version,
    // Ordered list of sources the downloader will try in sequence
    // for this cut. Same shape + fallback semantics as the
    // pre-versions[] top-level Sources field.
    IReadOnlyList<CatalogSource> Sources,
    // Path (relative to the catalog.json directory) to the .sql file
    // that registers this cut's model identifiers. Lives under the
    // per-entry folder `sql/<catalog-id>/<version>.sql` so older
    // versions stay byte-stable when a new cut lands.
    string? InstallSql = null,
    // Declared set of model identifiers this cut's installSql registers.
    // Source of truth for version-switch DROP/CREATE OR REPLACE
    // accounting and `tasks.recommended` validation. Cross-checked
    // against actual registrations at install time — mismatch fails
    // the install before <id>/active flips.
    IReadOnlyList<string>? Models = null,
    // Per-version deprecation: this specific cut has a known bug; the
    // entry itself is still good. Pre-flight surfaces the reason
    // when a query pins this version.
    bool Deprecated = false,
    string? DeprecationReason = null);

// ───────────────────────── Python-backed model config ─────────────────────────

// Install + dispatch config for a kind="python" model. Authored in
// catalog.json under the `python` key; loaded by the registrar at
// install time to materialise a venv + spawn the worker.
//
// Today WorkerScript is always a filename relative to the engine's
// bundled `python/` directory (the same place bark_worker.py etc.
// ship from). A future schema rev can extend the resolver to also
// accept HF URLs / model-relative paths once the user-defined Python
// model story lands.
public sealed record CatalogPythonSpec(
    string WorkerScript,
    string PythonVersion,
    IReadOnlyList<string> Requirements,
    CatalogModelSignature Signature,
    // CLI args appended after the worker script when the engine spawns
    // the subprocess (e.g. ["--model-id", "suno/bark-small"]). Same
    // shape as PythonBackedModel.scriptArgs. Empty when the worker
    // needs no scaffold args.
    IReadOnlyList<string>? ScaffoldArgs = null);

// Model signature mirroring the IModel-shaped fields that hardcoded
// registrations supply (InputKinds, OutputKind, IsDeterministic,
// OptionalArgKinds). Carried in catalog.json for kind="python"
// entries so the registrar can construct a ModelCatalogEntry without
// re-hardcoding these values.
//
// DataKind name strings are case-insensitive matches against the
// runtime enum — e.g. "String", "Image", "Audio", "Float64". The
// registrar throws ArgumentException at load time if any name is
// unrecognised so the manifest never silently mis-types a model.
public sealed record CatalogModelSignature(
    IReadOnlyList<string> InputKinds,
    string OutputKind,
    bool IsDeterministic,
    IReadOnlyList<string>? OptionalArgKinds = null);

public sealed record CatalogHardware(
    int MinRamMb,
    int MinVramMb,
    // The execution provider the model is optimized for. One of:
    // <list type="bullet">
    // <item><c>"cpu"</c> — works on ONNX Runtime's CPU EP; no accelerator
    //   required.</item>
    // <item><c>"cuda"</c> — NVIDIA GPU via ORT's CUDA EP. Catalog entries
    //   whose <i>weights</i> are CUDA-quantized (e.g. <c>onnxruntime-genai</c>
    //   CUDA INT4 builds) won't fall back to CPU; generic ONNX entries
    //   tagged "cuda" still run on CPU EP, just much slower.</item>
    // <item><c>"directml"</c> — Any DirectX 12 GPU (AMD / Intel / NVIDIA)
    //   via ORT's DirectML EP. Reserved — no catalog entries today.</item>
    // <item><c>"coreml"</c> — Apple Silicon / Mac via ORT's CoreML EP.
    //   Reserved — no catalog entries today.</item>
    // <item><c>"any"</c> — runs on whichever EP the host has; informational
    //   default when no provider is clearly preferred.</item>
    // </list>
    // Validated at catalog-load time. Aligns with the runtime
    // <see cref="DatumIngest.Inference.InferenceDevice"/> enum's EP names.
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
