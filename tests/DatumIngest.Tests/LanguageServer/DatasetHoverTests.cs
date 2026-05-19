namespace Heliosoph.DatumV.Tests.LanguageServer;

using Heliosoph.DatumV.LanguageServer;
using Heliosoph.DatumV.Manifest;

/// <summary>
/// Tests for <see cref="HoverProvider"/>'s dataset-aware paths — installed
/// datasets enrich the bare table hover with entry/version/size context,
/// discovered datasets synthesize a card even without a mounted provider.
/// </summary>
public sealed class DatasetHoverTests : ServiceTestBase
{
    private static LanguageServerManifest BuildManifest(
        IReadOnlyList<DatasetEntry> datasets,
        IReadOnlyList<TableSchemaEntry>? extraTables = null)
    {
        return new LanguageServerManifest
        {
            Tables = extraTables ?? [],
            Functions = [],
            Keywords = [],
            Datasets = datasets,
        };
    }

    private static DatasetEntry Entry(
        string schema, string name, DatasetInstallStatus status, long archiveBytes)
        => new()
        {
            Schema = schema,
            Name = name,
            VariantId = name,
            EntryName = "COCO 2017",
            DisplayName = $"{name} (display)",
            Version = "2017",
            Modalities = ["Image"],
            LicenseIds = ["cc-by-4.0"],
            ApproxArchiveBytes = archiveBytes,
            ApproxIngestedBytes = archiveBytes - 100,
            Status = status,
        };

    [Fact]
    public void DiscoveredDataset_HoverSynthesizesCard()
    {
        LanguageServerManifest manifest = BuildManifest([
            Entry("datasets", "coco_test2017", DatasetInstallStatus.Discovered, 6_646_972_416L),
        ]);
        HoverProvider provider = new(manifest);

        // Hover anywhere inside "datasets.coco_test2017".
        const string sql = "SELECT * FROM datasets.coco_test2017";
        HoverResult? result = provider.GetHover(sql, sql.IndexOf("coco", StringComparison.Ordinal));
        Assert.NotNull(result);
        Assert.Contains("Dataset:", result.Contents);
        Assert.Contains("datasets.coco_test2017", result.Contents);
        Assert.Contains("installable", result.Contents);
        Assert.Contains("COCO 2017", result.Contents);
        Assert.Contains("v2017", result.Contents);
        Assert.Contains("Image", result.Contents);
        Assert.Contains("cc-by-4.0", result.Contents);
        Assert.Contains("6.2 GB", result.Contents);
    }

    [Fact]
    public void InstalledDataset_HoverMergesTableColumnsWithDatasetMetadata()
    {
        LanguageServerManifest manifest = BuildManifest(
            datasets: [Entry("datasets", "coco_test2017", DatasetInstallStatus.Installed, 6_646_972_416L)],
            extraTables: [
                new TableSchemaEntry
                {
                    Name = "datasets.coco_test2017",
                    Columns =
                    [
                        new TableColumnEntry { Name = "name", Kind = "String", Nullable = false },
                        new TableColumnEntry { Name = "image", Kind = "Image", Nullable = false },
                    ],
                },
            ]);
        HoverProvider provider = new(manifest);

        const string sql = "SELECT * FROM datasets.coco_test2017";
        HoverResult? result = provider.GetHover(sql, sql.IndexOf("coco", StringComparison.Ordinal));
        Assert.NotNull(result);
        Assert.Contains("Dataset:", result.Contents);
        Assert.DoesNotContain("installable", result.Contents);   // installed
        // Dataset card section.
        Assert.Contains("COCO 2017", result.Contents);
        Assert.Contains("Image", result.Contents);
        Assert.Contains("v2017", result.Contents);
        // Column block from the underlying TableSchemaEntry.
        Assert.Contains("`name`", result.Contents);
        Assert.Contains("`image`", result.Contents);
        Assert.Contains("(2 columns)", result.Contents);
    }
}
