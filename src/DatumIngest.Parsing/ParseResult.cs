namespace DatumIngest.Parsing;

using DatumIngest.Parsing.Ast;

/// <summary>
/// The result of parsing SQL text, containing either a valid AST, a partial
/// AST with errors, or just errors when recovery was not possible.
/// </summary>
public sealed class ParseResult
{
    /// <summary>
    /// The parsed query expression. May be <see langword="null"/> if parsing failed entirely
    /// and no partial tree could be recovered. When errors are present, the tree
    /// may contain <see cref="ErrorExpression"/> placeholder nodes where recovery
    /// skipped unparseable input.
    /// </summary>
    public QueryExpression? Query { get; }

    /// <summary>
    /// The parsed AST as a <see cref="SelectStatement"/>, for backward compatibility.
    /// Returns the statement from a <see cref="SelectQueryExpression"/>, or <see langword="null"/>
    /// if the query is a compound expression or parsing failed.
    /// </summary>
    public SelectStatement? Statement => Query is SelectQueryExpression select ? select.Statement : null;

    /// <summary>
    /// Parse errors encountered during analysis. Empty for valid SQL.
    /// Errors are ordered by position in the source text.
    /// </summary>
    public IReadOnlyList<ParseError> Errors { get; }

    /// <summary>Whether the input was parsed without any errors.</summary>
    public bool IsSuccess => Errors.Count == 0 && Query is not null;

    /// <summary>Creates a successful parse result with no errors.</summary>
    internal ParseResult(QueryExpression query)
    {
        Query = query;
        Errors = [];
    }

    /// <summary>Creates a parse result with a partial or null AST and one or more errors.</summary>
    internal ParseResult(QueryExpression? query, IReadOnlyList<ParseError> errors)
    {
        Query = query;
        Errors = errors;
    }
}

/// <summary>
/// A single parse error with position information. Unlike <see cref="ParseException"/>,
/// this is a data object collected during error-recovering parsing rather than
/// an exception thrown to abort parsing.
/// </summary>
public sealed class ParseError
{
    /// <summary>The human-readable error message.</summary>
    public required string Message { get; init; }

    /// <summary>1-based line number where the error was detected.</summary>
    public required int Line { get; init; }

    /// <summary>1-based column number where the error was detected.</summary>
    public required int Column { get; init; }

    /// <summary>Character length of the problematic span. Defaults to 1.</summary>
    public int Length { get; init; } = 1;
}
