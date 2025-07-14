using DatumIngest.Catalog;
using DatumIngest.Functions;
using RadLine;

namespace DatumIngest.Cli.Shell;

/// <summary>
/// Provides context-aware tab completion for the interactive shell.
/// Completes SQL keywords, table names, column names (after qualifying table),
/// function names, dot-commands, and provider names.
/// </summary>
internal sealed class ShellCompletionHandler : ITextCompletion
{
    private static readonly string[] SqlKeywords =
    [
        "SELECT", "FROM", "WHERE", "JOIN", "LEFT", "RIGHT", "FULL", "OUTER",
        "CROSS", "INNER", "ON", "AND", "OR", "NOT", "IN", "BETWEEN", "LIKE",
        "IS", "NULL", "AS", "INTO", "ORDER", "BY", "ASC", "DESC", "LIMIT",
        "OFFSET", "CAST", "SHARD", "TRUE", "FALSE"
    ];

    private static readonly string[] DotCommands =
    [
        ".tables", ".schema", ".columns", ".providers", ".functions",
        ".source", ".explain", ".sessions", ".kill", ".timer",
        ".export", ".help", ".quit", ".exit"
    ];

    private readonly TableCatalog _catalog;
    private readonly FunctionRegistry _functionRegistry;

    /// <summary>
    /// Initializes the completion handler with the catalog and function registry for name lookups.
    /// </summary>
    /// <param name="catalog">Table catalog providing table and provider names.</param>
    /// <param name="functionRegistry">Function registry providing function names.</param>
    public ShellCompletionHandler(TableCatalog catalog, FunctionRegistry functionRegistry)
    {
        _catalog = catalog;
        _functionRegistry = functionRegistry;
    }

    /// <inheritdoc />
    public IEnumerable<string>? GetCompletions(string prefix, string word, string suffix)
    {
        if (string.IsNullOrEmpty(word))
        {
            return null;
        }

        // Dot-commands: complete at the start of input.
        if (word.StartsWith('.') && string.IsNullOrWhiteSpace(prefix))
        {
            return DotCommands
                .Where(command => command.StartsWith(word, StringComparison.OrdinalIgnoreCase));
        }

        // After FROM or JOIN, complete with table names.
        string trimmedPrefix = prefix.TrimEnd();
        if (EndsWithKeyword(trimmedPrefix, "FROM") ||
            EndsWithKeyword(trimmedPrefix, "JOIN"))
        {
            return _catalog.TableNames
                .Where(name => name.StartsWith(word, StringComparison.OrdinalIgnoreCase));
        }

        // Function names: if the character after the word is '(' in suffix, or word looks like a function call.
        List<string> candidates = new();

        // Always offer SQL keywords.
        candidates.AddRange(SqlKeywords
            .Where(keyword => keyword.StartsWith(word, StringComparison.OrdinalIgnoreCase)));

        // Table names.
        candidates.AddRange(_catalog.TableNames
            .Where(name => name.StartsWith(word, StringComparison.OrdinalIgnoreCase)));

        // Function names.
        candidates.AddRange(_functionRegistry.ScalarFunctionNames
            .Where(name => name.StartsWith(word, StringComparison.OrdinalIgnoreCase)));
        candidates.AddRange(_functionRegistry.TableValuedFunctionNames
            .Where(name => name.StartsWith(word, StringComparison.OrdinalIgnoreCase)));

        return candidates.Count > 0 ? candidates.Distinct(StringComparer.OrdinalIgnoreCase) : null;
    }

    private static bool EndsWithKeyword(string text, string keyword)
    {
        if (text.Length < keyword.Length)
        {
            return false;
        }

        // Must end with the keyword, preceded by whitespace or start of string.
        if (!text.EndsWith(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int beforeKeyword = text.Length - keyword.Length;
        return beforeKeyword == 0 || char.IsWhiteSpace(text[beforeKeyword - 1]);
    }
}
