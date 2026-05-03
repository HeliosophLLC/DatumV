using DatumIngest.DatasetLibrary;
using DatumIngest.ModelLibrary;

using Microsoft.Extensions.Logging.Abstractions;

// ManifestStore exists in both DatumIngest.DatasetLibrary and
// DatumIngest.ModelLibrary; this file only exercises the dataset one.
using ManifestStore = DatumIngest.DatasetLibrary.ManifestStore;

namespace DatumIngest.Tests.DatasetLibrary;

/// <summary>
/// Smoke test for the production datasets/catalog.json: loads it through
/// <see cref="ManifestStore"/> and asserts the entries the engine builds
/// against today are present and structurally sound under the
/// entry/variant hierarchy.
/// </summary>
public sealed class DatasetManifestLoadTests
{
    [Fact]
    public void RealManifest_LoadsCleanly_FromRepoRoot()
    {
        ManifestStore store = new(NullLogger<ManifestStore>.Instance);

        DatasetCatalogManifest manifest = store.Manifest;
        Assert.Equal(1, manifest.SchemaVersion);
        Assert.NotEmpty(manifest.Datasets);
        Assert.NotEmpty(manifest.Licenses);
    }

    [Fact]
    public void RealManifest_ContainsCoco2017Entry_WithExpectedVariants()
    {
        ManifestStore store = new(NullLogger<ManifestStore>.Instance);
        DatasetEntry coco = Assert.Single(store.Manifest.Datasets,
            d => string.Equals(d.Name, "COCO 2017", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("cc-by-4.0", Assert.Single(coco.LicenseIds));

        // The three 2017 splits ship as separate variants. Per-variant
        // shape (one HTTPS source for the split's zip, one ingest job
        // writing to the `images` table) is identical across all three —
        // assert it once per variant via the helper.
        AssertImagesOnlyVariant(coco, "coco-test2017", "test2017.zip", minArchiveBytes: 6_000_000_000);
        AssertImagesOnlyVariant(coco, "coco-val2017", "val2017.zip", minArchiveBytes: 500_000_000);
        AssertImagesOnlyVariant(coco, "coco-train2017", "train2017.zip", minArchiveBytes: 17_000_000_000);
    }

    private static void AssertImagesOnlyVariant(
        DatasetEntry coco,
        string variantId,
        string expectedSourceFile,
        long minArchiveBytes)
    {
        DatasetVariant variant = Assert.Single(coco.Variants,
            v => string.Equals(v.Id, variantId, StringComparison.Ordinal));
        Assert.True(variant.ApproxArchiveBytes >= minArchiveBytes,
            $"Expected {variantId} archive >= {minArchiveBytes:N0}; got {variant.ApproxArchiveBytes:N0}.");

        CatalogDatasetVersion version = Assert.Single(variant.Versions);
        Assert.Equal("2017", version.Version);

        // Each variant ships images-only — one ingest job writing to the
        // `images` table, sourced from the split's zip.
        CatalogIngestJob job = Assert.Single(version.Ingest);
        Assert.Equal("images", job.TableName);
        Assert.Equal(expectedSourceFile, job.SourcePath);

        CatalogSource source = Assert.Single(version.Sources);
        HttpsSource https = Assert.IsType<HttpsSource>(source);
        HttpsFile file = Assert.Single(https.Urls);
        Assert.Equal(expectedSourceFile, file.DestFile);
    }

    [Fact]
    public void RealManifest_LicenseText_ResolvesForDeclaredLicenses()
    {
        ManifestStore store = new(NullLogger<ManifestStore>.Instance);

        string? text = store.GetLicenseText("cc-by-4.0");
        Assert.NotNull(text);
        Assert.Contains("Creative Commons", text);
    }

    [Fact]
    public void RealManifest_EntryCardMarkdown_ResolvesForCoco2017()
    {
        ManifestStore store = new(NullLogger<ManifestStore>.Instance);
        string? card = store.GetEntryCardMarkdown("COCO 2017");
        Assert.NotNull(card);
        Assert.Contains("COCO", card);
    }

    [Fact]
    public void RealManifest_FindVariant_ResolvesByIdToBothEntryAndVariant()
    {
        ManifestStore store = new(NullLogger<ManifestStore>.Instance);
        (DatasetEntry Entry, DatasetVariant Variant)? hit = store.FindVariant("coco-test2017");
        Assert.NotNull(hit);
        Assert.Equal("COCO 2017", hit.Value.Entry.Name);
        Assert.Equal("coco-test2017", hit.Value.Variant.Id);
    }
}
