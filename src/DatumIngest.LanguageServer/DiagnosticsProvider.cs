namespace DatumIngest.LanguageServer;

using DatumIngest.Parsing;

/// <summary>
/// Produces diagnostics (parse errors) for SQL text by running the full parser
/// and converting any <see cref="ParseException"/> into LSP-aligned diagnostics.
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
        if (string.IsNullOrWhiteSpace(sql))
        {
            return [];
        }

        try
        {
            SqlParser.Parse(sql);
            return [];
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
    }
}
