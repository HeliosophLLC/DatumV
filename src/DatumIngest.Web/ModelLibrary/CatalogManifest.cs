using System.Collections.Generic;

namespace DatumIngest.Web.ModelLibrary;

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
    CatalogHardware Hardware,
    CatalogSource Source,
    int ApproxSizeMb,
    // Marks entries whose Source.Repo is not yet uploaded. The downloader
    // refuses to install placeholders so a half-finished upload doesn't
    // surface as a broken model.
    bool Placeholder = false,
    // The HF source repo requires a logged-in token to download (gated
    // repos like Meta-Llama). Independent of license acceptance.
    bool RequiresHfLogin = false);

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
