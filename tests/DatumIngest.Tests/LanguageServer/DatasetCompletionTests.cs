namespace DatumIngest.Tests.LanguageServer;

using DatumIngest.LanguageServer;
using DatumIngest.Manifest;

/// <summary>
/// Tests for the dataset-aware paths in <see cref="CompletionProvider"/> —
/// `&lt;dataset-schema&gt;.` after-dot zone surfaces discovered variants
/// alongside installed tables, and the `datasets` schema appears in
/// schema-namespace completion even when nothing is installed yet.
/// </summary>
public sealed class DatasetCompletionTests : ServiceTestBase
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

    private static DatasetEntry Discovered(string schema, string name, string entryName, long archiveBytes)
        => new()
        {
            Schema = schema,
            Name = name,
            VariantId = name,
            EntryName = entryName,
            DisplayName = $"{name} (display)",
            Version = "2017",
            Modalities = ["Image"],
            LicenseIds = ["cc-by-4.0"],
            ApproxArchiveBytes = archiveBytes,
            ApproxIngestedBytes = archiveBytes - 100,
            Status = DatasetInstallStatus.Discovered,
        };

    [Fact]
    public void AfterDot_DatasetSchema_SurfacesDiscoveredVariant()
    {
        LanguageServerManifest manifest = BuildManifest([
            Discovered("datasets", "coco_test2017", "COCO 2017", 6_646_972_416L),
        ]);
        CompletionProvider provider = new(manifest);

        const string sql = "SELECT * FROM datasets.";
        CompletionItem[] items = provider.GetCompletions(sql, sql.Length);

        CompletionItem? coco = items.FirstOrDefault(
            i => string.Equals(i.Label, "coco_test2017", StringComparison.Ordinal));
        Assert.NotNull(coco);
        Assert.Equal(CompletionItemKind.Table, coco.Kind);
        Assert.Equal("installable", coco.LabelSuffix);
        // The Detail line includes modality + entry name + version + size.
        Assert.Contains("COCO 2017", coco.Detail);
        Assert.Contains("Image", coco.Detail);
        Assert.Contains("v2017", coco.Detail);
        Assert.Contains("6.2 GB", coco.Detail);
    }

    [Fact]
    public void AfterDot_DatasetSchema_SurfacesInstalledVariantViaTablesEntry()
    {
        // Installed datasets reach the manifest via the generic Tables
        // iteration (the binder mounts a DatumFileTableProviderV2 under
        // <schema>.<name>). The dataset entry's Status is Installed, so
        // AddDatasetVariants skips it (it's already in Tables). Verify
        // the installed name still shows up via AddSchemaTables.
        DatasetEntry installed = new()
        {
            Schema = "datasets",
            Name = "coco_val2017",
            VariantId = "coco_val2017",
            EntryName = "COCO 2017",
            DisplayName = "val2017 (images)",
            Version = "2017",
            Modalities = ["Image"],
            LicenseIds = ["cc-by-4.0"],
            ApproxArchiveBytes = 815_585_330L,
            ApproxIngestedBytes = 815_000_000L,
            Status = DatasetInstallStatus.Installed,
        };
        LanguageServerManifest manifest = BuildManifest(
            datasets: [installed],
            extraTables: [
                new TableSchemaEntry
                {
                    Name = "datasets.coco_val2017",
                    Columns =
                    [
                        new TableColumnEntry { Name = "name", Kind = "String", Nullable = false },
                        new TableColumnEntry { Name = "image", Kind = "Image", Nullable = false },
                    ],
                },
            ]);
        CompletionProvider provider = new(manifest);

        const string sql = "SELECT * FROM datasets.";
        CompletionItem[] items = provider.GetCompletions(sql, sql.Length);

        CompletionItem? cocoVal = items.FirstOrDefault(
            i => string.Equals(i.Label, "coco_val2017", StringComparison.Ordinal));
        Assert.NotNull(cocoVal);
        Assert.Equal(CompletionItemKind.Table, cocoVal.Kind);
        // Installed variant should NOT carry the installable badge — it's already queryable.
        Assert.NotEqual("installable", cocoVal.LabelSuffix);
    }

    [Fact]
    public void AfterDot_WrongSchema_DoesNotSurfaceDataset()
    {
        LanguageServerManifest manifest = BuildManifest([
            Discovered("datasets", "coco_test2017", "COCO 2017", 6_646_972_416L),
        ]);
        CompletionProvider provider = new(manifest);

        const string sql = "SELECT * FROM public.";
        CompletionItem[] items = provider.GetCompletions(sql, sql.Length);

        Assert.DoesNotContain(items,
            i => string.Equals(i.Label, "coco_test2017", StringComparison.Ordinal));
    }

    [Fact]
    public void AfterDot_DatasetsSchema_DoesNotLeakSystemDatasetsColumns()
    {
        // Regression: `SELECT * FROM datasets.` was surfacing the columns
        // of `system.datasets` (the virtual installed-datasets table)
        // because the qualified-column lookup walked the search_path and
        // matched `system.datasets` for the bare qualifier "datasets".
        // The fix gates the search-path walk on "is this a known
        // schema?" — for known schemas, only schema-aware paths run.
        LanguageServerManifest manifest = BuildManifest(
            datasets: [Discovered("datasets", "coco_test2017", "COCO 2017", 6_646_972_416L)],
            extraTables: [
                // The actual system.datasets provider's column shape.
                new TableSchemaEntry
                {
                    Name = "system.datasets",
                    Columns =
                    [
                        new TableColumnEntry { Name = "schema", Kind = "String", Nullable = false },
                        new TableColumnEntry { Name = "name", Kind = "String", Nullable = false },
                        new TableColumnEntry { Name = "variant_id", Kind = "String", Nullable = false },
                        new TableColumnEntry { Name = "approx_archive_bytes", Kind = "Int64", Nullable = false },
                        new TableColumnEntry { Name = "file_path", Kind = "String", Nullable = false },
                    ],
                },
            ]);
        CompletionProvider provider = new(manifest);

        const string sql = "SELECT * FROM datasets.";
        CompletionItem[] items = provider.GetCompletions(sql, sql.Length);

        // None of system.datasets' columns should leak.
        Assert.DoesNotContain(items, i => i.Label == "approx_archive_bytes");
        Assert.DoesNotContain(items, i => i.Label == "variant_id");
        Assert.DoesNotContain(items, i => i.Label == "file_path");
        // But the dataset variant should be there.
        Assert.Contains(items,
            i => string.Equals(i.Label, "coco_test2017", StringComparison.Ordinal));
    }

    [Fact]
    public void SchemaNames_DatasetSchemaSurfaces_EvenWithNoInstalledTables()
    {
        // No installed tables (Tables = empty) but a discovered dataset
        // declares the `datasets` schema. The schema name should still
        // appear in schema-namespace completion so the user can drill
        // `datasets.` before installing.
        LanguageServerManifest manifest = BuildManifest([
            Discovered("datasets", "coco_test2017", "COCO 2017", 6_646_972_416L),
        ]);
        CompletionProvider provider = new(manifest);

        const string sql = "SELECT * FROM ";
        CompletionItem[] items = provider.GetCompletions(sql, sql.Length);

        Assert.Contains(items,
            i => string.Equals(i.Label, "datasets", StringComparison.Ordinal)
                && i.Kind == CompletionItemKind.Schema);
    }
}
