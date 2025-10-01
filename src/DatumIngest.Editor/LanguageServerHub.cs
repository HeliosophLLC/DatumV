using System.Collections.Concurrent;
using DatumIngest.LanguageServer;
using Microsoft.AspNetCore.SignalR;

namespace DatumIngest.Editor;

/// <summary>
/// SignalR hub that exposes the DatumIngest SQL language server over WebSocket.
/// Each connection maintains its own <see cref="LanguageService"/> instance,
/// initialized with a client-provided manifest. This mirrors the per-tab
/// isolation of the WASM approach while running server-side.
/// </summary>
public sealed class LanguageServerHub : Hub
{
    /// <summary>
    /// Connection-scoped language service instances. Keyed by connection ID,
    /// cleaned up in <see cref="OnDisconnectedAsync"/>.
    /// </summary>
    private static readonly ConcurrentDictionary<string, LanguageService> Services = new();

    /// <summary>
    /// Initializes the language service for this connection with the given manifest JSON.
    /// Must be called before <see cref="GetCompletions"/>, <see cref="GetDiagnostics"/>,
    /// or <see cref="GetHover"/>.
    /// </summary>
    /// <param name="manifestJson">
    /// The JSON-serialized language server manifest containing table schemas,
    /// function signatures, and keywords.
    /// </param>
    public void Initialize(string manifestJson)
    {
        LanguageService service = GetOrCreateService();
        service.Initialize(manifestJson);
    }

    /// <summary>
    /// Returns completion items for the given SQL text and cursor offset.
    /// </summary>
    /// <param name="sql">The full SQL text in the editor.</param>
    /// <param name="cursorOffset">The 0-based character offset of the cursor.</param>
    /// <returns>An array of completion items.</returns>
    /// <exception cref="HubException">Thrown if <see cref="Initialize"/> has not been called.</exception>
    public CompletionItem[] GetCompletions(string sql, int cursorOffset)
    {
        LanguageService service = GetInitializedService();
        return service.GetCompletions(sql, cursorOffset);
    }

    /// <summary>
    /// Returns parse error diagnostics and semantic warnings for the given SQL text.
    /// </summary>
    /// <param name="sql">The SQL text to analyze.</param>
    /// <returns>An array of diagnostics.</returns>
    public Diagnostic[] GetDiagnostics(string sql)
    {
        LanguageService service = GetInitializedService();
        return service.GetDiagnostics(sql);
    }

    /// <summary>
    /// Returns hover information for the token at the given cursor offset, or
    /// <see langword="null"/> if there is nothing meaningful to display.
    /// </summary>
    /// <param name="sql">The full SQL text in the editor.</param>
    /// <param name="cursorOffset">The 0-based character offset of the cursor.</param>
    /// <returns>A hover result with Markdown content, or null.</returns>
    /// <exception cref="HubException">Thrown if <see cref="Initialize"/> has not been called.</exception>
    public HoverResult? GetHover(string sql, int cursorOffset)
    {
        LanguageService service = GetInitializedService();
        return service.GetHover(sql, cursorOffset);
    }

    /// <summary>
    /// Returns the Monarch grammar definition for the DatumIngest SQL dialect.
    /// Pass the result directly to <c>monaco.languages.setMonarchTokensProvider</c>
    /// to enable client-side syntax highlighting. Does not require
    /// <see cref="Initialize"/> to have been called — the grammar is static and
    /// independent of the schema manifest.
    /// </summary>
    /// <returns>
    /// An object graph that serializes to a valid Monarch grammar JSON document.
    /// </returns>
    public object GetMonarchGrammar()
    {
        return MonarchGrammarFactory.Build();
    }

    /// <summary>
    /// Returns the full documentation section for the given key, or null if not found.
    /// Does not require <see cref="Initialize"/> — documentation is static and
    /// independent of the schema manifest.
    /// </summary>
    /// <param name="sectionKey">The section key (e.g. "sql/select", "functions/string/upper").</param>
    public DocumentationSection? GetDocSection(string sectionKey)
    {
        return LanguageService.GetDocSection(sectionKey);
    }

    /// <summary>
    /// Returns all documentation section keys and titles for building a table of contents.
    /// Does not require <see cref="Initialize"/> — documentation is static.
    /// </summary>
    public IReadOnlyList<DocumentationSectionSummary> GetDocTableOfContents()
    {
        return LanguageService.GetDocTableOfContents();
    }

    /// <inheritdoc/>
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        Services.TryRemove(Context.ConnectionId, out _);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Returns the <see cref="LanguageService"/> for this connection, creating
    /// one if it does not already exist.
    /// </summary>
    private LanguageService GetOrCreateService()
    {
        return Services.GetOrAdd(Context.ConnectionId, _ => new LanguageService());
    }

    /// <summary>
    /// Returns the <see cref="LanguageService"/> for this connection, throwing
    /// a <see cref="HubException"/> if the connection has not been initialized.
    /// </summary>
    private LanguageService GetInitializedService()
    {
        if (!Services.TryGetValue(Context.ConnectionId, out LanguageService? service) || !service.IsInitialized)
        {
            throw new HubException(
                "Language server has not been initialized. Call Initialize(manifestJson) first.");
        }

        return service;
    }
}
