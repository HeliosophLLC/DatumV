namespace DatumIngest.Tests.Editor;

using System.Security.Claims;
using DatumIngest.Editor;
using DatumIngest.LanguageServer;
using DatumIngest.Manifest;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;

/// <summary>
/// Tests for <see cref="LanguageServerHub"/> — the SignalR hub wrapping
/// <see cref="LanguageService"/> for server-side language intelligence.
/// </summary>
public sealed class LanguageServerHubTests : IAsyncLifetime
{
    private readonly LanguageServerHub _hub;
    private readonly string _connectionId = Guid.NewGuid().ToString("N");

    /// <summary>Creates a hub instance with a stubbed context.</summary>
    public LanguageServerHubTests()
    {
        _hub = new LanguageServerHub();
        HubCallerContext context = new StubHubCallerContext(_connectionId);
        // Hub.Context has a public setter used by the SignalR framework;
        // we assign it directly to avoid needing a full test server.
        _hub.Context = context;
    }

    /// <inheritdoc/>
    public Task InitializeAsync() => Task.CompletedTask;

    /// <inheritdoc/>
    public async Task DisposeAsync()
    {
        await _hub.OnDisconnectedAsync(null);
        _hub.Dispose();
    }

    private static string CreateTestManifestJson()
    {
        LanguageServerManifest manifest = new()
        {
            Tables =
            [
                new TableSchemaEntry
                {
                    Name = "sensors",
                    Columns =
                    [
                        new TableColumnEntry { Name = "id", Kind = "Scalar", Nullable = false },
                        new TableColumnEntry { Name = "reading", Kind = "Scalar", Nullable = true },
                    ],
                },
            ],
            Functions =
            [
                new FunctionSignature
                {
                    Name = "abs",
                    Parameters = [new ParameterSignature { Name = "value", Kind = "Scalar" }],
                    ReturnType = "Scalar",
                    Description = "Absolute value.",
                },
            ],
            Keywords = ["SELECT", "FROM", "WHERE"],
        };

        return LanguageServerManifestSerializer.Serialize(manifest);
    }

    // ───────────────────── Initialization ─────────────────────

    [Fact]
    public void GetCompletions_BeforeInitialize_ThrowsHubException()
    {
        HubException exception = Assert.Throws<HubException>(
            () => _hub.GetCompletions("SELECT ", 7));

        Assert.Contains("Initialize", exception.Message);
    }

    [Fact]
    public void GetDiagnostics_BeforeInitialize_ThrowsHubException()
    {
        HubException exception = Assert.Throws<HubException>(
            () => _hub.GetDiagnostics("SELECT x FROM t"));

        Assert.Contains("Initialize", exception.Message);
    }

    [Fact]
    public void GetHover_BeforeInitialize_ThrowsHubException()
    {
        HubException exception = Assert.Throws<HubException>(
            () => _hub.GetHover("SELECT x FROM t", 0));

        Assert.Contains("Initialize", exception.Message);
    }

    [Fact]
    public void Initialize_ThenGetCompletions_Succeeds()
    {
        _hub.Initialize(CreateTestManifestJson());

        CompletionItem[] completions = _hub.GetCompletions("SELECT ", 7);

        Assert.NotEmpty(completions);
    }

    // ───────────────────── Completions ─────────────────────

    [Fact]
    public void GetCompletions_AfterSelect_ReturnsColumnNames()
    {
        _hub.Initialize(CreateTestManifestJson());

        CompletionItem[] completions = _hub.GetCompletions("SELECT ", 7);

        Assert.Contains(completions, item => item.Label == "id");
        Assert.Contains(completions, item => item.Label == "reading");
    }

    [Fact]
    public void GetCompletions_AfterFrom_ReturnsTableNames()
    {
        _hub.Initialize(CreateTestManifestJson());

        CompletionItem[] completions = _hub.GetCompletions("SELECT x FROM ", 14);

        Assert.Contains(completions, item => item.Label == "sensors");
    }

    // ───────────────────── Diagnostics ─────────────────────

    [Fact]
    public void GetDiagnostics_ValidSql_ReturnsEmpty()
    {
        _hub.Initialize(CreateTestManifestJson());

        Diagnostic[] diagnostics = _hub.GetDiagnostics("SELECT id FROM sensors");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void GetDiagnostics_InvalidSql_ReturnsError()
    {
        _hub.Initialize(CreateTestManifestJson());

        Diagnostic[] diagnostics = _hub.GetDiagnostics("SELEKT x");

        Assert.NotEmpty(diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, diagnostics[0].Severity);
    }

    [Fact]
    public void GetDiagnostics_UnknownTable_ReturnsWarning()
    {
        _hub.Initialize(CreateTestManifestJson());

        Diagnostic[] diagnostics = _hub.GetDiagnostics("SELECT x FROM ghost");

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("ghost"));
    }

    [Fact]
    public void GetDiagnostics_UnknownColumn_ReturnsWarning()
    {
        _hub.Initialize(CreateTestManifestJson());

        Diagnostic[] diagnostics = _hub.GetDiagnostics("SELECT phantom FROM sensors");

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("phantom"));
    }

    // ───────────────────── Hover ─────────────────────

    [Fact]
    public void GetHover_OverKeyword_ReturnsResult()
    {
        _hub.Initialize(CreateTestManifestJson());

        HoverResult? hover = _hub.GetHover("SELECT id FROM sensors", 0);

        Assert.NotNull(hover);
    }

    [Fact]
    public void GetHover_OverWhitespace_ReturnsNull()
    {
        _hub.Initialize(CreateTestManifestJson());

        // Cursor on the space between SELECT and id.
        HoverResult? hover = _hub.GetHover("SELECT id FROM sensors", 6);

        Assert.Null(hover);
    }

    // ───────────────────── Connection isolation ─────────────────────

    [Fact]
    public async Task SeparateConnections_HaveIndependentState()
    {
        // Connection A — initialized.
        _hub.Initialize(CreateTestManifestJson());

        // Connection B — separate hub with different connection ID.
        using LanguageServerHub hubB = new();
        hubB.Context = new StubHubCallerContext(Guid.NewGuid().ToString("N"));

        // B has not called Initialize, so it should throw.
        Assert.Throws<HubException>(() => hubB.GetCompletions("SELECT ", 7));

        // A should still work.
        CompletionItem[] completions = _hub.GetCompletions("SELECT ", 7);
        Assert.NotEmpty(completions);

        // Clean up B.
        await hubB.OnDisconnectedAsync(null);
    }

    // ───────────────────── Disconnect cleanup ─────────────────────

    [Fact]
    public async Task OnDisconnectedAsync_CleansUpState()
    {
        _hub.Initialize(CreateTestManifestJson());

        // Verify it works before disconnect.
        CompletionItem[] completions = _hub.GetCompletions("SELECT ", 7);
        Assert.NotEmpty(completions);

        // Disconnect.
        await _hub.OnDisconnectedAsync(null);

        // After disconnect, calling methods should throw (no service in map).
        Assert.Throws<HubException>(() => _hub.GetCompletions("SELECT ", 7));
    }

    // ───────────────────── Monarch grammar ─────────────────────

    /// <summary>
    /// GetMonarchGrammar succeeds without Initialize being called first.
    /// </summary>
    [Fact]
    public void GetMonarchGrammar_DoesNotRequireInitialization()
    {
        // Do NOT call Initialize — grammar must work statically.
        object grammar = _hub.GetMonarchGrammar();

        Assert.NotNull(grammar);
    }

    /// <summary>
    /// The grammar contains a non-empty keywords array covering all SQL clause keywords.
    /// </summary>
    [Fact]
    public void GetMonarchGrammar_ContainsExpectedKeywords()
    {
        object grammar = _hub.GetMonarchGrammar();
        string json = System.Text.Json.JsonSerializer.Serialize(grammar);
        System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(json);

        System.Text.Json.JsonElement keywords = document.RootElement.GetProperty("keywords");
        Assert.Equal(System.Text.Json.JsonValueKind.Array, keywords.ValueKind);
        Assert.True(keywords.GetArrayLength() > 0);

        // Spot-check a representative cross-section of the token inventory.
        List<string> keywordList = [];
        foreach (System.Text.Json.JsonElement element in keywords.EnumerateArray())
        {
            keywordList.Add(element.GetString()!);
        }

        Assert.Contains("SELECT", keywordList);
        Assert.Contains("GROUP", keywordList);
        Assert.Contains("QUALIFY", keywordList);
        Assert.Contains("OVER", keywordList);
        Assert.Contains("WITH", keywordList);
        Assert.Contains("LET", keywordList);
        Assert.Contains("PIVOT", keywordList);
        Assert.Contains("UNPIVOT", keywordList);
    }

    /// <summary>
    /// TRUE, FALSE, and NULL appear in a separate boolNullKeywords array, not in
    /// the main keywords array, so themes can color them distinctly.
    /// </summary>
    [Fact]
    public void GetMonarchGrammar_BoolNullKeywords_InSeparateCategory()
    {
        object grammar = _hub.GetMonarchGrammar();
        string json = System.Text.Json.JsonSerializer.Serialize(grammar);
        System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(json);

        List<string> keywords = [];
        foreach (System.Text.Json.JsonElement element in document.RootElement.GetProperty("keywords").EnumerateArray())
        {
            keywords.Add(element.GetString()!);
        }

        List<string> boolNullKeywords = [];
        foreach (System.Text.Json.JsonElement element in document.RootElement.GetProperty("boolNullKeywords").EnumerateArray())
        {
            boolNullKeywords.Add(element.GetString()!);
        }

        // TRUE/FALSE/NULL must be in boolNullKeywords, not in keywords.
        Assert.DoesNotContain("TRUE", keywords);
        Assert.DoesNotContain("FALSE", keywords);
        Assert.DoesNotContain("NULL", keywords);
        Assert.Contains("TRUE", boolNullKeywords);
        Assert.Contains("FALSE", boolNullKeywords);
        Assert.Contains("NULL", boolNullKeywords);
    }

    /// <summary>
    /// The grammar serializes to valid JSON with the required Monarch top-level keys.
    /// </summary>
    [Fact]
    public void GetMonarchGrammar_SerializesToValidMonarchShape()
    {
        object grammar = _hub.GetMonarchGrammar();
        string json = System.Text.Json.JsonSerializer.Serialize(grammar);
        System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(json);

        // All top-level Monarch keys required for Monaco registration.
        Assert.True(document.RootElement.TryGetProperty("keywords", out _));
        Assert.True(document.RootElement.TryGetProperty("boolNullKeywords", out _));
        Assert.True(document.RootElement.TryGetProperty("tokenizer", out System.Text.Json.JsonElement tokenizer));
        Assert.True(tokenizer.TryGetProperty("root", out _));
        Assert.True(tokenizer.TryGetProperty("whitespace", out _));
        Assert.True(tokenizer.TryGetProperty("blockComment", out _));
    }

    // ───────────────────── Reinitialize ─────────────────────

    [Fact]
    public void Initialize_CalledTwice_UsesLatestManifest()
    {
        // First init with "sensors" table.
        _hub.Initialize(CreateTestManifestJson());

        // Second init with different table.
        LanguageServerManifest secondManifest = new()
        {
            Tables =
            [
                new TableSchemaEntry
                {
                    Name = "metrics",
                    Columns =
                    [
                        new TableColumnEntry { Name = "value", Kind = "Scalar", Nullable = false },
                    ],
                },
            ],
            Functions = [],
            Keywords = ["SELECT", "FROM"],
        };
        _hub.Initialize(LanguageServerManifestSerializer.Serialize(secondManifest));

        CompletionItem[] completions = _hub.GetCompletions("SELECT x FROM ", 14);

        // Should offer "metrics" (new manifest), not "sensors" (old).
        Assert.Contains(completions, item => item.Label == "metrics");
        Assert.DoesNotContain(completions, item => item.Label == "sensors");
    }

    // ───────────────────── Stub types ─────────────────────

    /// <summary>
    /// Minimal <see cref="HubCallerContext"/> stub for testing without a
    /// full SignalR pipeline.
    /// </summary>
    private sealed class StubHubCallerContext : HubCallerContext
    {
        public StubHubCallerContext(string connectionId)
        {
            ConnectionId = connectionId;
        }

        public override string ConnectionId { get; }
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public override void Abort()
        {
        }
    }
}
