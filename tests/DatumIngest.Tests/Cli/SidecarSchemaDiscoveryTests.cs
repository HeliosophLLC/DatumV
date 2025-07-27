using DatumIngest.Catalog;
using DatumIngest.Model;

namespace DatumIngest.Tests.Cli;

/// <summary>
/// Tests for automatic discovery of <c>.datum-schema</c> sidecar files
/// when sources are registered in the catalog.
/// </summary>
public sealed class SidecarSchemaDiscoveryTests : IDisposable
{
    private readonly string _tempDirectory;

    public SidecarSchemaDiscoveryTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "datum-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void DiscoversSidecarSchema_WhenFileExists()
    {
        string csvPath = CreateCsvFile("data.csv", "id,name\n1,Alice\n2,Bob\n");
        Schema schema = new([
            new ColumnInfo("id", DataKind.Scalar, nullable: false),
            new ColumnInfo("name", DataKind.String, nullable: true),
        ]);
        WriteSidecar(csvPath, "data", schema);

        TableCatalog catalog = CreateCatalog("data", csvPath);

        DiscoverSidecarSchemas(catalog);

        Assert.True(catalog.TryGetSchema("data", out Schema? discovered));
        Assert.Equal(2, discovered!.Columns.Count);
        Assert.Equal("id", discovered.Columns[0].Name);
    }

    [Fact]
    public void SkipsSidecar_WhenNoFileExists()
    {
        string csvPath = Path.Combine(_tempDirectory, "missing.csv");
        File.WriteAllText(csvPath, "id\n1\n");

        TableCatalog catalog = CreateCatalog("data", csvPath);

        DiscoverSidecarSchemas(catalog);

        Assert.False(catalog.TryGetSchema("data", out _));
    }

    [Fact]
    public void SkipsSidecar_WhenSchemaAlreadyRegistered()
    {
        string csvPath = CreateCsvFile("data.csv", "id\n1\n");
        Schema explicitSchema = new([new ColumnInfo("id", DataKind.Scalar, nullable: false)]);
        Schema sidecarSchema = new([new ColumnInfo("id", DataKind.String, nullable: true)]);
        WriteSidecar(csvPath, "data", sidecarSchema);

        TableCatalog catalog = CreateCatalog("data", csvPath);
        catalog.RegisterSchema("data", explicitSchema);

        DiscoverSidecarSchemas(catalog);

        Assert.True(catalog.TryGetSchema("data", out Schema? found));
        Assert.Equal(DataKind.Scalar, found!.Columns[0].Kind);
    }

    [Fact]
    public void DiscoversMultipleSidecars_ForMultipleSources()
    {
        string csvPath1 = CreateCsvFile("images.csv", "pixel\n0\n1\n");
        string csvPath2 = CreateCsvFile("labels.csv", "label\n3\n7\n");
        WriteSidecar(csvPath1, "images", new Schema([new ColumnInfo("pixel", DataKind.Scalar, nullable: false)]));
        WriteSidecar(csvPath2, "labels", new Schema([new ColumnInfo("label", DataKind.String, nullable: false)]));

        TableCatalog catalog = new();
        catalog.Register(new TableDescriptor("csv", "images", csvPath1, new Dictionary<string, string>()));
        catalog.Register(new TableDescriptor("csv", "labels", csvPath2, new Dictionary<string, string>()));

        DiscoverSidecarSchemas(catalog);

        Assert.True(catalog.TryGetSchema("images", out _));
        Assert.True(catalog.TryGetSchema("labels", out _));
    }

    [Fact]
    public void DiscoveredSchema_PreservesColumnDetails()
    {
        string csvPath = CreateCsvFile("data.csv", "age,name\n25,Alice\n30,Bob\n");
        Schema schema = new([
            new ColumnInfo("age", DataKind.Scalar, nullable: false),
            new ColumnInfo("name", DataKind.String, nullable: true),
        ]);
        WriteSidecar(csvPath, "data", schema);

        TableCatalog catalog = CreateCatalog("data", csvPath);

        DiscoverSidecarSchemas(catalog);

        Assert.True(catalog.TryGetSchema("data", out Schema? discovered));
        Assert.Equal(2, discovered!.Columns.Count);
        Assert.Equal("age", discovered.Columns[0].Name);
        Assert.Equal(DataKind.Scalar, discovered.Columns[0].Kind);
        Assert.False(discovered.Columns[0].Nullable);
        Assert.Equal("name", discovered.Columns[1].Name);
        Assert.Equal(DataKind.String, discovered.Columns[1].Kind);
        Assert.True(discovered.Columns[1].Nullable);
    }

    /// <summary>
    /// Delegates to the unified <see cref="TableCatalog.DiscoverSidecars"/> method.
    /// </summary>
    private static void DiscoverSidecarSchemas(TableCatalog catalog)
    {
        catalog.DiscoverSidecars();
    }

    private string CreateCsvFile(string name, string content)
    {
        string path = Path.Combine(_tempDirectory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static TableCatalog CreateCatalog(string tableName, string csvPath)
    {
        TableCatalog catalog = new();
        catalog.Register(new TableDescriptor("csv", tableName, csvPath, new Dictionary<string, string>()));
        return catalog;
    }

    private static void WriteSidecar(string sourceFilePath, string tableName, Schema schema)
    {
        string sidecarPath = sourceFilePath + ".datum-schema";
        string json = SchemaSerializer.Serialize(tableName, schema);
        File.WriteAllText(sidecarPath, json);
    }
}
