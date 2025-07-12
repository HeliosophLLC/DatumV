namespace DatumIngest.LanguageServer;

using DatumIngest.Manifest;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

/// <summary>
/// Produces diagnostics (parse errors and semantic warnings) for SQL text by
/// running the full parser and optionally validating references against a
/// <see cref="LanguageServerManifest"/>.
/// </summary>
public static class DiagnosticsProvider
{
    /// <summary>
    /// Analyzes the SQL text and returns any parse error diagnostics.
    /// Returns an empty array for valid SQL or empty input.
    /// </summary>
    /// <param name="sql">The SQL text to analyze.</param>
    /// <returns>An array of diagnostics (currently at most one, since the parser stops at the first error).</returns>
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

        SelectStatement statement;
        try
        {
            statement = SqlParser.Parse(sql);
        }
        catch (ParseException exception)
        {
            Superpower.Model.Position position = exception.ErrorPosition;

            // Superpower's Position is 1-based; LSP diagnostics are 0-based.
            int line = System.Math.Max(0, position.Line - 1);
            int column = System.Math.Max(0, position.Column - 1);

            return
            [
                new Diagnostic
                {
                    Message = exception.Message,
                    Severity = DiagnosticSeverity.Error,
                    StartLine = line,
                    StartColumn = column,
                    EndLine = line,
                    // Highlight at least one character at the error position.
                    EndColumn = column + 1,
                }
            ];
        }

        if (manifest is null)
        {
            return [];
        }

        SemanticAnalyzer analyzer = new(manifest);
        return analyzer.Analyze(statement);
    }
}
