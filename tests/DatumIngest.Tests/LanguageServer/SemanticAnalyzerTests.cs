namespace DatumIngest.Tests.LanguageServer;

using DatumIngest.LanguageServer;
using DatumIngest.Manifest;

/// <summary>
/// Tests for <see cref="SemanticAnalyzer"/> — semantic diagnostics for unknown
/// tables, columns, and functions against a <see cref="LanguageServerManifest"/>.
/// </summary>
public sealed class SemanticAnalyzerTests : ServiceTestBase
{
    /// <summary>Builds a minimal manifest with the specified tables and functions.</summary>
    private static LanguageServerManifest CreateManifest(
        IReadOnlyList<TableSchemaEntry>? tables = null,
        IReadOnlyList<FunctionSignature>? functions = null)
    {
        return new LanguageServerManifest
        {
            Tables = tables ?? [],
            Functions = functions ?? [],
            Keywords = ["SELECT", "FROM", "WHERE"],
        };
    }

    private static TableSchemaEntry Table(string name, params string[] columns)
    {
        List<TableColumnEntry> columnEntries = new();
        foreach (string column in columns)
        {
            columnEntries.Add(new TableColumnEntry { Name = column, Kind = "Float32", Nullable = false });
        }

        return new TableSchemaEntry { Name = name, Columns = columnEntries };
    }

    private static FunctionSignature Function(string name, params string[] parameterNames)
    {
        List<ParameterSignature> parameters = new();
        foreach (string parameterName in parameterNames)
        {
            parameters.Add(new ParameterSignature { Name = parameterName, Kind = "Any" });
        }

        return new FunctionSignature { Name = name, Parameters = parameters };
    }

    /// <summary>Builds a <see cref="TableColumnEntry"/> with the specified name and data kind.</summary>
    private static TableColumnEntry TypedColumn(string name, string kind) =>
        new() { Name = name, Kind = kind, Nullable = false };

    /// <summary>Builds a table manifest entry with explicitly typed columns.</summary>
    private static TableSchemaEntry TypedTable(string name, params TableColumnEntry[] columns) =>
        new() { Name = name, Columns = [..columns] };

    /// <summary>Builds a function signature with typed parameters and an explicit return type.</summary>
    private static FunctionSignature TypedFunction(string name, string returnType, params (string Name, string Kind)[] parameters)
    {
        List<ParameterSignature> paramList = new();
        foreach ((string paramName, string paramKind) in parameters)
        {
            paramList.Add(new ParameterSignature { Name = paramName, Kind = paramKind });
        }

        return new FunctionSignature { Name = name, ReturnType = returnType, Parameters = paramList };
    }

    // ───────────────────── Valid SQL ─────────────────────

    [Fact]
    public void Analyze_ValidQuery_ReturnsEmpty()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("users", "id", "name")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT id, name FROM users", manifest);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Analyze_ValidQualifiedColumn_ReturnsEmpty()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("users", "id", "name")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT users.id FROM users", manifest);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Analyze_ValidJoin_ReturnsEmpty()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("users", "id", "name"), Table("orders", "id", "user_id")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT u.name, o.user_id FROM users AS u JOIN orders AS o ON u.id = o.user_id",
            manifest);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Analyze_ValidFunction_ReturnsEmpty()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("t", "x")],
            functions: [Function("upper", "value")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT upper(x) FROM t", manifest);

        Assert.Empty(diagnostics);
    }

    // ───────────────────── Unknown table ─────────────────────

    [Fact]
    public void Analyze_UnknownTable_ReturnsWarning()
    {
        LanguageServerManifest manifest = CreateManifest();

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT x FROM unknown_table", manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("unknown_table"));
    }

    [Fact]
    public void Analyze_UnknownTable_HasAccurateSpan()
    {
        LanguageServerManifest manifest = CreateManifest();

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT x FROM bad_table", manifest);

        Diagnostic tableWarning = Assert.Single(diagnostics,
            diagnostic => diagnostic.Message.Contains("bad_table"));

        // "bad_table" starts at column 15 (0-based: 14), length 9.
        Assert.Equal(0, tableWarning.StartLine);
        Assert.Equal(14, tableWarning.StartColumn);
        Assert.Equal(14 + 9, tableWarning.EndColumn);
    }

    [Fact]
    public void Analyze_UnknownJoinTable_ReturnsWarning()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("users", "id")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT u.id FROM users AS u JOIN ghost AS g ON u.id = g.id",
            manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("ghost"));
    }

    // ───────────────────── Unknown column ─────────────────────

    [Fact]
    public void Analyze_UnknownColumn_ReturnsWarning()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("users", "id", "name")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT missing_col FROM users", manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("missing_col"));
    }

    [Fact]
    public void Analyze_UnknownQualifiedColumn_ReturnsWarning()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("users", "id", "name")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT users.bogus FROM users", manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("bogus") &&
            diagnostic.Message.Contains("users"));
    }

    [Fact]
    public void Analyze_UnknownColumnInWhere_ReturnsWarning()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("users", "id", "name")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT id FROM users WHERE phantom = 1", manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("phantom"));
    }

    [Fact]
    public void Analyze_UnknownColumnInOrderBy_ReturnsWarning()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("users", "id", "name")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT id FROM users ORDER BY nope", manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("nope"));
    }

    // ───────────────────── Unknown function ─────────────────────

    [Fact]
    public void Analyze_UnknownFunction_ReturnsWarning()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("t", "x")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT bogus_fn(x) FROM t", manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("bogus_fn"));
    }

    [Fact]
    public void Analyze_UnknownFunctionSource_ReturnsWarning()
    {
        LanguageServerManifest manifest = CreateManifest();

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT x FROM missing_tvf('path')", manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("missing_tvf"));
    }

    // ───────────────────── Case insensitivity ─────────────────────

    [Fact]
    public void Analyze_CaseInsensitiveTable_ReturnsEmpty()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("Users", "id")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT id FROM users", manifest);

        // Should not warn about "users" even though manifest has "Users".
        Assert.DoesNotContain(diagnostics, diagnostic =>
            diagnostic.Message.Contains("users", StringComparison.OrdinalIgnoreCase) &&
            diagnostic.Message.Contains("Unknown table"));
    }

    [Fact]
    public void Analyze_CaseInsensitiveColumn_ReturnsEmpty()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("t", "Name")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT name FROM t", manifest);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Analyze_CaseInsensitiveFunction_ReturnsEmpty()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("t", "x")],
            functions: [Function("UPPER", "value")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT upper(x) FROM t", manifest);

        Assert.Empty(diagnostics);
    }

    // ───────────────────── Aliases ─────────────────────

    [Fact]
    public void Analyze_AliasedTable_AcceptsAliasQualifiedColumns()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("users", "id", "name")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT u.id FROM users AS u", manifest);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Analyze_UnknownAliasQualifier_ReturnsWarning()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("users", "id")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT z.id FROM users AS u", manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("z"));
    }

    // ───────────────────── Opaque sources ─────────────────────

    [Fact]
    public void Analyze_SubquerySource_SkipsColumnValidation()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("users", "id", "name")]);

        // Subquery columns are opaque — we should not warn about "sub.x".
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT sub.x FROM (SELECT id FROM users) AS sub", manifest);

        Assert.DoesNotContain(diagnostics, diagnostic =>
            diagnostic.Message.Contains("Unknown column"));
    }

    [Fact]
    public void Analyze_FunctionSource_SkipsColumnValidation()
    {
        LanguageServerManifest manifest = CreateManifest(
            functions: [Function("read_csv", "path")]);

        // Function source columns are opaque.
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT f.col FROM read_csv('data.csv') AS f", manifest);

        Assert.DoesNotContain(diagnostics, diagnostic =>
            diagnostic.Message.Contains("Unknown column"));
    }

    /// <summary>
    /// An unaliased function source (e.g. <c>FROM RANGE(0, 100)</c>) must also
    /// suppress unknown-column warnings for its output columns.
    /// </summary>
    [Fact]
    public void Analyze_UnaliasedFunctionSource_SkipsColumnValidation()
    {
        LanguageServerManifest manifest = CreateManifest(
            functions: [Function("range", "start", "stop")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT Value FROM range(0, 100)", manifest);

        Assert.DoesNotContain(diagnostics, diagnostic =>
            diagnostic.Message.Contains("Unknown column"));
    }

    // ───────────────────── SELECT * / table.* ─────────────────────

    [Fact]
    public void Analyze_SelectAll_ReturnsEmpty()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("t", "x")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT * FROM t", manifest);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Analyze_SelectTableStar_KnownTable_ReturnsEmpty()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("t", "x")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT t.* FROM t", manifest);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Analyze_SelectTableStar_UnknownTable_ReturnsWarning()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("t", "x")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT z.* FROM t", manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("z"));
    }

    // ───────────────────── No manifest ─────────────────────

    [Fact]
    public void Analyze_NoManifest_ValidSql_ReturnsEmpty()
    {
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT x FROM unknown_table");

        // Without a manifest, no semantic validation occurs.
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Analyze_NoManifest_InvalidSql_ReturnsParseError()
    {
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics("SELEKT x");

        Assert.NotEmpty(diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, diagnostics[0].Severity);
    }

    // ───────────────────── Multiple diagnostics ─────────────────────

    [Fact]
    public void Analyze_MultipleIssues_ReturnsMultipleDiagnostics()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("t", "x")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT bogus_fn(missing_col) FROM t", manifest);

        Assert.True(diagnostics.Length >= 2,
            $"Expected at least 2 diagnostics but got {diagnostics.Length}.");
    }

    // ───────────────────── Expression nesting ─────────────────────

    [Fact]
    public void Analyze_UnknownColumnInBinaryExpression_ReturnsWarning()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("t", "x")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT x + missing FROM t", manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("missing"));
    }

    [Fact]
    public void Analyze_UnknownColumnInCastExpression_ReturnsWarning()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("t", "x")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT CAST(ghost AS int) FROM t", manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("ghost"));
    }

    [Fact]
    public void Analyze_SubqueryExpression_RecursesIntoSubquery()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("t", "x"), Table("inner_t", "y")]);

        // Use a scalar subquery (parenthesized SELECT) rather than IN (SELECT ...)
        // because the parser's IN postfix expects an expression list, not a subquery.
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT x FROM t WHERE x = (SELECT phantom FROM inner_t)", manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("phantom"));
    }

    // ───────────────────── JOIN ON condition ─────────────────────

    [Fact]
    public void Analyze_UnknownColumnInJoinCondition_ReturnsWarning()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("a", "id"), Table("b", "id")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT a.id FROM a JOIN b ON a.id = b.ghost", manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("ghost"));
    }

    // ───────────────────── CASE expression ─────────────────────

    [Fact]
    public void Analyze_ValidCaseExpression_ReturnsEmpty()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("t", "x", "label")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT CASE WHEN x > 0 THEN label ELSE 'none' END FROM t", manifest);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Analyze_UnknownColumnInCaseCondition_ReturnsWarning()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("t", "x")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT CASE WHEN ghost > 0 THEN 'yes' ELSE 'no' END FROM t", manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("ghost"));
    }

    [Fact]
    public void Analyze_UnknownColumnInCaseResult_ReturnsWarning()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("t", "x")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT CASE WHEN x > 0 THEN phantom ELSE 'no' END FROM t", manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("phantom"));
    }

    // ───────────────────── Type mismatch diagnostics ─────────────────────

    [Fact]
    public void Analyze_StringColumnInNumericFunction_ReturnsWarning()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [TypedTable("t", TypedColumn("label", "String"))],
            functions: [TypedFunction("sin", "Float32", ("x", "Float32"))]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT sin(label) FROM t", manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("sin") &&
            diagnostic.Message.Contains("String"));
    }

    [Fact]
    public void Analyze_NumericColumnInStringFunction_ReturnsWarning()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [TypedTable("t", TypedColumn("price", "Float32"))],
            functions: [TypedFunction("len", "Int32", ("value", "String"))]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT len(price) FROM t", manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("len") &&
            diagnostic.Message.Contains("Float32"));
    }

    [Fact]
    public void Analyze_CompatibleNumericKinds_ReturnsEmpty()
    {
        // Int32 is in the numeric category — should be accepted where Float32 is expected.
        LanguageServerManifest manifest = CreateManifest(
            tables: [TypedTable("t", TypedColumn("count", "Int32"))],
            functions: [TypedFunction("sin", "Float32", ("x", "Float32"))]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT sin(count) FROM t", manifest);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Analyze_StringLiteralInStringFunction_ReturnsEmpty()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("t", "x")],
            functions: [TypedFunction("len", "Int32", ("value", "String"))]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT len('hello') FROM t", manifest);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Analyze_NumericLiteralInNumericFunction_ReturnsEmpty()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("t", "x")],
            functions: [TypedFunction("sin", "Float32", ("x", "Float32"))]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT sin(3.14) FROM t", manifest);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Analyze_StringLiteralInNumericFunction_ReturnsWarning()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("t", "x")],
            functions: [TypedFunction("sin", "Float32", ("x", "Float32"))]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT sin('hello') FROM t", manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("sin"));
    }

    [Fact]
    public void Analyze_AnyParameterKind_NeverWarns()
    {
        // "Any" parameters must never produce type-mismatch warnings.
        LanguageServerManifest manifest = CreateManifest(
            tables: [TypedTable("t", TypedColumn("label", "String"))],
            functions: [Function("coalesce", "a", "b")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT coalesce(label, 'default') FROM t", manifest);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Analyze_CastToCompatibleType_ReturnsEmpty()
    {
        // CAST infers the target type; Float32 matches sin's Float32 parameter.
        LanguageServerManifest manifest = CreateManifest(
            tables: [TypedTable("t", TypedColumn("label", "String"))],
            functions: [TypedFunction("sin", "Float32", ("x", "Float32"))]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT sin(CAST(label AS Float32)) FROM t", manifest);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Analyze_CastToIncompatibleType_ReturnsWarning()
    {
        // CAST to String still produces a warning when sin expects a numeric type.
        LanguageServerManifest manifest = CreateManifest(
            tables: [TypedTable("t", TypedColumn("label", "String"))],
            functions: [TypedFunction("sin", "Float32", ("x", "Float32"))]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT sin(CAST(label AS String)) FROM t", manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("sin"));
    }

    // ─────────────────── Virtual schema references ───────────────────
    //
    // S5 retired the hardcoded virtual-schema list — these tests now
    // pass-by-providing rather than pass-by-accident: the manifest
    // explicitly carries the standard virtual tables and the assertions
    // demand zero warnings (not just zero "Unknown table" substrings,
    // which the post-S5 messages bypassed anyway).

    private static LanguageServerManifest VirtualSchemaManifest() => CreateManifest(tables: new[]
    {
        Table("information_schema.tables", "table_catalog", "table_schema", "table_name", "table_type"),
        Table("information_schema.columns", "table_schema", "table_name", "column_name", "ordinal_position", "data_type"),
        Table("information_schema.schemata", "schema_name"),
        Table("datum_catalog.functions", "function_name", "category"),
        Table("datum_catalog.function_parameters", "function_name", "parameter_name", "ordinal_position"),
        Table("datum_catalog.indexes", "table_name", "column_name", "index_type", "entry_count"),
        Table("datum_catalog.interactions", "left_column", "right_column", "pearson"),
    });

    [Fact]
    public void Analyze_InformationSchemaTables_NoWarning()
    {
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT * FROM information_schema.tables", VirtualSchemaManifest());

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Analyze_InformationSchemaColumns_NoWarning()
    {
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT column_name FROM information_schema.columns", VirtualSchemaManifest());

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Analyze_DatumCatalogFunctions_NoWarning()
    {
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT function_name FROM datum_catalog.functions", VirtualSchemaManifest());

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Analyze_DatumCatalogFunctionParameters_NoWarning()
    {
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT parameter_name FROM datum_catalog.function_parameters", VirtualSchemaManifest());

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Analyze_DatumCatalogIndexes_NoWarning()
    {
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT index_type FROM datum_catalog.indexes", VirtualSchemaManifest());

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Analyze_DatumCatalogInteractions_NoWarning()
    {
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT pearson FROM datum_catalog.interactions", VirtualSchemaManifest());

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Analyze_UnknownVirtualSchemaTable_ReturnsWarning()
    {
        // When the manifest carries known tables in information_schema,
        // requesting a missing one produces a precise "table not in schema"
        // diagnostic distinct from "schema doesn't exist".
        LanguageServerManifest manifest = CreateManifest(tables: new[]
        {
            Table("information_schema.tables", "table_schema", "table_name"),
        });

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT * FROM information_schema.nonexistent", manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("Table 'nonexistent' does not exist in schema 'information_schema'"));
    }

    [Fact]
    public void Analyze_UnknownSchema_ReturnsWarning()
    {
        // When the schema itself isn't represented in the manifest,
        // the diagnostic names the missing schema specifically.
        LanguageServerManifest manifest = CreateManifest();

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT * FROM fake_schema.tables", manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("Schema 'fake_schema' does not exist"));
    }

    [Fact]
    public void Analyze_VirtualSchemaWithAlias_NoWarning()
    {
        LanguageServerManifest manifest = CreateManifest();

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT t.table_name FROM information_schema.tables t", manifest);

        Assert.DoesNotContain(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("Unknown table"));
    }

    // ───────────────────── LET binding diagnostics ─────────────────────

    /// <summary>
    /// A scalar LET binding name referenced in an ASSERT predicate must not produce
    /// an "Unknown column" diagnostic — LET names are runtime virtual columns, not
    /// manifest columns, so they are added to the opaque scope for ASSERT analysis.
    /// </summary>
    [Fact]
    public void Analyze_ScalarLetName_InAssertPredicate_NoUnknownColumnWarning()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("t", "price", "qty")]);

        // 'total' is referenced only inside the DEFINE block (LET + ASSERT), not in the
        // SELECT column list, so the analyzer never sees it as an unresolved column reference
        // outside the opaque-alias scope.
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT DEFINE { LET total = price * qty; ASSERT total > 0; } price FROM t", manifest);

        Assert.DoesNotContain(diagnostics, d =>
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.Contains("total") &&
            d.Message.Contains("Unknown column"));
    }

    /// <summary>
    /// Names produced by positional destructuring (<c>LET (a, b) = expr</c>) must not
    /// generate "Unknown column" warnings when referenced in an ASSERT predicate inside
    /// a DEFINE block. Destructured names are added to the opaque scope by the analyzer.
    /// </summary>
    [Fact]
    public void Analyze_PositionalDestructuringNames_InAssertPredicate_NoUnknownColumnWarning()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("t", "arr")]);

        // 'x' and 'y' are destructured names — not in the table schema.
        // The analyzer must suppress "Unknown column" for them in ASSERT predicates.
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT DEFINE { LET (x, y) = arr; ASSERT x > 0; } arr FROM t", manifest);

        Assert.DoesNotContain(diagnostics, d =>
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.Contains("Unknown column") &&
            (d.Message.Contains("'x'") || d.Message.Contains("'y'")));
    }

    /// <summary>
    /// Names produced by named destructuring (<c>LET {alpha, beta} = expr</c>) must not
    /// generate "Unknown column" warnings when referenced in an ASSERT predicate.
    /// </summary>
    [Fact]
    public void Analyze_NamedDestructuringNames_InAssertPredicate_NoUnknownColumnWarning()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("t", "pair")]);

        // 'lo' and 'hi' are named-destructuring bindings — not in the table schema.
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT DEFINE { LET {lo, hi} = pair; ASSERT hi > lo; } pair FROM t", manifest);

        Assert.DoesNotContain(diagnostics, d =>
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.Contains("Unknown column") &&
            (d.Message.Contains("'lo'") || d.Message.Contains("'hi'")));
    }

    // ───────────────────── LET names in SELECT / WHERE / ORDER BY ─────────────────────

    /// <summary>
    /// A scalar LET name referenced directly in a SELECT output column must not produce
    /// an "Unknown column" warning — the name is a virtual row field, not a schema column.
    /// </summary>
    [Fact]
    public void Analyze_ScalarLetName_InSelectColumn_NoUnknownColumnWarning()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("t", "price", "qty")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT LET total = price * qty, total FROM t", manifest);

        Assert.DoesNotContain(diagnostics, d =>
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.Contains("'total'") &&
            d.Message.Contains("Unknown column"));
    }

    /// <summary>
    /// Positional destructuring names referenced in SELECT output columns must not warn.
    /// </summary>
    [Fact]
    public void Analyze_PositionalDestructuringNames_InSelectColumn_NoUnknownColumnWarning()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("t", "arr")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT LET (x, y) = arr, x, y FROM t", manifest);

        Assert.DoesNotContain(diagnostics, d =>
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.Contains("Unknown column") &&
            (d.Message.Contains("'x'") || d.Message.Contains("'y'")));
    }

    /// <summary>
    /// Named destructuring names referenced in SELECT output columns must not warn.
    /// </summary>
    [Fact]
    public void Analyze_NamedDestructuringNames_InSelectColumn_NoUnknownColumnWarning()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("t", "pair")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT LET {lo, hi} = pair, lo, hi FROM t", manifest);

        Assert.DoesNotContain(diagnostics, d =>
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.Contains("Unknown column") &&
            (d.Message.Contains("'lo'") || d.Message.Contains("'hi'")));
    }

    /// <summary>
    /// A LET name used in a WHERE clause must not produce an "Unknown column" warning.
    /// </summary>
    [Fact]
    public void Analyze_LetName_InWhereClause_NoUnknownColumnWarning()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("t", "price", "qty")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT LET total = price * qty, price FROM t WHERE total > 100", manifest);

        Assert.DoesNotContain(diagnostics, d =>
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.Contains("'total'") &&
            d.Message.Contains("Unknown column"));
    }
}
