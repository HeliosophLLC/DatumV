namespace DatumIngest.LanguageServer;

/// <summary>
/// A single documentation section parsed from a markdown reference file.
/// </summary>
public sealed class DocumentationSection
{
    /// <summary>Unique key for this section (e.g. "sql/select", "functions/string/upper").</summary>
    public required string Key { get; init; }

    /// <summary>The section heading text without the markdown prefix.</summary>
    public required string Title { get; init; }

    /// <summary>The source directory identifier ("sql" or "functions").</summary>
    public required string Source { get; init; }

    /// <summary>A short excerpt (first paragraph, up to ~300 characters) for hover tooltips.</summary>
    public required string Excerpt { get; init; }

    /// <summary>The full markdown content of this section.</summary>
    public required string FullContent { get; init; }
}

/// <summary>
/// Lightweight summary for building a table of contents without loading full content.
/// </summary>
public sealed class DocumentationSectionSummary
{
    /// <summary>Unique key matching <see cref="DocumentationSection.Key"/>.</summary>
    public required string Key { get; init; }

    /// <summary>The section heading text.</summary>
    public required string Title { get; init; }

    /// <summary>The source directory identifier ("sql" or "functions").</summary>
    public required string Source { get; init; }
}
