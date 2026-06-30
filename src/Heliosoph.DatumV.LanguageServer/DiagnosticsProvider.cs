namespace Heliosoph.DatumV.LanguageServer;

using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Parsing;
using Heliosoph.DatumV.Parsing.Ast;

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
        // is available and the parser produced a tree. EffectiveQuery (vs
        // Query) so a DECLARE preceding the SELECT still gets unknown-
        // column/table/function checks — Query is intentionally null for
        // multi-statement batches.
        QueryExpression? analyzed = parseResult.EffectiveQuery;
        if (manifest is not null && analyzed is not null)
        {
            // Procedural variables declared earlier in the batch (DECLARE @x,
            // FOR @i, CATCH @err) resolve as values in a trailing SELECT, so
            // pass their names to the analyzer to suppress unknown-column
            // warnings on bare references to them.
            HashSet<string> declaredVariables = new(StringComparer.OrdinalIgnoreCase);
            if (parseResult.Statements is not null)
            {
                foreach (Statement statement in parseResult.Statements)
                {
                    CollectDeclaredVariableNames(statement, declaredVariables);
                }
            }

            SemanticAnalyzer analyzer = new(manifest);
            Diagnostic[] semanticDiagnostics = analyzer.Analyze(analyzed, declaredVariables);
            diagnostics.AddRange(semanticDiagnostics);
        }

        // DML against a view: views are pure macros and not updatable, but
        // the SourcePlanner / executor wouldn't tell the user that until
        // runtime. Flag here so the editor draws a squiggle on the
        // statement instead. Walks every parsed statement (batches surface
        // their list via `Statements`; a single statement is wrapped on
        // the fly).
        if (manifest is not null)
        {
            CheckDmlAgainstViews(sql, parseResult, manifest, diagnostics);
        }

        return diagnostics.ToArray();
    }

    /// <summary>
    /// Walks a statement (recursing into block / branch / loop / handler
    /// bodies) and collects every procedural variable name it introduces —
    /// <c>DECLARE</c>, the counter / cursor of a <c>FOR</c>, and a
    /// <c>CATCH</c> error variable. Over-collecting nested-scope names is
    /// harmless here: the set only suppresses unknown-column warnings, and a
    /// real typo is unlikely to collide with a declared variable name.
    /// </summary>
    private static void CollectDeclaredVariableNames(Statement statement, HashSet<string> sink)
    {
        switch (statement)
        {
            case DeclareStatement decl:
                sink.Add(decl.VariableName);
                break;
            case ForCounterStatement forCtr:
                sink.Add(forCtr.VariableName);
                CollectDeclaredVariableNames(forCtr.Body, sink);
                break;
            case ForInStatement forIn:
                sink.Add(forIn.VariableName);
                CollectDeclaredVariableNames(forIn.Body, sink);
                break;
            case BlockStatement block:
                foreach (Statement child in block.Statements) CollectDeclaredVariableNames(child, sink);
                break;
            case IfStatement ifStmt:
                CollectDeclaredVariableNames(ifStmt.Then, sink);
                if (ifStmt.Else is not null) CollectDeclaredVariableNames(ifStmt.Else, sink);
                break;
            case WhileStatement whileStmt:
                CollectDeclaredVariableNames(whileStmt.Body, sink);
                break;
            case TryStatement tryStmt:
                CollectDeclaredVariableNames(tryStmt.TryBody, sink);
                sink.Add(tryStmt.ErrorVariableName);
                CollectDeclaredVariableNames(tryStmt.CatchBody, sink);
                if (tryStmt.FinallyBody is not null) CollectDeclaredVariableNames(tryStmt.FinallyBody, sink);
                break;
        }
    }

    /// <summary>
    /// Flags <c>INSERT INTO view</c> / <c>UPDATE view SET …</c> /
    /// <c>DELETE FROM view</c> against names that resolve to a view entry
    /// in the manifest. Positions the diagnostic at the leading
    /// <c>INSERT</c> / <c>UPDATE</c> / <c>DELETE</c> keyword via a literal
    /// substring scan — DML statements don't currently carry a
    /// <c>SourceSpan</c>, so a precise AST-driven position isn't available
    /// here. Imprecise-but-present is preferable to silent.
    /// </summary>
    private static void CheckDmlAgainstViews(
        string sql, ParseResult parseResult, LanguageServerManifest manifest, List<Diagnostic> diagnostics)
    {
        IEnumerable<Statement> statements = parseResult.Statements
            ?? (parseResult.EffectiveQuery is not null
                ? new[] { (Statement)new QueryStatement(parseResult.EffectiveQuery) }
                : Enumerable.Empty<Statement>());

        foreach (Statement statement in statements)
        {
            switch (statement)
            {
                case InsertStatement insert when ResolveView(manifest, insert.SchemaName, insert.TableName) is { } iv:
                    diagnostics.Add(BuildDmlOnViewDiagnostic(sql, "INSERT", iv));
                    break;
                case UpdateStatement update when ResolveView(manifest, update.SchemaName, update.TableName) is { } uv:
                    diagnostics.Add(BuildDmlOnViewDiagnostic(sql, "UPDATE", uv));
                    break;
                case DeleteStatement delete when ResolveView(manifest, delete.SchemaName, delete.TableName) is { } dv:
                    diagnostics.Add(BuildDmlOnViewDiagnostic(sql, "DELETE", dv));
                    break;
            }
        }
    }

    /// <summary>
    /// Resolves <paramref name="name"/> (optionally schema-qualified)
    /// against the manifest's view entries. Walks <see cref="LanguageServerManifest.SearchPath"/>
    /// for unqualified lookups so the precedence matches the engine's.
    /// Returns the matched view's qualified name; <see langword="null"/>
    /// when no view entry exists at that name.
    /// </summary>
    private static string? ResolveView(LanguageServerManifest manifest, string? explicitSchema, string name)
    {
        if (explicitSchema is not null)
        {
            string qualified = $"{explicitSchema}.{name}";
            return manifest.Tables.Any(t =>
                string.Equals(t.Kind, "VIEW", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.Name, qualified, StringComparison.OrdinalIgnoreCase))
                ? qualified
                : null;
        }

        foreach (string schema in manifest.SearchPath)
        {
            string qualified = $"{schema}.{name}";
            if (manifest.Tables.Any(t =>
                string.Equals(t.Kind, "VIEW", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.Name, qualified, StringComparison.OrdinalIgnoreCase)))
            {
                return qualified;
            }
        }

        return null;
    }

    /// <summary>
    /// Builds a diagnostic for DML against a view. Positions on the leading
    /// <c>verb</c> keyword via a case-insensitive substring scan — DML
    /// statements have no <see cref="SourceSpan"/> today, so this is the
    /// best signal available without an AST-driven position.
    /// </summary>
    private static Diagnostic BuildDmlOnViewDiagnostic(string sql, string verb, string qualifiedViewName)
    {
        int byteOffset = sql.IndexOf(verb, StringComparison.OrdinalIgnoreCase);
        (int line, int column) = byteOffset >= 0
            ? LineColumnOf(sql, byteOffset)
            : (0, 0);

        return new Diagnostic
        {
            Message = $"{verb} target '{qualifiedViewName}' is a view; "
                    + $"{verb} through a view is not supported. Target the underlying table directly.",
            Severity = DiagnosticSeverity.Error,
            StartLine = line,
            StartColumn = column,
            EndLine = line,
            EndColumn = column + verb.Length,
        };
    }

    /// <summary>
    /// Translates a 0-based character offset into 0-based (line, column).
    /// Counts <c>\n</c> as the line terminator; <c>\r\n</c> sequences
    /// resolve the same way because <c>\r</c> contributes a column but
    /// the <c>\n</c> resets it.
    /// </summary>
    private static (int Line, int Column) LineColumnOf(string text, int offset)
    {
        int line = 0;
        int column = 0;
        for (int i = 0; i < offset && i < text.Length; i++)
        {
            if (text[i] == '\n') { line++; column = 0; }
            else { column++; }
        }
        return (line, column);
    }
}
