namespace Heliosoph.DatumV.Parsing;

using Heliosoph.DatumV.Parsing.Ast;

/// <summary>
/// The result of parsing SQL text, containing either a valid AST, a partial
/// AST with errors, or just errors when recovery was not possible.
/// </summary>
public sealed class ParseResult
{
    /// <summary>
    /// The parsed query expression. May be <see langword="null"/> if parsing failed entirely
    /// and no partial tree could be recovered, or if the input is a DDL/DML batch.
    /// When errors are present, the tree may contain <see cref="ErrorExpression"/>
    /// placeholder nodes where recovery skipped unparseable input.
    /// </summary>
    public QueryExpression? Query { get; }

    /// <summary>
    /// The parsed statements when the input is a DDL/DML batch or a
    /// semicolon-separated sequence of statements. <see langword="null"/> when
    /// the recovery parser was used (which only produces <see cref="Query"/>).
    /// </summary>
    public IReadOnlyList<Statement>? Statements { get; }

    /// <summary>
    /// The parsed AST as a <see cref="SelectStatement"/>, for backward compatibility.
    /// Returns the statement from a <see cref="SelectQueryExpression"/>, or <see langword="null"/>
    /// if the query is a compound expression or parsing failed.
    /// </summary>
    public SelectStatement? Statement => Query is SelectQueryExpression select ? select.Statement : null;

    /// <summary>
    /// The first <see cref="QueryExpression"/> reachable from this parse — either
    /// <see cref="Query"/> when the input was a bare query, or the first
    /// <see cref="QueryStatement"/>'s <see cref="QueryStatement.Query"/> when
    /// the batch mixes procedural statements (e.g. <c>DECLARE</c>) with a
    /// query. Tooling that needs the query for analysis regardless of
    /// surrounding procedural scaffolding (LSP hover, CTE-schema resolution,
    /// TVF-alias enumeration) should prefer this over <see cref="Query"/>,
    /// which is intentionally <see langword="null"/> for multi-statement
    /// batches.
    /// </summary>
    public QueryExpression? EffectiveQuery
    {
        get
        {
            if (Query is not null) return Query;
            if (Statements is null) return null;
            foreach (Statement statement in Statements)
            {
                if (statement is QueryStatement qs) return qs.Query;
            }
            return null;
        }
    }

    /// <summary>
    /// Parse errors encountered during analysis. Empty for valid SQL.
    /// Errors are ordered by position in the source text.
    /// </summary>
    public IReadOnlyList<ParseError> Errors { get; }

    /// <summary>Whether the input was parsed without any errors.</summary>
    public bool IsSuccess => Errors.Count == 0 && (Query is not null || Statements is not null);

    /// <summary>Creates a successful parse result for a single query expression.</summary>
    internal ParseResult(QueryExpression query)
    {
        Query = query;
        Errors = [];
    }

    /// <summary>Creates a successful parse result for a batch of statements (DDL/DML/queries).</summary>
    internal ParseResult(IReadOnlyList<Statement> statements)
    {
        // When the batch is a single query statement, extract the QueryExpression
        // so that semantic analysis (table/column validation) still works.
        Query = statements is [QueryStatement single] ? single.Query : null;
        Statements = statements;
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
