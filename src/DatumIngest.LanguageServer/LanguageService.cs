namespace DatumIngest.LanguageServer;

using DatumIngest.Manifest;

/// <summary>
/// The top-level facade for DatumIngest SQL language intelligence.
/// Transport-agnostic: can be called directly from WASM interop or wrapped
/// in an LSP server for VS Code integration.
/// </summary>
public sealed class LanguageService
{
    private LanguageServerManifest? _manifest;
    private CompletionProvider? _completionProvider;
    private HoverProvider? _hoverProvider;

    /// <summary>
    /// Initializes the language service with a manifest JSON string.
    /// Must be called before any other method.
    /// </summary>
    /// <param name="manifestJson">The JSON-serialized <see cref="LanguageServerManifest"/>.</param>
    /// <exception cref="InvalidOperationException">Thrown if the manifest cannot be deserialized.</exception>
    public void Initialize(string manifestJson)
    {
        _manifest = LanguageServerManifestSerializer.Deserialize(manifestJson)
            ?? throw new InvalidOperationException("Failed to deserialize language server manifest.");

        _completionProvider = new CompletionProvider(_manifest);
        _hoverProvider = new HoverProvider(_manifest);
    }

    /// <summary>
    /// Returns completion items for the given SQL text and cursor offset.
    /// </summary>
    /// <param name="sql">The full SQL text in the editor.</param>
    /// <param name="cursorOffset">The 0-based character offset of the cursor.</param>
    /// <returns>An array of completion items.</returns>
    /// <exception cref="InvalidOperationException">Thrown if <see cref="Initialize"/> has not been called.</exception>
    public CompletionItem[] GetCompletions(string sql, int cursorOffset)
    {
        EnsureInitialized();
        return _completionProvider!.GetCompletions(sql, cursorOffset);
    }

    /// <summary>
    /// Returns parse error diagnostics and semantic warnings for the given SQL text.
    /// When the service has been initialized with a manifest, unknown table,
    /// column, and function references are reported as warnings.
    /// </summary>
    /// <param name="sql">The SQL text to analyze.</param>
    /// <returns>An array of diagnostics.</returns>
    public Diagnostic[] GetDiagnostics(string sql)
    {
        return DiagnosticsProvider.GetDiagnostics(sql, _manifest);
    }

    /// <summary>
    /// Returns hover information for the token at the given cursor offset, or null
    /// if there is nothing meaningful to display.
    /// </summary>
    /// <param name="sql">The full SQL text in the editor.</param>
    /// <param name="cursorOffset">The 0-based character offset of the cursor.</param>
    /// <returns>A hover result with Markdown content, or null.</returns>
    /// <exception cref="InvalidOperationException">Thrown if <see cref="Initialize"/> has not been called.</exception>
    public HoverResult? GetHover(string sql, int cursorOffset)
    {
        EnsureInitialized();
        return _hoverProvider!.GetHover(sql, cursorOffset);
    }

    /// <summary>
    /// Returns whether the service has been initialized with a manifest.
    /// </summary>
    public bool IsInitialized => _manifest is not null;

    private void EnsureInitialized()
    {
        if (_manifest is null)
        {
            throw new InvalidOperationException(
                "LanguageService has not been initialized. Call Initialize(manifestJson) first.");
        }
    }
}
