using DatumIngest.Catalog;
using DatumIngest.Catalog.Registries;
using DatumIngest.LanguageServer;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// S7f end-to-end pins for schema-qualified UDFs and procedures: the
/// contract S7a–S7e built up, exercised through the catalog (DDL +
/// query), through the manifest round-trip (persist → reload), and
/// through the language server (completion + hover + semantic). One
/// file so every layer's expectation is visible in one place.
/// </summary>
public sealed class QualifiedRoutineE2ETests : ServiceTestBase, IDisposable
{
    private readonly string _scratchDir;
    private readonly string _catalogPath;

    public QualifiedRoutineE2ETests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            $"datum-s7-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratchDir);
        _catalogPath = Path.Combine(_scratchDir, CatalogStore.DefaultFileName);
    }

    public new void Dispose()
    {
        base.Dispose();
        try { if (Directory.Exists(_scratchDir)) Directory.Delete(_scratchDir, recursive: true); }
        catch { /* best-effort */ }
    }

    // ───────────────────── DDL & registration ─────────────────────

    [Fact]
    public void CreateFunction_Unqualified_LandsInPublic()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE FUNCTION shout(@s STRING) AS upper(@s)");

        UdfDescriptor descriptor = Assert.Single(catalog.Udfs.Entries);
        Assert.Equal("public", descriptor.SchemaName);
        Assert.Equal("shout", descriptor.Name);
    }

    [Fact]
    public void CreateFunction_QualifiedSchema_LandsThere()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE SCHEMA myapp");
        catalog.Plan("CREATE FUNCTION myapp.classify(@x INT32) AS @x + 1");

        Assert.True(catalog.Udfs.TryGet(new QualifiedName("myapp", "classify"), out UdfDescriptor? udf));
        Assert.Equal("myapp", udf!.SchemaName);
    }

    [Fact]
    public void CreateProcedure_Unqualified_LandsInPublic()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE PROCEDURE noop() AS BEGIN SELECT 1 END");

        ProcedureDescriptor descriptor = Assert.Single(catalog.Procedures.Entries);
        Assert.Equal("public", descriptor.SchemaName);
        Assert.Equal("noop", descriptor.Name);
    }

    [Fact]
    public void DropFunction_QualifiedAndUnqualified_BothResolveViaSearchPath()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE SCHEMA myapp");
        catalog.Plan("CREATE FUNCTION myapp.a(@x INT32) AS @x");
        catalog.Plan("CREATE FUNCTION b(@x INT32) AS @x");

        // Explicit-schema drop.
        catalog.Plan("DROP FUNCTION myapp.a");
        Assert.False(catalog.Udfs.TryGet(new QualifiedName("myapp", "a"), out _));

        // Unqualified — walks search_path = [public, system] → finds (public, b).
        catalog.Plan("DROP FUNCTION b");
        Assert.False(catalog.Udfs.TryGet(new QualifiedName("public", "b"), out _));
    }

    // ───────────────────── Call resolution ─────────────────────

    [Fact]
    public async Task UnqualifiedCall_ResolvesViaSearchPath()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE TABLE t (id Int32)");
        catalog.Plan("INSERT INTO t VALUES (5)");
        catalog.Plan("CREATE FUNCTION square(@x INT32) AS @x * @x");

        IQueryPlan plan = catalog.Plan("SELECT square(id) FROM t");
        List<int> values = await CollectFirstColumnAsInt32Async(plan);
        Assert.Equal(new[] { 25 }, values);
    }

    [Fact]
    public async Task QualifiedCall_ResolvesExactly()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE SCHEMA myapp");
        catalog.Plan("CREATE TABLE t (id Int32)");
        catalog.Plan("INSERT INTO t VALUES (3)");
        catalog.Plan("CREATE FUNCTION myapp.dbl(@x INT32) AS @x + @x");

        IQueryPlan plan = catalog.Plan("SELECT myapp.dbl(id) FROM t");
        List<int> values = await CollectFirstColumnAsInt32Async(plan);
        Assert.Equal(new[] { 6 }, values);
    }

    [Fact]
    public void Procedure_InSelectPosition_Rejected()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE PROCEDURE noop() AS BEGIN SELECT 1 END");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => catalog.Plan("SELECT noop()"));
        Assert.Contains("procedure", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CALL", ex.Message);
    }

    [Fact]
    public async Task ExplicitSystemQualifier_ResolvesBuiltIn()
    {
        // Built-ins live in `system`; explicit `system.fn(...)` works.
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE TABLE t (s STRING)");
        catalog.Plan("INSERT INTO t VALUES ('hello')");

        IQueryPlan plan = catalog.Plan("SELECT system.upper(s) FROM t");
        bool sawRow = false;
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            for (int r = 0; r < batch.Count; r++)
            {
                Assert.Equal("HELLO", batch[r][0].AsString(batch.Arena));
                sawRow = true;
            }
        }
        Assert.True(sawRow);
    }

    // ───────────────────── Persistence round-trip ─────────────────────

    [Fact]
    public void Manifest_RoundTripsSchemaAcrossReopen()
    {
        // CREATE → save → close → reopen → UDF descriptor still carries the
        // schema it was registered under. The manifest is v4 and the
        // schema field is load-bearing.
        using (TableCatalog first = CreateCatalog(_catalogPath))
        {
            first.Plan("CREATE SCHEMA myapp");
            first.Plan("CREATE FUNCTION myapp.classify(@x INT32) AS @x + 1");
            first.Plan("CREATE PROCEDURE myapp.tally(@n INT32) AS BEGIN SELECT @n END");
        }

        using TableCatalog reopened = CreateCatalog(_catalogPath);

        Assert.True(reopened.Udfs.TryGet(new QualifiedName("myapp", "classify"), out UdfDescriptor? udf));
        Assert.Equal("myapp", udf!.SchemaName);
        Assert.True(reopened.Procedures.TryGet(new QualifiedName("myapp", "tally"), out ProcedureDescriptor? proc));
        Assert.Equal("myapp", proc!.SchemaName);
    }

    [Fact]
    public void Manifest_OldV3IsRejected()
    {
        // No backward compatibility — a v3 manifest file is hard-rejected
        // at load with a "delete the catalog directory" diagnostic. The
        // S7c bump intentionally provides no migration path.
        File.WriteAllText(_catalogPath,
            """
            {
              "version": 3,
              "udfs": [{"name": "old", "parameters": [], "body": "1"}]
            }
            """);

        CatalogStoreLoadException ex = Assert.Throws<CatalogStoreLoadException>(
            () => CreateCatalog(_catalogPath));
        Assert.Contains("version 3", ex.Message);
        Assert.Contains("Delete the catalog directory", ex.Message);
    }

    // ───────────────────── system.udfs / system.procedures ─────────────────────

    [Fact]
    public async Task SystemUdfs_ExposesSchemaColumn()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE SCHEMA myapp");
        catalog.Plan("CREATE FUNCTION myapp.classify(@x INT32) AS @x");
        catalog.Plan("CREATE FUNCTION shout(@s STRING) AS upper(@s)");

        IQueryPlan plan = catalog.Plan("SELECT schema, name FROM system.udfs ORDER BY schema, name");

        List<(string schema, string name)> rows = new();
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            for (int r = 0; r < batch.Count; r++)
            {
                rows.Add((batch[r][0].AsString(batch.Arena), batch[r][1].AsString(batch.Arena)));
            }
        }

        Assert.Equal(new[]
        {
            ("myapp", "classify"),
            ("public", "shout"),
        }, rows);
    }

    // ───────────────────── Language server ─────────────────────

    [Fact]
    public void Lsp_CompletionAfterSchemaDot_OffersUdfsInSchema()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE SCHEMA myapp");
        catalog.Plan("CREATE FUNCTION myapp.classify(@x INT32) AS @x + 1");

        LanguageServerManifest manifest = CatalogManifestBuilder.Build(catalog, catalog.Functions);
        CompletionProvider completion = new(manifest);

        CompletionItem[] items = completion.GetCompletions("SELECT myapp.", 13);

        Assert.Contains(items, item => item.Label == "classify");
    }

    [Fact]
    public void Lsp_SignatureHelp_ForQualifiedUdf()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE SCHEMA myapp");
        catalog.Plan("CREATE FUNCTION myapp.classify(@x INT32) RETURNS INT32 AS @x + 1");

        LanguageServerManifest manifest = CatalogManifestBuilder.Build(catalog, catalog.Functions);
        SignatureHelpProvider sigHelp = new(manifest);

        SignatureHelp? sig = sigHelp.GetSignatureHelp("SELECT myapp.classify(", 22);

        Assert.NotNull(sig);
        Assert.Contains("myapp.classify", sig!.Signatures[0].Label);
        Assert.Contains("INT32", sig.Signatures[0].Label);
    }

    [Fact]
    public void Lsp_SemanticAnalyzer_FlagsProcedureInSelect()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE PROCEDURE tally() AS BEGIN SELECT 1 END");

        LanguageServerManifest manifest = CatalogManifestBuilder.Build(catalog, catalog.Functions);
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT tally()", manifest);

        Assert.Contains(diagnostics, d =>
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.Contains("procedure", StringComparison.OrdinalIgnoreCase) &&
            d.Message.Contains("CALL"));
    }

    [Fact]
    public void Lsp_Hover_QualifiedUdfCall_ShowsSchemaPrefixedSignature()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE SCHEMA myapp");
        catalog.Plan("CREATE FUNCTION myapp.classify(@x INT32) RETURNS INT32 AS @x + 1");

        LanguageServerManifest manifest = CatalogManifestBuilder.Build(catalog, catalog.Functions);
        HoverProvider hover = new(manifest);

        // Cursor on "classify" in `SELECT myapp.classify(`.
        // "myapp." occupies 6 chars from start of "myapp"; "classify" starts at offset 13.
        const string sql = "SELECT myapp.classify(";
        HoverResult? result = hover.GetHover(sql, sql.IndexOf("classify", StringComparison.Ordinal));

        Assert.NotNull(result);
        Assert.Contains("myapp.classify", result!.Contents);
    }

    [Fact]
    public void Lsp_Hover_ProcedureCall_ShowsUseCallHint()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE PROCEDURE tally() AS BEGIN SELECT 1 END");

        LanguageServerManifest manifest = CatalogManifestBuilder.Build(catalog, catalog.Functions);
        HoverProvider hover = new(manifest);

        // Hovering over `tally` in the (mistaken) SELECT-position call.
        const string sql = "SELECT tally(";
        HoverResult? result = hover.GetHover(sql, sql.IndexOf("tally", StringComparison.Ordinal));

        Assert.NotNull(result);
        Assert.Contains("procedure", result!.Contents, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CALL", result.Contents);
    }

    // ───────────────────── Helpers ─────────────────────

    private static async Task<List<int>> CollectFirstColumnAsInt32Async(IQueryPlan plan)
    {
        List<int> values = new();
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            for (int r = 0; r < batch.Count; r++)
            {
                values.Add(batch[r][0].AsInt32());
            }
        }
        return values;
    }
}
