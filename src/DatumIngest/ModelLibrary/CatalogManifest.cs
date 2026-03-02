// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

using System.Collections.Generic;

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
    CatalogSource Source,
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

public sealed record CatalogSource(
    // Today: only "huggingface". Future could add "github-release" etc.
    string Type,
    string Repo,
    // Either "main" or a 40-char commit sha. Pinned shas give reproducibility;
    // "main" is the v1 default while the catalog is hand-maintained.
    string Revision,
    IReadOnlyList<string> Include);
