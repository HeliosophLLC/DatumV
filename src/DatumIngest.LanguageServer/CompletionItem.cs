namespace DatumIngest.LanguageServer;

/// <summary>
/// A completion suggestion returned to the editor. Field names align with the
/// LSP CompletionItem specification for minimal mapping in a future VS Code extension.
/// </summary>
public sealed class CompletionItem
{
    /// <summary>The display text for this completion.</summary>
    public required string Label { get; init; }

    /// <summary>The kind of completion (keyword, table, column, function).</summary>
    public required CompletionItemKind Kind { get; init; }

    /// <summary>A short detail string shown alongside the label (e.g. type information).</summary>
    public string? Detail { get; init; }

    /// <summary>The text to insert when the completion is accepted. Defaults to <see cref="Label"/> if null.</summary>
    public string? InsertText { get; init; }

    /// <summary>Extended documentation or signature shown in a secondary panel.</summary>
    public string? Documentation { get; init; }

    /// <summary>Sort order hint; lower values sort first. Used to prioritize context-relevant items.</summary>
    public int SortOrder { get; init; }
}

/// <summary>
/// Classification of a completion item, aligned with LSP CompletionItemKind values.
/// </summary>
public enum CompletionItemKind
{
    /// <summary>A SQL keyword (SELECT, FROM, WHERE, etc.).</summary>
    Keyword = 14,

    /// <summary>A table name from the schema manifest.</summary>
    Table = 22,

    /// <summary>A column name from a table schema.</summary>
    Column = 5,

    /// <summary>A scalar or table-valued function.</summary>
    Function = 3,

    /// <summary>A data type name (used in CAST expressions).</summary>
    TypeParameter = 25,

    /// <summary>A procedural variable (@-prefixed) declared earlier in the batch.</summary>
    Variable = 6,

    /// <summary>
    /// A schema / namespace name (<c>inference</c>, <c>tokenizer</c>,
    /// <c>templates</c>, <c>models</c>). Surfaced so the user can pick a
    /// schema with partial typing and drill in with <c>.</c>.
    /// Maps to LSP <c>CompletionItemKind.Module = 9</c>.
    /// </summary>
    Schema = 9,
}
