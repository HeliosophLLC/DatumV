namespace DatumIngest.LanguageServer;

using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Lazily parsed, immutable index of all embedded documentation sections.
/// Provides lookup by section key, function name, and SQL keyword.
/// Thread-safe after initialization via <see cref="Lazy{T}"/>.
/// </summary>
public sealed class DocumentationIndex
{
    private static readonly Lazy<DocumentationIndex> LazyInstance = new(Build);

    /// <summary>Singleton instance, built on first access.</summary>
    public static DocumentationIndex Instance => LazyInstance.Value;

    private readonly Dictionary<string, DocumentationSection> _sections;
    private readonly Dictionary<string, string> _functionToSection;
    private readonly Dictionary<string, string> _keywordToSection;
    private readonly List<DocumentationSectionSummary> _tableOfContents;

    private DocumentationIndex(
        Dictionary<string, DocumentationSection> sections,
        Dictionary<string, string> functionToSection,
        Dictionary<string, string> keywordToSection,
        List<DocumentationSectionSummary> tableOfContents)
    {
        _sections = sections;
        _functionToSection = functionToSection;
        _keywordToSection = keywordToSection;
        _tableOfContents = tableOfContents;
    }

    /// <summary>Returns the section for the given key, or null if not found.</summary>
    public DocumentationSection? TryGetSection(string key)
    {
        return _sections.GetValueOrDefault(key);
    }

    /// <summary>Finds the section key for a function name, or null.</summary>
    public string? FindFunctionSection(string functionName)
    {
        return _functionToSection.GetValueOrDefault(functionName);
    }

    /// <summary>Finds the section key for a SQL keyword, or null.</summary>
    public string? FindKeywordSection(string keyword)
    {
        return _keywordToSection.GetValueOrDefault(keyword);
    }

    /// <summary>Returns the table of contents as an ordered list of summaries.</summary>
    public IReadOnlyList<DocumentationSectionSummary> GetTableOfContents()
    {
        return _tableOfContents;
    }

    /// <summary>Total number of indexed sections.</summary>
    internal int SectionCount => _sections.Count;

    /// <summary>Total number of function name mappings.</summary>
    internal int FunctionMappingCount => _functionToSection.Count;

    // ───────────────────── Builder ─────────────────────

    private static DocumentationIndex Build()
    {
        Dictionary<string, DocumentationSection> sections = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> functionToSection = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> keywordToSection = new(StringComparer.OrdinalIgnoreCase);
        List<DocumentationSectionSummary> toc = [];

        Assembly assembly = typeof(DocumentationIndex).Assembly;

        // Parse all embedded sql/*.md files.
        foreach (string resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith("DatumIngest.LanguageServer.docs.", StringComparison.Ordinal))
            {
                continue;
            }

            if (resourceName.EndsWith(".yml", StringComparison.Ordinal))
            {
                continue;
            }

            string content = ReadResource(assembly, resourceName);

            // Determine source ("sql" or "functions") from the resource name.
            // Format: DatumIngest.LanguageServer.docs.{source}.{filename}.md
            string withoutPrefix = resourceName["DatumIngest.LanguageServer.docs.".Length..];
            string[] parts = withoutPrefix.Split('.');
            if (parts.Length < 3)
            {
                continue;
            }

            string source = parts[0]; // "sql" or "functions"
            string fileSlug = parts[1]; // e.g. "select", "string"

            ParseFile(content, source, fileSlug, sections, toc, functionToSection, keywordToSection);
        }

        // Sort TOC: sql first, then functions; alphabetically within each group.
        toc.Sort((a, b) =>
        {
            int sourceCompare = string.Compare(a.Source, b.Source, StringComparison.Ordinal);
            return sourceCompare != 0
                ? sourceCompare
                : string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
        });

        return new DocumentationIndex(sections, functionToSection, keywordToSection, toc);
    }

    private static void ParseFile(
        string content,
        string source,
        string fileSlug,
        Dictionary<string, DocumentationSection> sections,
        List<DocumentationSectionSummary> toc,
        Dictionary<string, string> functionToSection,
        Dictionary<string, string> keywordToSection)
    {
        // Strip YAML frontmatter.
        string body = StripFrontmatter(content);
        string[] lines = body.Split('\n');

        // Collect ### sections from function files, and ## sections from sql files.
        // For function files, each ### is a function entry.
        // For sql files, ### subsections are nested under the file-level topic.

        // Always register the file itself as a top-level section.
        string fileKey = $"{source}/{fileSlug}";
        string fileTitle = ExtractTitle(content) ?? fileSlug;
        string fileExcerpt = ExtractExcerpt(lines, 0);

        sections[fileKey] = new DocumentationSection
        {
            Key = fileKey,
            Title = fileTitle,
            Source = source,
            Excerpt = fileExcerpt,
            FullContent = body,
        };

        toc.Add(new DocumentationSectionSummary
        {
            Key = fileKey,
            Title = fileTitle,
            Source = source,
        });

        // Map SQL file titles to keyword lookups.
        if (source == "sql")
        {
            MapKeywordsFromTitle(fileTitle, fileKey, keywordToSection);
        }

        // Parse ### headings as sub-sections.
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');

            if (!line.StartsWith("### ", StringComparison.Ordinal))
            {
                continue;
            }

            string heading = line[4..].Trim();
            string slug = Slugify(heading);
            string sectionKey = $"{fileKey}/{slug}";

            // Collect content until next ## or ### heading.
            StringBuilder sectionContent = new();
            int j = i + 1;
            while (j < lines.Length)
            {
                string nextLine = lines[j].TrimEnd('\r');
                if (nextLine.StartsWith("## ", StringComparison.Ordinal) ||
                    nextLine.StartsWith("### ", StringComparison.Ordinal))
                {
                    break;
                }

                sectionContent.AppendLine(nextLine);
                j++;
            }

            string sectionBody = sectionContent.ToString().Trim();
            string excerpt = ExtractExcerptFromText(sectionBody);

            sections[sectionKey] = new DocumentationSection
            {
                Key = sectionKey,
                Title = heading,
                Source = source,
                Excerpt = excerpt,
                FullContent = $"### {heading}\n\n{sectionBody}",
            };

            // For function files, map the ### heading as a function name.
            if (source == "functions")
            {
                functionToSection.TryAdd(heading, sectionKey);
            }

            // For sql files, map subsection headings to keywords.
            if (source == "sql")
            {
                MapKeywordsFromTitle(heading, sectionKey, keywordToSection);
            }
        }
    }

    /// <summary>
    /// Maps heading text to SQL keyword lookup entries.
    /// E.g. "SELECT" → "SELECT", "LATERAL JOIN / APPLY" → "LATERAL" + "APPLY".
    /// </summary>
    private static void MapKeywordsFromTitle(string title, string sectionKey, Dictionary<string, string> keywordToSection)
    {
        // Extract uppercase words that look like SQL keywords.
        foreach (Match match in Regex.Matches(title, @"\b[A-Z_]{2,}\b"))
        {
            keywordToSection.TryAdd(match.Value, sectionKey);
        }

        // Also try the full title as a keyword (e.g. "QUALIFY", "ASSERT").
        string upper = title.Trim().ToUpperInvariant();
        if (upper.Length > 1 && upper.All(c => char.IsLetterOrDigit(c) || c == '_'))
        {
            keywordToSection.TryAdd(upper, sectionKey);
        }
    }

    /// <summary>Extracts the title from YAML frontmatter if present.</summary>
    private static string? ExtractTitle(string content)
    {
        Match match = Regex.Match(content, @"^---\s*\n.*?^title:\s*(.+?)\s*$.*?^---", RegexOptions.Multiline | RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Strips YAML frontmatter (--- ... ---) from the beginning of a file.</summary>
    private static string StripFrontmatter(string content)
    {
        if (!content.StartsWith("---", StringComparison.Ordinal))
        {
            return content;
        }

        int secondDashes = content.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (secondDashes < 0)
        {
            return content;
        }

        return content[(secondDashes + 4)..].TrimStart('\r', '\n');
    }

    /// <summary>Extracts a hover-suitable excerpt from lines starting at a given index.</summary>
    private static string ExtractExcerpt(string[] lines, int startIndex)
    {
        StringBuilder sb = new();
        bool inCodeBlock = false;

        for (int i = startIndex; i < lines.Length && sb.Length < 400; i++)
        {
            string line = lines[i].TrimEnd('\r');

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                if (inCodeBlock)
                {
                    inCodeBlock = false;
                    continue;
                }

                inCodeBlock = true;
                continue;
            }

            if (inCodeBlock)
            {
                continue;
            }

            // Skip headings, blank lines at start, HTML, and frontmatter.
            if (line.StartsWith('#') || line.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                if (sb.Length > 0)
                {
                    break; // Stop at first blank line after content.
                }

                continue;
            }

            // Skip markdown tables.
            if (line.StartsWith('|'))
            {
                continue;
            }

            // Skip function signature lines (e.g. "`sigmoid(x)` → Float32 | QU: 1").
            // These start with a backtick and contain the arrow or QU pattern.
            if (line.StartsWith('`') && (line.Contains('→') || line.Contains("QU:")))
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(line.Trim());
        }

        return TruncateAtSentence(sb.ToString(), 300);
    }

    /// <summary>Extracts an excerpt from a block of text content.</summary>
    private static string ExtractExcerptFromText(string text)
    {
        string[] lines = text.Split('\n');
        return ExtractExcerpt(lines, 0);
    }

    /// <summary>Truncates text at a sentence boundary near the target length.</summary>
    private static string TruncateAtSentence(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        // Look for sentence boundary (. ! ?) near the limit.
        int cutoff = text.LastIndexOfAny(['.', '!', '?'], maxLength - 1);
        if (cutoff > maxLength / 2)
        {
            return text[..(cutoff + 1)];
        }

        // Fall back to word boundary.
        cutoff = text.LastIndexOf(' ', maxLength - 1);
        return cutoff > 0 ? text[..cutoff] + "…" : text[..maxLength] + "…";
    }

    /// <summary>Converts a heading to a URL-safe slug (GitHub-style).</summary>
    internal static string Slugify(string heading)
    {
        string lower = heading.ToLowerInvariant();
        StringBuilder sb = new();

        foreach (char c in lower)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
            else if (c is ' ' or '-')
            {
                sb.Append('-');
            }

            // Strip all other characters (*, /, etc.)
        }

        // Collapse consecutive hyphens.
        string result = sb.ToString();
        while (result.Contains("--", StringComparison.Ordinal))
        {
            result = result.Replace("--", "-");
        }

        return result.Trim('-');
    }

    /// <summary>Reads an embedded resource as a string.</summary>
    private static string ReadResource(Assembly assembly, string resourceName)
    {
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
