using System.Text.RegularExpressions;

namespace DatumIngest.Parsing.Tokens;

/// <summary>
/// Utilities for determining whether a SQL identifier requires quoting
/// and for applying bracket-quoting when necessary.
/// </summary>
public static partial class SqlIdentifier
{
    /// <summary>
    /// The set of reserved SQL keywords that cannot be used as bare identifiers.
    /// </summary>
    private static readonly HashSet<string> ReservedKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "INTO", "FROM", "JOIN", "LEFT", "RIGHT", "FULL", "OUTER",
        "CROSS", "INNER", "ON", "WHERE", "AND", "OR", "NOT", "IN", "BETWEEN",
        "LIKE", "IS", "NULL", "AS", "SHARD", "ORDER", "BY", "ASC", "DESC",
        "LIMIT", "OFFSET", "CAST", "TRUE", "FALSE", "LET",
        "CASE", "WHEN", "THEN", "ELSE", "END",
        "OVER", "PARTITION", "ROWS", "UNBOUNDED",
        "PRECEDING", "FOLLOWING", "CURRENT"
    };

    /// <summary>
    /// Returns <see langword="true"/> when the name is not a valid bare SQL
    /// identifier — it contains special characters, starts with a digit,
    /// or collides with a reserved keyword.
    /// </summary>
    public static bool NeedsQuoting(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return true;
        }

        if (!BareIdentifierPattern().IsMatch(name))
        {
            return true;
        }

        return ReservedKeywords.Contains(name);
    }

    /// <summary>
    /// Returns the name wrapped in double quotes (PostgreSQL convention) if it
    /// requires quoting, or the original name if it is a valid bare identifier.
    /// Embedded double-quote characters are escaped by doubling.
    /// </summary>
    public static string QuoteIfNeeded(string name)
    {
        return NeedsQuoting(name)
            ? $"\"{name.Replace("\"", "\"\"")}\""
            : name;
    }

    /// <summary>
    /// Strips surrounding double-quote or single-quote delimiters from a name
    /// if present, un-escaping doubled characters. Returns the bare name otherwise.
    /// </summary>
    public static string Unquote(string name)
    {
        if (name.Length >= 2)
        {
            if (name[0] == '"' && name[^1] == '"')
                return name[1..^1].Replace("\"\"", "\"");
            if (name[0] == '\'' && name[^1] == '\'')
                return name[1..^1].Replace("''", "'");
        }

        return name;
    }

    /// <summary>Matches a valid C-style identifier: <c>[a-zA-Z_][a-zA-Z0-9_]*</c>.</summary>
    [GeneratedRegex(@"^[a-zA-Z_][a-zA-Z0-9_]*$")]
    private static partial Regex BareIdentifierPattern();
}
