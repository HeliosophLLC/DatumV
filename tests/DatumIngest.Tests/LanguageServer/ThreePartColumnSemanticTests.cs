using DatumIngest.LanguageServer;
using DatumIngest.Manifest;

namespace DatumIngest.Tests.LanguageServer;

/// <summary>
/// S6: semantic-analyzer validation for three-part column references
/// (<c>schema.table.column</c>). Covers the resolution path through
/// the qualified-name alias registration and the diagnostic shapes
/// emitted when the schema or column doesn't line up.
/// </summary>
public class ThreePartColumnSemanticTests : ServiceTestBase
{
    private static LanguageServerManifest Manifest(params TableSchemaEntry[] tables) =>
        new()
        {
            Tables = tables,
            Functions = [],
            Keywords = ["SELECT", "FROM", "WHERE"],
        };

    private static TableSchemaEntry Table(string qualifiedName, params string[] columnNames)
    {
        List<TableColumnEntry> cols = new();
        foreach (string c in columnNames)
        {
            cols.Add(new TableColumnEntry { Name = c, Kind = "Int32", Nullable = false });
        }
        return new TableSchemaEntry { Name = qualifiedName, Columns = cols };
    }

    [Fact]
    public void ThreePartColumn_TableInScope_KnownColumn_NoWarning()
    {
        LanguageServerManifest manifest = Manifest(Table("public.users", "id", "email"));

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT public.users.id FROM public.users", manifest);

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void ThreePartColumn_TableInScope_UnknownColumn_Warns()
    {
        LanguageServerManifest manifest = Manifest(Table("public.users", "id"));

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT public.users.nonexistent FROM public.users", manifest);

        Assert.Contains(diagnostics, d =>
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.Contains("Unknown column 'nonexistent'") &&
            d.Message.Contains("public.users"));
    }

    [Fact]
    public void ThreePartColumn_SchemaTableNotInScope_Warns()
    {
        // No FROM clause names myapp.users → 3-part column ref is unresolvable.
        LanguageServerManifest manifest = Manifest(
            Table("public.users", "id"),
            Table("myapp.users", "id"));

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT myapp.users.id FROM public.users", manifest);

        // The qualifier `myapp.users` is not in the FROM clause's alias map.
        Assert.Contains(diagnostics, d =>
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.Contains("myapp.users"));
    }

    [Fact]
    public void ThreePartStarColumn_NoWarning()
    {
        // SELECT public.users.* should be accepted as a wildcard expansion
        // (validated against the table existing in scope; expansion itself
        // is the planner's job).
        LanguageServerManifest manifest = Manifest(Table("public.users", "id", "email"));

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT public.users.* FROM public.users", manifest);

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Warning);
    }
}
