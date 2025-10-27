namespace DatumIngest.Tests.LanguageServer;

using System.Text.Json;
using DatumIngest.LanguageServer;
using DatumIngest.Manifest;

/// <summary>
/// Tests for <see cref="LanguageService"/> — the top-level facade coordinating
/// completions, diagnostics, and hover through a single entry point.
/// </summary>
public sealed class LanguageServiceTests : ServiceTestBase
{
    private static string CreateTestManifestJson()
    {
        LanguageServerManifest manifest = new()
        {
            Tables =
            [
                new TableSchemaEntry
                {
                    Name = "products",
                    Columns =
                    [
                        new TableColumnEntry { Name = "sku", Kind = "String", Nullable = false },
                        new TableColumnEntry { Name = "price", Kind = "Float32", Nullable = false },
                    ],
                },
            ],
            Functions =
            [
                new FunctionSignature
                {
                    Name = "round",
                    Parameters = [new ParameterSignature { Name = "value", Kind = "Float32" }],
                    ReturnType = "Float32",
                    Description = "Rounds to nearest integer.",
                },
            ],
            Keywords = ["SELECT", "FROM", "WHERE"],
        };

        return LanguageServerManifestSerializer.Serialize(manifest);
    }

    private static LanguageService CreateInitializedService()
    {
        LanguageService service = new();
        service.Initialize(CreateTestManifestJson());
        return service;
    }

    // ───────────────────── Initialization ─────────────────────

    [Fact]
    public void Initialize_ValidJson_SetsIsInitialized()
    {
        LanguageService service = new();

        Assert.False(service.IsInitialized);

        service.Initialize(CreateTestManifestJson());

        Assert.True(service.IsInitialized);
    }

    [Fact]
    public void GetCompletions_BeforeInitialize_ThrowsInvalidOperation()
    {
        LanguageService service = new();

        Assert.Throws<InvalidOperationException>(() => service.GetCompletions("SELECT ", 7));
    }

    [Fact]
    public void GetHover_BeforeInitialize_ThrowsInvalidOperation()
    {
        LanguageService service = new();

        Assert.Throws<InvalidOperationException>(() => service.GetHover("SELECT", 0));
    }

    // ───────────────────── Completions through facade ─────────────────────

    [Fact]
    public void GetCompletions_AfterInit_ReturnsItems()
    {
        LanguageService service = CreateInitializedService();

        CompletionItem[] items = service.GetCompletions("SELECT ", 7);

        Assert.NotEmpty(items);
        Assert.Contains(items, item => item.Label == "sku");
        Assert.Contains(items, item => item.Label == "round");
    }

    // ───────────────────── Diagnostics through facade ─────────────────────

    [Fact]
    public void GetDiagnostics_ValidSql_ReturnsEmpty()
    {
        LanguageService service = CreateInitializedService();

        Diagnostic[] diagnostics = service.GetDiagnostics("SELECT sku FROM products");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void GetDiagnostics_InvalidSql_ReturnsDiagnostic()
    {
        LanguageService service = CreateInitializedService();

        Diagnostic[] diagnostics = service.GetDiagnostics("SELEKT sku");

        Assert.NotEmpty(diagnostics);
    }

    [Fact]
    public void GetDiagnostics_DoesNotRequireInitialization()
    {
        // Diagnostics only use the parser, not the manifest, so they work without init.
        LanguageService service = new();

        Diagnostic[] diagnostics = service.GetDiagnostics("SELECT x FROM t");

        Assert.Empty(diagnostics);
    }

    // ───────────────────── Hover through facade ─────────────────────

    [Fact]
    public void GetHover_KnownTable_ReturnsHoverResult()
    {
        LanguageService service = CreateInitializedService();

        HoverResult? result = service.GetHover("SELECT * FROM products", 14);

        Assert.NotNull(result);
        Assert.Contains("products", result.Contents);
    }
}
