using System.Text.Json;
using System.Text.Json.Serialization;
using DatumIngest.LanguageServer;
using Microsoft.JSInterop;

namespace DatumIngest.Wasm;

/// <summary>
/// JavaScript-callable interop surface for the DatumIngest SQL language server.
/// All methods are synchronous (no I/O) and return JSON strings for simple marshaling.
/// </summary>
public sealed class LanguageServerInterop
{
    private readonly LanguageService _languageService = new();

    /// <summary>
    /// Initializes the language server with the given manifest JSON.
    /// Must be called before any other method.
    /// </summary>
    /// <param name="manifestJson">The JSON-serialized language server manifest.</param>
    [JSInvokable]
    public void Initialize(string manifestJson)
    {
        _languageService.Initialize(manifestJson);
    }

    /// <summary>
    /// Returns completion items as a JSON array for the given SQL text and cursor offset.
    /// </summary>
    /// <param name="sql">The SQL text in the editor.</param>
    /// <param name="cursorOffset">The 0-based character offset of the cursor.</param>
    /// <returns>A JSON string representing an array of completion items.</returns>
    [JSInvokable]
    public string GetCompletions(string sql, int cursorOffset)
    {
        CompletionItem[] items = _languageService.GetCompletions(sql, cursorOffset);
        return JsonSerializer.Serialize(items, InteropJsonContext.Default.CompletionItemArray);
    }

    /// <summary>
    /// Returns diagnostics as a JSON array for the given SQL text.
    /// </summary>
    /// <param name="sql">The SQL text to analyze.</param>
    /// <returns>A JSON string representing an array of diagnostics.</returns>
    [JSInvokable]
    public string GetDiagnostics(string sql)
    {
        Diagnostic[] diagnostics = _languageService.GetDiagnostics(sql);
        return JsonSerializer.Serialize(diagnostics, InteropJsonContext.Default.DiagnosticArray);
    }

    /// <summary>
    /// Returns hover information as a JSON object for the token at the given cursor offset,
    /// or "null" if there is nothing to display.
    /// </summary>
    /// <param name="sql">The SQL text in the editor.</param>
    /// <param name="cursorOffset">The 0-based character offset of the cursor.</param>
    /// <returns>A JSON string representing a hover result, or the string "null".</returns>
    [JSInvokable]
    public string GetHover(string sql, int cursorOffset)
    {
        HoverResult? result = _languageService.GetHover(sql, cursorOffset);
        return JsonSerializer.Serialize(result, InteropJsonContext.Default.HoverResult);
    }

    /// <summary>
    /// Returns the full documentation section for the given key, or "null" if not found.
    /// Does not require <see cref="Initialize"/> — documentation is static.
    /// </summary>
    /// <param name="sectionKey">The section key (e.g. "sql/select", "functions/string/upper").</param>
    /// <returns>A JSON string representing a documentation section, or the string "null".</returns>
    [JSInvokable]
    public string GetDocSection(string sectionKey)
    {
        DocumentationSection? section = LanguageService.GetDocSection(sectionKey);
        return JsonSerializer.Serialize(section, InteropJsonContext.Default.DocumentationSection);
    }

    /// <summary>
    /// Returns all documentation section keys and titles for building a table of contents.
    /// Does not require <see cref="Initialize"/> — documentation is static.
    /// </summary>
    /// <returns>A JSON string representing an array of documentation section summaries.</returns>
    [JSInvokable]
    public string GetDocTableOfContents()
    {
        IReadOnlyList<DocumentationSectionSummary> toc = LanguageService.GetDocTableOfContents();
        return JsonSerializer.Serialize(toc, InteropJsonContext.Default.IReadOnlyListDocumentationSectionSummary);
    }
}

/// <summary>
/// Source-generated JSON serializer context for the WASM interop response types.
/// </summary>
[JsonSerializable(typeof(CompletionItem[]))]
[JsonSerializable(typeof(Diagnostic[]))]
[JsonSerializable(typeof(HoverResult))]
[JsonSerializable(typeof(DocumentationSection))]
[JsonSerializable(typeof(IReadOnlyList<DocumentationSectionSummary>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class InteropJsonContext : JsonSerializerContext
{
}
