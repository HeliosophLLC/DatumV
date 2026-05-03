using DatumIngest.DatasetLibrary;
using DatumIngest.ModelLibrary;

// ManifestStore exists in both DatumIngest.DatasetLibrary and
// DatumIngest.ModelLibrary; this file only exercises the dataset one.
using ManifestStore = DatumIngest.DatasetLibrary.ManifestStore;

namespace DatumIngest.Tests.DatasetLibrary;

/// <summary>
/// Exercises the cross-field invariants enforced at load time. Tests
/// call <see cref="ManifestStore.ValidateDatasets"/> directly with
/// hand-built manifests so each failure mode is one focused assertion
/// without disk I/O.
/// </summary>
public sealed class DatasetManifestValidationTests
{
    private const string ManifestPath = "<test>";

    [Fact]
    public void ValidManifest_Passes()
    {
        DatasetCatalogManifest manifest = BuildOneEntryManifest();
        ManifestStore.ValidateDatasets(manifest, ManifestPath);
    }

    [Fact]
    public void WrongSchemaVersion_Throws()
    {
        DatasetCatalogManifest manifest = BuildOneEntryManifest() with { SchemaVersion = 99 };
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ManifestStore.ValidateDatasets(manifest, ManifestPath));
        Assert.Contains("schemaVersion", ex.Message);
    }

    [Fact]
    public void BlankEntrySummary_Throws()
    {
        DatasetCatalogManifest manifest = WithEntry(BuildEntry() with { Summary = "  " });
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ManifestStore.ValidateDatasets(manifest, ManifestPath));
        Assert.Contains("summary", ex.Message);
    }

    [Fact]
    public void EmptyVariants_Throws()
    {
        DatasetCatalogManifest manifest = WithEntry(BuildEntry() with { Variants = [] });
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ManifestStore.ValidateDatasets(manifest, ManifestPath));
        Assert.Contains("variants", ex.Message);
    }

    [Fact]
    public void DuplicateEntryNames_Throws()
    {
        DatasetEntry a = BuildEntry() with { Name = "COCO 2017" };
        DatasetEntry b = BuildEntry() with
        {
            Name = "COCO 2017",
            Variants = [BuildVariant() with { Id = "coco-other" }],
        };
        DatasetCatalogManifest manifest = new(
            SchemaVersion: 1,
            Licenses: BuildLicenses(),
            Datasets: [a, b]);
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ManifestStore.ValidateDatasets(manifest, ManifestPath));
        Assert.Contains("duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateVariantIds_AcrossEntries_Throws()
    {
        // Variant ids are the install handle and the SQL-resolvable name —
        // they must be unique across the whole manifest, not just within
        // the parent entry.
        DatasetEntry a = BuildEntry() with
        {
            Name = "Entry A",
            Variants = [BuildVariant() with { Id = "shared" }],
        };
        DatasetEntry b = BuildEntry() with
        {
            Name = "Entry B",
            Variants = [BuildVariant() with { Id = "shared" }],
        };
        DatasetCatalogManifest manifest = new(
            SchemaVersion: 1,
            Licenses: BuildLicenses(),
            Datasets: [a, b]);
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ManifestStore.ValidateDatasets(manifest, ManifestPath));
        Assert.Contains("shared", ex.Message);
    }

    [Fact]
    public void DuplicateVariantIds_WithinEntry_Throws()
    {
        DatasetEntry entry = BuildEntry() with
        {
            Variants =
            [
                BuildVariant() with { Id = "v1" },
                BuildVariant() with { Id = "v1" },
            ],
        };
        DatasetCatalogManifest manifest = WithEntry(entry);
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ManifestStore.ValidateDatasets(manifest, ManifestPath));
        Assert.Contains("v1", ex.Message);
    }

    [Fact]
    public void BlankVariantId_Throws()
    {
        DatasetCatalogManifest manifest = WithEntry(BuildEntry() with
        {
            Variants = [BuildVariant() with { Id = "  " }],
        });
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ManifestStore.ValidateDatasets(manifest, ManifestPath));
        Assert.Contains("id", ex.Message);
    }

    [Fact]
    public void EmptyVariantSources_Throws()
    {
        DatasetCatalogManifest manifest = WithEntry(BuildEntry() with
        {
            Variants =
            [
                BuildVariant() with { Versions = [BuildVersion() with { Sources = [] }] },
            ],
        });
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ManifestStore.ValidateDatasets(manifest, ManifestPath));
        Assert.Contains("sources", ex.Message);
    }

    [Fact]
    public void EmptyVariantIngest_Throws()
    {
        DatasetCatalogManifest manifest = WithEntry(BuildEntry() with
        {
            Variants =
            [
                BuildVariant() with { Versions = [BuildVersion() with { Ingest = [] }] },
            ],
        });
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ManifestStore.ValidateDatasets(manifest, ManifestPath));
        Assert.Contains("ingest", ex.Message);
    }

    [Fact]
    public void DuplicateIngestTableNames_Throws()
    {
        DatasetCatalogManifest manifest = WithEntry(BuildEntry() with
        {
            Variants =
            [
                BuildVariant() with
                {
                    Versions =
                    [
                        BuildVersion() with
                        {
                            Ingest =
                            [
                                new CatalogIngestJob("a.zip", "images"),
                                new CatalogIngestJob("b.zip", "images"),
                            ],
                        },
                    ],
                },
            ],
        });
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ManifestStore.ValidateDatasets(manifest, ManifestPath));
        Assert.Contains("duplicate", ex.Message);
        Assert.Contains("images", ex.Message);
    }

    [Fact]
    public void UnknownLicenseId_Throws()
    {
        DatasetCatalogManifest manifest = WithEntry(BuildEntry() with
        {
            LicenseIds = ["nonexistent-license"],
        });
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ManifestStore.ValidateDatasets(manifest, ManifestPath));
        Assert.Contains("nonexistent-license", ex.Message);
    }

    [Fact]
    public void EmptyModalities_Throws()
    {
        DatasetCatalogManifest manifest = WithEntry(BuildEntry() with { Modalities = [] });
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ManifestStore.ValidateDatasets(manifest, ManifestPath));
        Assert.Contains("modalities", ex.Message);
    }

    [Fact]
    public void UnknownModality_Throws()
    {
        DatasetCatalogManifest manifest = WithEntry(BuildEntry() with
        {
            Modalities = ["NotAThing"],
        });
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ManifestStore.ValidateDatasets(manifest, ManifestPath));
        Assert.Contains("NotAThing", ex.Message);
    }

    [Fact]
    public void Modality_CaseInsensitive_Accepted()
    {
        // "image" lower-cased should still pass since modality matching
        // is case-insensitive — authors who forget the PascalCase
        // shouldn't be punished. Canonical form is PascalCase but the
        // validator accepts equivalents.
        DatasetCatalogManifest manifest = WithEntry(BuildEntry() with
        {
            Modalities = ["image"],
        });
        ManifestStore.ValidateDatasets(manifest, ManifestPath);
    }

    [Fact]
    public void UnknownSuitableForTask_Throws()
    {
        DatasetCatalogManifest manifest = WithEntry(BuildEntry() with
        {
            SuitableForTasks = ["NotARealTask"],
        });
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ManifestStore.ValidateDatasets(manifest, ManifestPath));
        Assert.Contains("NotARealTask", ex.Message);
    }

    [Fact]
    public void KnownSuitableForTask_Accepted()
    {
        DatasetCatalogManifest manifest = WithEntry(BuildEntry() with
        {
            SuitableForTasks = ["LabeledObjectDetector", "ImageCaptioner"],
        });
        ManifestStore.ValidateDatasets(manifest, ManifestPath);
    }

    [Fact]
    public void CardFile_MissingOnDisk_Throws()
    {
        DatasetCatalogManifest manifest = WithEntry(BuildEntry() with
        {
            CardFile = "cards/does-not-exist.md",
        });
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ManifestStore.ValidateDatasets(manifest, ManifestPath));
        Assert.Contains("does-not-exist.md", ex.Message);
    }

    [Fact]
    public void DeprecatedVersionsZero_Throws()
    {
        CatalogDatasetVersion deprecatedHead = BuildVersion() with { Deprecated = true };
        DatasetCatalogManifest manifest = WithEntry(BuildEntry() with
        {
            Variants = [BuildVariant() with { Versions = [deprecatedHead] }],
        });
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ManifestStore.ValidateDatasets(manifest, ManifestPath));
        Assert.Contains("deprecated", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────── fixtures ───────────────────────────

    private static DatasetCatalogManifest BuildOneEntryManifest()
        => WithEntry(BuildEntry());

    private static DatasetCatalogManifest WithEntry(DatasetEntry entry)
        => new(SchemaVersion: 1, Licenses: BuildLicenses(), Datasets: [entry]);

    private static IReadOnlyDictionary<string, CatalogLicense> BuildLicenses()
        => new Dictionary<string, CatalogLicense>(StringComparer.Ordinal)
        {
            ["cc-by-4.0"] = new(
                Title: "Creative Commons Attribution 4.0",
                Spdx: "CC-BY-4.0",
                CanonicalUrl: "https://example.invalid/",
                TextFile: "licenses/cc-by-4.0.txt",
                Summary: "Permissive.",
                RequiresAcceptance: false),
        };

    private static DatasetEntry BuildEntry()
        => new(
            Name: "COCO 2017",
            Summary: "Microsoft Common Objects in Context — 2017 release.",
            Description: "Eighty-class detection/segmentation/captioning corpus.",
            Modalities: ["Image"],
            LicenseIds: ["cc-by-4.0"],
            Attributions: ["COCO consortium"],
            SuitableForTasks: null,
            Variants: [BuildVariant()]);

    private static DatasetVariant BuildVariant()
        => new(
            Id: "coco-test2017",
            DisplayName: "test2017 (images)",
            Summary: "40,670 unlabeled test images.",
            ApproxArchiveBytes: 1_000_000_000,
            ApproxIngestedBytes: 1_000_000_000,
            ExpectedRowCounts: new Dictionary<string, long> { ["images"] = 40670 },
            RequiresHfLogin: false,
            Versions: [BuildVersion()]);

    private static CatalogDatasetVersion BuildVersion()
        => new(
            Version: "2017",
            Sources: [new HttpsSource([new HttpsFile("https://example.invalid/test2017.zip", "test2017.zip")])],
            Ingest: [new CatalogIngestJob("test2017.zip", "images")],
            InstallSql: null);
}
