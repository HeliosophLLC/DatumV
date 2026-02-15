using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Inference;
using DatumIngest.Model;
using DatumIngest.Models;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Catalog.Providers;

/// <summary>
/// End-to-end tests for <see cref="ModelsTableProvider"/> — verifies the
/// virtual table reflects the live <see cref="ModelCatalog"/>, surfaces
/// the metadata fields users care about, and reports <c>status</c>
/// correctly for missing-vs-available files.
/// </summary>
public sealed class ModelsTableProviderTests : ServiceTestBase, IDisposable
{
    private readonly string _tempModelDir = Path.Combine(
        Path.GetTempPath(), $"models_provider_test_{Guid.NewGuid():N}");

    public ModelsTableProviderTests()
    {
        Directory.CreateDirectory(_tempModelDir);
    }

    public override void Dispose()
    {
        base.Dispose();
        
        if (Directory.Exists(_tempModelDir))
        {
            try { Directory.Delete(_tempModelDir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Schema sanity check — confirms the 10 columns we promised are present
    /// in the declared order with the declared kinds. Locks the contract that
    /// downstream UI / tooling will read.
    /// </summary>
    [Fact]
    public void GetSchema_ExposesExpectedColumns()
    {
        Pool pool = CreatePool();
        ModelCatalog catalog = new(_tempModelDir);
        using ModelsTableProvider provider = new(pool, catalog);

        Schema schema = provider.GetSchema();

        Assert.Equal(14, schema.Columns.Count);

        Assert.Equal("name", schema.Columns[0].Name);
        Assert.Equal(DataKind.String, schema.Columns[0].Kind);

        Assert.Equal("display_name", schema.Columns[1].Name);
        Assert.Equal("category", schema.Columns[2].Name);

        Assert.Equal("modalities", schema.Columns[3].Name);
        Assert.Equal(DataKind.String, schema.Columns[3].Kind);
        Assert.True(schema.Columns[3].IsArray);

        Assert.Equal("backend", schema.Columns[4].Name);
        Assert.Equal("parameters", schema.Columns[5].Name);
        Assert.Equal("file_name", schema.Columns[6].Name);

        Assert.Equal("file_names", schema.Columns[7].Name);
        Assert.Equal(DataKind.String, schema.Columns[7].Kind);
        Assert.True(schema.Columns[7].IsArray);

        Assert.Equal("file_size_bytes", schema.Columns[8].Name);
        Assert.Equal(DataKind.Int64, schema.Columns[8].Kind);

        Assert.Equal("license", schema.Columns[9].Name);
        Assert.Equal("license_holder", schema.Columns[10].Name);
        Assert.Equal("source_url", schema.Columns[11].Name);
        Assert.Equal("status", schema.Columns[12].Name);
        Assert.Equal("kind", schema.Columns[13].Name);
        Assert.Equal(DataKind.String, schema.Columns[13].Kind);
        Assert.False(schema.Columns[13].Nullable);
    }

    /// <summary>
    /// One entry, file present on disk → row exposes all metadata, reports
    /// <c>status = available</c>, and <c>file_size_bytes</c> is the file's
    /// real length.
    /// </summary>
    [Fact]
    public async Task ScanAsync_FilePresent_ReportsAvailable()
    {
        const string filename = "fake-model.bin";
        string filePath = Path.Combine(_tempModelDir, filename);
        byte[] payload = new byte[1234];
        await File.WriteAllBytesAsync(filePath, payload);

        Pool pool = CreatePool();
        ModelCatalog catalog = new(_tempModelDir);
        catalog.Register(new ModelCatalogEntry(
            Name: "fake",
            Backend: "test",
            RelativePath: filename,
            InputKinds: [DataKind.String],
            OutputKind: DataKind.String,
            IsDeterministic: true,
            Loader: _ => throw new NotImplementedException(),
            DisplayName: "Fake Test Model",
            Parameters: "0.1B",
            License: "MIT",
            LicenseHolder: "Nobody",
            SourceUrl: "https://example.com/fake",
            Category: "llm",
            Modalities: ["text", "image"],
            Files: [filename, "extra-config.json"]));

        using ModelsTableProvider provider = new(pool, catalog);
        (Row row, Arena arena) = await ReadOnlyRowAsync(provider);

        Assert.Equal("fake", row[0].AsString(arena));
        Assert.Equal("Fake Test Model", row[1].AsString(arena));
        Assert.Equal("llm", row[2].AsString(arena));

        // modalities — typed Array<String> cell.
        Assert.True(row[3].IsArray);
        Assert.Equal(DataKind.String, row[3].Kind);
        string[] modalities = row[3].AsStringArray(arena);
        Assert.Equal(["text", "image"], modalities);

        Assert.Equal("test", row[4].AsString(arena));
        Assert.Equal("0.1B", row[5].AsString(arena));
        Assert.Equal(filename, row[6].AsString(arena));

        // file_names — typed Array<String> with the full dependency list.
        Assert.True(row[7].IsArray);
        Assert.Equal(DataKind.String, row[7].Kind);
        string[] fileNames = row[7].AsStringArray(arena);
        Assert.Equal([filename, "extra-config.json"], fileNames);

        Assert.Equal(payload.Length, row[8].AsInt64());
        Assert.Equal("MIT", row[9].AsString(arena));
        Assert.Equal("Nobody", row[10].AsString(arena));
        Assert.Equal("https://example.com/fake", row[11].AsString(arena));
        Assert.Equal("available", row[12].AsString(arena));
        Assert.Equal("builtin", row[13].AsString(arena));
    }

    /// <summary>
    /// File missing on disk → row is still emitted (registration is valid),
    /// but <c>status = missing</c> and <c>file_size_bytes</c> is null.
    /// This is the hot path for the "I deleted my models folder, what do I
    /// need to re-download?" diagnostic.
    /// </summary>
    [Fact]
    public async Task ScanAsync_FileMissing_ReportsMissingWithNullSize()
    {
        Pool pool = CreatePool();
        ModelCatalog catalog = new(_tempModelDir);
        catalog.Register(new ModelCatalogEntry(
            Name: "ghost",
            Backend: "test",
            RelativePath: "never-downloaded.bin",
            InputKinds: [DataKind.String],
            OutputKind: DataKind.String,
            IsDeterministic: true,
            Loader: _ => throw new NotImplementedException(),
            License: "Apache-2.0",
            SourceUrl: "https://example.com/ghost"));

        using ModelsTableProvider provider = new(pool, catalog);
        (Row row, Arena arena) = await ReadOnlyRowAsync(provider);

        Assert.Equal("ghost", row[0].AsString(arena));
        Assert.Equal("never-downloaded.bin", row[6].AsString(arena));
        Assert.True(row[8].IsNull);
        Assert.Equal("missing", row[12].AsString(arena));
    }

    /// <summary>
    /// Multiple entries → rows come back sorted by name (stable ordering for
    /// readable <c>SELECT *</c> output) regardless of registration order.
    /// </summary>
    [Fact]
    public async Task ScanAsync_MultipleEntries_RowsSortedByName()
    {
        Pool pool = CreatePool();
        ModelCatalog catalog = new(_tempModelDir);

        // Register out of alphabetical order.
        catalog.Register(MakeEntry("zebra"));
        catalog.Register(MakeEntry("apple"));
        catalog.Register(MakeEntry("mango"));

        using ModelsTableProvider provider = new(pool, catalog);

        List<string> names = new();
        await foreach (RowBatch batch in provider.ScanAsync(
            requiredColumns: null,
            filterHint: null,
            targetArena: null,
            cancellationToken: CancellationToken.None))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                names.Add(batch[i][0].AsString(batch.Arena));
            }
        }

        Assert.Equal(["apple", "mango", "zebra"], names);
    }

    /// <summary>
    /// SQL-defined models (registered via <c>CREATE MODEL</c> into
    /// <c>TableCatalog.DeclaredModels</c>) surface alongside built-ins.
    /// Most metadata columns are NULL — declared models don't carry
    /// license / category / modalities — but <c>kind = "declared"</c>
    /// and <c>file_name</c> reflects the user's <c>USING</c> path.
    /// </summary>
    [Fact]
    public async Task ScanAsync_DeclaredModel_ReportsKindDeclaredAndUsingPath()
    {
        const string filename = "decl-model.onnx";
        string filePath = Path.Combine(_tempModelDir, filename);
        byte[] payload = new byte[42];
        await File.WriteAllBytesAsync(filePath, payload);

        Pool pool = CreatePool();
        ModelCatalog catalog = new(_tempModelDir);
        ModelRegistry declared = new();
        declared.Register(MakeDescriptor("my_declared", $"file://{filePath}"));

        using ModelsTableProvider provider = new(pool, catalog, declared);
        (Row row, Arena arena) = await ReadOnlyRowAsync(provider);

        Assert.Equal("my_declared", row[0].AsString(arena));
        // Most metadata columns null for declared models — they don't have
        // upstream catalog metadata to surface.
        Assert.True(row[1].IsNull); // display_name
        Assert.True(row[2].IsNull); // category
        Assert.True(row[3].IsNull); // modalities
        Assert.Equal("sql", row[4].AsString(arena)); // backend (synthetic discriminator inside the row)
        Assert.True(row[5].IsNull); // parameters
        Assert.Equal($"file://{filePath}", row[6].AsString(arena)); // file_name = raw USING path
        Assert.True(row[7].IsNull); // file_names
        Assert.Equal(payload.Length, row[8].AsInt64()); // file_size_bytes
        Assert.True(row[9].IsNull);  // license
        Assert.True(row[10].IsNull); // license_holder
        Assert.True(row[11].IsNull); // source_url
        Assert.Equal("available", row[12].AsString(arena));
        Assert.Equal("declared", row[13].AsString(arena));
    }

    /// <summary>
    /// Built-ins and SQL-defined models share <c>system.models</c>, sorted
    /// by name. The <c>kind</c> column is the only schema-stable
    /// discriminator users can filter on.
    /// </summary>
    [Fact]
    public async Task ScanAsync_BuiltinAndDeclared_BothScanWithKindDiscriminator()
    {
        // Built-in named "alpha" — file present so it reports available.
        string builtinFile = Path.Combine(_tempModelDir, "alpha.bin");
        await File.WriteAllBytesAsync(builtinFile, new byte[10]);

        // Declared named "zeta" — file present.
        string declaredFile = Path.Combine(_tempModelDir, "zeta.onnx");
        await File.WriteAllBytesAsync(declaredFile, new byte[20]);

        Pool pool = CreatePool();
        ModelCatalog catalog = new(_tempModelDir);
        catalog.Register(MakeEntry("alpha"));

        ModelRegistry declared = new();
        declared.Register(MakeDescriptor("zeta", $"file://{declaredFile}"));

        using ModelsTableProvider provider = new(pool, catalog, declared);

        List<(string Name, string Kind)> rows = new();
        await foreach (RowBatch batch in provider.ScanAsync(
            requiredColumns: null,
            filterHint: null,
            targetArena: null,
            cancellationToken: CancellationToken.None))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                rows.Add((
                    batch[i][0].AsString(batch.Arena),
                    batch[i][13].AsString(batch.Arena)));
            }
        }

        Assert.Equal(
            [("alpha", "builtin"), ("zeta", "declared")],
            rows);
    }

    private static ModelDescriptor MakeDescriptor(string name, string usingPath) => new(
        SchemaName: "models",
        Name: name,
        Parameters: Array.Empty<DatumIngest.Parsing.Ast.UdfParameter>(),
        ReturnTypeName: "Float32",
        UsingPath: usingPath,
        StatementBody: Array.Empty<DatumIngest.Parsing.Ast.Statement>(),
        BoundSessions: new Dictionary<string, IInferenceSession>(StringComparer.Ordinal),
        ReturnIsNotNull: false,
        SourceText: $"CREATE MODEL {name}");

    private static ModelCatalogEntry MakeEntry(string name) => new(
        Name: name,
        Backend: "test",
        RelativePath: $"{name}.bin",
        InputKinds: [DataKind.String],
        OutputKind: DataKind.String,
        IsDeterministic: true,
        Loader: _ => throw new NotImplementedException());

    private static async Task<(Row Row, Arena Arena)> ReadOnlyRowAsync(ModelsTableProvider provider)
    {
        await foreach (RowBatch batch in provider.ScanAsync(
            requiredColumns: null,
            filterHint: null,
            targetArena: null,
            cancellationToken: CancellationToken.None))
        {
            Assert.Equal(1, batch.Count);
            return (batch[0], batch.Arena);
        }
        throw new InvalidOperationException("Provider yielded no batches.");
    }
}
