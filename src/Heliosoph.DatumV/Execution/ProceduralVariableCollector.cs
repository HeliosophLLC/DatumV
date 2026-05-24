using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution;

/// <summary>
/// Walks a parsed statement tree and collects every procedural-variable
/// name visible anywhere in the script. Used by
/// <see cref="QueryScopeValidator"/> to skip "unknown column" failures
/// for identifier references that resolve through the runtime variable
/// scope rather than the row schema:
/// <list type="bullet">
///   <item><c>DECLARE name TYPE = expr</c> bindings.</item>
///   <item><c>FOR i = a TO b</c> counter bindings.</item>
///   <item><c>FOR row IN (...)</c> row bindings.</item>
///   <item><c>TRY ... CATCH err</c> error-variable bindings.</item>
///   <item><c>CREATE PROCEDURE p(@x ...)</c> parameter bindings.</item>
///   <item><c>CREATE FUNCTION f(@x ...)</c> parameter bindings.</item>
///   <item>Top-level <c>SELECT name := expr, ...</c> assignment columns.</item>
/// </list>
/// </summary>
/// <remarks>
/// The walker is intentionally over-permissive on scope precision —
/// every procedural-variable name anywhere in the parsed tree gets
/// added, regardless of whether it's actually in scope at any given
/// reference site. The alternative — full per-statement scope
/// tracking — is harder and the conservative whitelist's only failure
/// mode is "validator skips a check it could have caught", which
/// degrades to the prior runtime-error behaviour.
/// </remarks>
internal static class ProceduralVariableCollector
{
    /// <summary>
    /// Returns the set of every procedural variable name reachable
    /// from <paramref name="statement"/>. Names are stored without
    /// the optional <c>@</c> sigil — the parser strips it during
    /// AST construction so consumers compare against bare identifiers.
    /// Returns an empty set when no procedural bindings are present.
    /// </summary>
    public static HashSet<string> Collect(Statement? statement)
    {
        HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);
        if (statement is null) return result;
        Visit(statement, result);
        return result;
    }

    /// <summary>
    /// Walks a batch of top-level statements (a semicolon-separated
    /// script). The same set is shared across the whole batch — a
    /// DECLARE in an earlier statement is visible to a SELECT later
    /// in the same script.
    /// </summary>
    public static HashSet<string> Collect(IEnumerable<Statement> statements)
    {
        HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (Statement statement in statements)
        {
            Visit(statement, result);
        }
        return result;
    }

    private static void Visit(Statement statement, HashSet<string> sink)
    {
        switch (statement)
        {
            case DeclareStatement decl:
                AddIfNotEmpty(sink, decl.VariableName);
                break;
            case ForCounterStatement forCtr:
                AddIfNotEmpty(sink, forCtr.VariableName);
                Visit(forCtr.Body, sink);
                break;
            case ForInStatement forIn:
                AddIfNotEmpty(sink, forIn.VariableName);
                Visit(forIn.Body, sink);
                break;
            case TryStatement tryStmt:
                Visit(tryStmt.TryBody, sink);
                AddIfNotEmpty(sink, tryStmt.ErrorVariableName);
                Visit(tryStmt.CatchBody, sink);
                if (tryStmt.FinallyBody is not null) Visit(tryStmt.FinallyBody, sink);
                break;
            case BlockStatement block:
                foreach (Statement child in block.Statements) Visit(child, sink);
                break;
            case IfStatement ifs:
                Visit(ifs.Then, sink);
                if (ifs.Else is not null) Visit(ifs.Else, sink);
                break;
            case WhileStatement loop:
                Visit(loop.Body, sink);
                break;
            case CreateProcedureStatement createProc:
                foreach (UdfParameter p in createProc.Parameters)
                    AddIfNotEmpty(sink, p.Name);
                Visit(createProc.Body, sink);
                break;
            case CreateFunctionStatement createFn:
                foreach (UdfParameter p in createFn.Parameters)
                    AddIfNotEmpty(sink, p.Name);
                if (createFn.StatementBody is { } stmtBody)
                {
                    foreach (Statement child in stmtBody) Visit(child, sink);
                }
                break;
        }
    }

    private static void AddIfNotEmpty(HashSet<string> sink, string? name)
    {
        if (!string.IsNullOrEmpty(name)) sink.Add(name);
    }
}
