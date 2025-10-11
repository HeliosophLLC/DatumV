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

    /// <summary>
    /// Returns <see langword="true"/> when the name span requires quoting.
    /// Span-based overload that avoids allocating a managed string.
    /// </summary>
    public static bool NeedsQuoting(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty) return true;

        if (!IsBareIdentifier(name)) return true;

        // HashSet doesn't support span lookup directly — allocate only for the keyword check.
        return ReservedKeywords.Contains(name.ToString());
    }

    /// <summary>
    /// Writes the quoted form of an identifier into the destination span if quoting is needed,
    /// or copies the original name. Returns the number of chars written.
    /// </summary>
    /// <param name="name">The identifier to quote.</param>
    /// <param name="destination">Buffer to write the result into. Must be large enough
    /// (worst case: <c>name.Length * 2 + 2</c> for all-quote characters).</param>
    /// <returns>The number of chars written to <paramref name="destination"/>.</returns>
    public static int QuoteIfNeeded(ReadOnlySpan<char> name, Span<char> destination)
    {
        if (!NeedsQuoting(name))
        {
            name.CopyTo(destination);
            return name.Length;
        }

        int pos = 0;
        destination[pos++] = '"';
        foreach (char c in name)
        {
            if (c == '"') destination[pos++] = '"';
            destination[pos++] = c;
        }
        destination[pos++] = '"';
        return pos;
    }

    /// <summary>
    /// Returns <see langword="true"/> when every character in the span is a valid
    /// bare identifier character: <c>[a-zA-Z_][a-zA-Z0-9_]*</c>.
    /// </summary>
    private static bool IsBareIdentifier(ReadOnlySpan<char> name)
    {
        char first = name[0];
        if (!((first >= 'a' && first <= 'z') || (first >= 'A' && first <= 'Z') || first == '_'))
            return false;

        for (int i = 1; i < name.Length; i++)
        {
            char c = name[i];
            if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_'))
                return false;
        }

        return true;
    }

    /// <summary>Matches a valid C-style identifier: <c>[a-zA-Z_][a-zA-Z0-9_]*</c>.</summary>
    [GeneratedRegex(@"^[a-zA-Z_][a-zA-Z0-9_]*$")]
    private static partial Regex BareIdentifierPattern();
}
