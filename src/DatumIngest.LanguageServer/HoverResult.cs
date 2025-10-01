namespace DatumIngest.LanguageServer;

/// <summary>
/// Hover information returned to the editor when the user hovers over a token.
/// Field names align with the LSP Hover specification.
/// </summary>
public sealed class HoverResult
{
    /// <summary>The hover content formatted as Markdown.</summary>
    public required string Contents { get; init; }

    /// <summary>The 0-based line of the hovered token start.</summary>
    public required int StartLine { get; init; }

    /// <summary>The 0-based column of the hovered token start.</summary>
    public required int StartColumn { get; init; }

    /// <summary>The 0-based line of the hovered token end.</summary>
    public required int EndLine { get; init; }

    /// <summary>The 0-based column of the hovered token end (exclusive).</summary>
    public required int EndColumn { get; init; }

    /// <summary>
    /// Optional documentation section key for "See more" navigation (e.g. "sql/select",
    /// "functions/string/upper"). The host application can use this to open the full
    /// documentation page. Null when no matching documentation section exists.
    /// </summary>
    public string? DocumentationUri { get; init; }
}
