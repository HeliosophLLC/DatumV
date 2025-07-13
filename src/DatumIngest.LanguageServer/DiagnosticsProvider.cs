namespace DatumIngest.LanguageServer;

using DatumIngest.Manifest;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

/// <summary>
/// Produces diagnostics (parse errors and semantic warnings) for SQL text by
/// running the error-recovering parser and optionally validating references
/// against a <see cref="LanguageServerManifest"/>.
/// </summary>
public static class DiagnosticsProvider
{
    /// <summary>
    /// Analyzes the SQL text and returns any parse error diagnostics.
    /// Returns an empty array for valid SQL or empty input.
    /// </summary>
    /// <param name="sql">The SQL text to analyze.</param>
    /// <returns>An array of diagnostics (may contain multiple parse errors).</returns>
    public static Diagnostic[] GetDiagnostics(string sql)
    {
        return GetDiagnostics(sql, manifest: null);
    }

    /// <summary>
    /// Analyzes the SQL text for parse errors and, when a
    /// <paramref name="manifest"/> is provided, for semantic warnings
    /// about unknown tables, columns, and functions.
    /// </summary>
    /// <param name="sql">The SQL text to analyze.</param>
    /// <param name="manifest">
    /// Optional manifest for semantic validation. When <see langword="null"/>,
    /// only syntax errors are reported.
    /// </param>
    /// <returns>An array of diagnostics ordered by position.</returns>
    public static Diagnostic[] GetDiagnostics(string sql, LanguageServerManifest? manifest)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return [];
        }

        ParseResult parseResult = SqlParser.TryParseRecovering(sql);

        List<Diagnostic> diagnostics = new();

        // Convert parse errors to diagnostics.
        foreach (ParseError error in parseResult.Errors)
        {
            // ParseError uses 1-based positions; LSP diagnostics are 0-based.
            int line = System.Math.Max(0, error.Line - 1);
            int column = System.Math.Max(0, error.Column - 1);

            diagnostics.Add(new Diagnostic
            {
                Message = error.Message,
                Severity = DiagnosticSeverity.Error,
                StartLine = line,
                StartColumn = column,
                EndLine = line,
                EndColumn = column + error.Length,
            });
        }

        // Run semantic analysis on the (possibly partial) AST if a manifest
        // is available and the parser produced a tree.
        if (manifest is not null && parseResult.Statement is not null)
        {
            SemanticAnalyzer analyzer = new(manifest);
            Diagnostic[] semanticDiagnostics = analyzer.Analyze(parseResult.Statement);
            diagnostics.AddRange(semanticDiagnostics);
        }

        return diagnostics.ToArray();
    }
}
