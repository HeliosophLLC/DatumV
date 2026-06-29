namespace Heliosoph.DatumV.Tests.LanguageServer;

using Heliosoph.DatumV.LanguageServer;
using Heliosoph.DatumV.Manifest;

/// <summary>
/// Tests for <see cref="SemanticAnalyzer"/> — semantic diagnostics for unknown
/// tables, columns, and functions against a <see cref="LanguageServerManifest"/>.
/// </summary>
public sealed class SemanticAnalyzerTests : ServiceTestBase
{
    /// <summary>Builds a minimal manifest with the specified tables and functions.</summary>
    private static LanguageServerManifest CreateManifest(
        IReadOnlyList<TableSchemaEntry>? tables = null,
        IReadOnlyList<FunctionSignature>? functions = null,
        IReadOnlyList<ModelEntry>? models = null)
    {
        return new LanguageServerManifest
        {
            Tables = tables ?? [],
            Functions = functions ?? [],
            Models = models,
            Keywords = ["SELECT", "FROM", "WHERE"],
        };
    }

    /// <summary>
    /// Builds a scalar-function signature whose output is a struct with the
    /// given field names (each typed <c>Float32</c> — the kind is irrelevant
    /// to the field-name validation under test).
    /// </summary>
    private static FunctionSignature StructFunction(string name, params string[] fieldNames)
    {
        List<StructFieldSignature> fields = new();
        foreach (string fieldName in fieldNames)
        {
            fields.Add(new StructFieldSignature { Name = fieldName, Kind = "Float32" });
        }

        return new FunctionSignature
        {
            Name = name,
            Parameters = [new ParameterSignature { Name = "x", Kind = "Any" }],
            OutputStructFields = fields,
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
    public void Analyze_UnknownColumn_AfterLeadingDeclare_StillReturnsWarning()
    {
        // Regression: DiagnosticsProvider used to guard semantic analysis on
        // parseResult.Query, which is null for multi-statement batches.
        // A DECLARE preceding the SELECT silenced every unknown-column /
        // unknown-table warning. EffectiveQuery restores the diagnostic.
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("users", "id", "name")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "DECLARE threshold Int32 = 10;\nSELECT missing_col FROM users",
            manifest);

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

    [Fact]
    public void Analyze_StructFieldAccess_OnFunctionSourceAlias_DoesNotWarn()
    {
        // `c.value.label` parses as a 3-part `schema.table.column` reference
        // (SchemaName=c, TableName=value, ColumnName=label). When the first
        // segment matches a known alias — here `c`, an opaque TVF output —
        // it's a struct-field access, not a fully-qualified table reference,
        // so the analyzer must not emit "Unknown table or alias 'c.value'".
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("items", "payload")],
            functions: [Function("unnest", "array")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT c.value.label FROM items a CROSS JOIN unnest(a.payload) c",
            manifest);

        Assert.DoesNotContain(diagnostics, diagnostic =>
            diagnostic.Message.Contains("Unknown table or alias 'c.value'"));
    }

    [Fact]
    public void Analyze_StructFieldAccess_OnTableAlias_DoesNotWarn()
    {
        // Same disambiguation for a real-table alias: `t.col.field` must not
        // warn even when `t` is a non-opaque table — there's no struct-field
        // metadata to validate against statically, so we under-warn rather
        // than false-positive.
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("things", "props")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT t.props.label FROM things t", manifest);

        Assert.DoesNotContain(diagnostics, diagnostic =>
            diagnostic.Message.Contains("Unknown table or alias 't.props'"));
    }

    [Fact]
    public void Analyze_StructFieldAccess_OnSubqueryColumn_DoesNotWarn()
    {
        // `p.label` where `p` is a struct-valued column (`models.foo(x) AS p`)
        // projected by a subquery `FROM (...) t`. The 2-part reference parses as
        // alias=p, column=label, but `p` is a row column the opaque subquery
        // emits — the runtime resolves it as struct-field access. The subquery's
        // output columns aren't introspected, so the analyzer must defer rather
        // than emit "Unknown table or alias 'p'".
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("images", "file", "file_name")],
            functions: [Function("classify", "img")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT file_name, p.label, p.score FROM ("
            + "SELECT file_name, file, classify(file) AS p FROM images) t",
            manifest);

        Assert.DoesNotContain(diagnostics, diagnostic =>
            diagnostic.Message.Contains("Unknown table or alias 'p'"));
    }

    [Fact]
    public void Analyze_StructFieldAccess_OnSubqueryStructColumn_KnownField_DoesNotWarn()
    {
        // The subquery projects a struct-valued column `p` from a scalar
        // function whose manifest entry declares `OutputStructFields`
        // (label, score). Accessing a real field — `p.label` / `p.score` —
        // resolves cleanly now that the analyzer threads the subquery's
        // struct shape into the outer scope.
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("images", "file", "file_name")],
            functions: [StructFunction("classify", "label", "score")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT file_name, p.label, p.score FROM ("
            + "SELECT file_name, classify(file) AS p FROM images) t",
            manifest);

        Assert.DoesNotContain(diagnostics, diagnostic =>
            diagnostic.Message.Contains("Unknown field")
            || diagnostic.Message.Contains("Unknown table or alias 'p'"));
    }

    [Fact]
    public void Analyze_StructFieldAccess_OnSubqueryStructColumn_UnknownField_Warns()
    {
        // The payoff of threading the struct shape: a typo'd field name
        // (`p.scxre` instead of `p.score`) is now caught at edit time with a
        // precise "unknown field" diagnostic, where before it either
        // false-positived as an unknown alias or slipped through the
        // opaque-source suppression.
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("images", "file", "file_name")],
            functions: [StructFunction("classify", "label", "score")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT p.scxre FROM (SELECT classify(file) AS p FROM images) t",
            manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Message.Contains("Unknown field 'scxre' on struct column 'p'"));
    }

    [Fact]
    public void Analyze_StructFieldAccess_OnSubqueryModelStructColumn_UnknownField_Warns()
    {
        // Same, but the struct column comes from a `models.X(...)` call whose
        // output fields are declared on the model entry — the engine's real
        // shape for `models.mobilenetv2(file) AS p`.
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("images", "file", "file_name")],
            models:
            [
                new ModelEntry
                {
                    Name = "mobilenetv2",
                    OutputStructFields =
                    [
                        new StructFieldSignature { Name = "label", Kind = "String" },
                        new StructFieldSignature { Name = "score", Kind = "Float32" },
                    ],
                },
            ]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT p.labxl FROM (SELECT models.mobilenetv2(file) AS p FROM images) t",
            manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Message.Contains("Unknown field 'labxl' on struct column 'p'"));
    }

    [Fact]
    public void Analyze_StructFieldAccess_OnSubqueryStructLiteralColumn_UnknownField_Warns()
    {
        // The struct shape can also come from a struct literal, whose field
        // names live directly in the AST — no manifest entry required.
        // `s.label`/`s.score` are valid; `s.bogus` is not.
        LanguageServerManifest manifest = CreateManifest();

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT s.bogus FROM (SELECT { label: 'test', score: 0.9 } s) t",
            manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Message.Contains("Unknown field 'bogus' on struct column 's'"));
    }

    [Fact]
    public void Analyze_StructFieldAccess_OnSubqueryStructLiteralColumn_KnownField_DoesNotWarn()
    {
        // Companion: real fields of the struct literal resolve cleanly.
        LanguageServerManifest manifest = CreateManifest();

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT s.label, s.score FROM (SELECT { label: 'test', score: 0.9 } s) t",
            manifest);

        Assert.DoesNotContain(diagnostics, diagnostic =>
            diagnostic.Message.Contains("Unknown field")
            || diagnostic.Message.Contains("Unknown table or alias 's'"));
    }

    [Fact]
    public void Analyze_BareTvfAlias_AsValue_Warns()
    {
        // The exact reproduction: `image_draw_bounding_boxes(file, c)`
        // where `c` is a TVF alias — meant to be `c.value`. The runtime
        // throws "Name 'c' is not a declared variable in scope and is
        // not a column in the current row." The analyzer should surface
        // a clearer "use c.<column>" warning so the bug is caught at
        // edit time rather than after a 20-second query setup.
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("items", "file")],
            functions: [Function("unnest", "array"), Function("image_draw_bounding_boxes", "img", "detections")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT image_draw_bounding_boxes(file, c) FROM items a CROSS JOIN unnest(a.file) c",
            manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Message.Contains("'c' is a table or subquery alias"));
    }

    [Fact]
    public void Analyze_TypoColumn_InQueryWithLet_StillWarns()
    {
        // Regression: previously, any LET in the SELECT registered the
        // LET name into opaqueAliases as a side effect of suppressing
        // unknown-column warnings for the LET ref itself. That
        // suppression was scope-blind — `filex` (a typo for `file`)
        // slipped past whenever any LET existed in the same SELECT.
        // LET names now live in a separate set so their presence
        // doesn't blanket-suppress unknown-column checks.
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("items", "file", "id")],
            functions: [Function("yolox", "img")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT LET classes = yolox(a.file), filex FROM items a",
            manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Message.Contains("Unknown column 'filex'"));
    }

    [Fact]
    public void Analyze_LetBindingBareReference_DoesNotWarn_AsUnknownColumn()
    {
        // Companion: the LET name itself still resolves as a value
        // (no false-positive unknown-column warning).
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("items", "file", "id")],
            functions: [Function("yolox", "img")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT LET classes = yolox(a.file), classes FROM items a",
            manifest);

        Assert.DoesNotContain(diagnostics, diagnostic =>
            diagnostic.Message.Contains("Unknown column 'classes'"));
    }

    [Fact]
    public void Analyze_TypoColumn_InQueryWithUnnest_Warns()
    {
        // Regression: previously, a bare typo like `filex` instead of
        // `file` in a SELECT slipped past the analyzer because the
        // `unnest(...) c` source was registered as opaque, blanket-
        // suppressing every unknown-column warning. The TVF's known
        // output column names are now resolved (`unnest` → `value`),
        // so refs to names the TVF doesn't produce fail correctly.
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("items", "file", "id")],
            functions: [Function("unnest", "array")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT filex FROM items a CROSS JOIN unnest(a.id) c",
            manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Message.Contains("Unknown column 'filex'"));
    }

    [Fact]
    public void Analyze_UnnestValueColumn_DoesNotWarn()
    {
        // Companion: bare `value` after `unnest(...) c` is the TVF's
        // actual output column name, so it resolves cleanly.
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("items", "file", "id")],
            functions: [Function("unnest", "array")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT value FROM items a CROSS JOIN unnest(a.id) c",
            manifest);

        Assert.DoesNotContain(diagnostics, diagnostic =>
            diagnostic.Message.Contains("Unknown column 'value'"));
    }

    [Fact]
    public void Analyze_LetBindingUsedAsStructQualifier_DoesNotWarn()
    {
        // A LET binding can hold a struct (e.g.
        // `LET d = models.depth(img)`) and downstream refs project
        // struct fields via `d.depth`. The 2-part qualifier-validation
        // path must consult the LET-name set alongside FROM/JOIN
        // aliases — otherwise legitimate struct-field access on a LET
        // emits a false "Unknown table or alias" diagnostic in the
        // editor.
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("items", "img")],
            functions: [Function("depth", "img")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT LET d = depth(a.img), d.depth FROM items a",
            manifest);

        Assert.DoesNotContain(diagnostics, diagnostic =>
            diagnostic.Message.Contains("Unknown table or alias 'd'"));
    }

    [Fact]
    public void Analyze_LetBindingInTvfArg_DoesNotWarn()
    {
        // Regression: `unnest(classes)` where `classes` is a LET binding
        // declared in the same SELECT. LET names are tracked in the
        // analyzer's opaque-aliases set (for the "Unknown column"
        // suppression), but they're values — referencing them bare is
        // the intended use, not an alias-as-value misuse. The
        // alias-as-value warning must skip them.
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("items", "file")],
            functions: [Function("unnest", "array"), Function("yolox", "img")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT LET classes = yolox(a.file), c.value "
            + "FROM items a CROSS JOIN unnest(classes) c",
            manifest);

        Assert.DoesNotContain(diagnostics, diagnostic =>
            diagnostic.Message.Contains("'classes' is a table or subquery alias"));
    }

    [Fact]
    public void Analyze_BareTableAlias_AsValue_Warns()
    {
        // Same disambiguation for a non-opaque (regular table) alias:
        // `SELECT t FROM users t` references `t` as a value, which
        // engines that don't support row-as-composite reject. Warning
        // mirrors the TVF case.
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("users", "id", "name")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT t FROM users t", manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Message.Contains("'t' is a table or subquery alias"));
    }

    [Fact]
    public void Analyze_ThreePartReference_UnknownFirstSegment_StillWarns()
    {
        // Unrelated: when the first segment is not a known alias, the
        // 3-part form keeps its `schema.table.column` reading and the
        // qualifier check still fires. Guards against the disambiguation
        // accidentally swallowing real typos.
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("users", "id")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT bogus.users.id FROM users", manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Message.Contains("Unknown table or alias 'bogus.users'"));
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
            functions: [TypedFunction("length", "Int32", ("value", "String"))]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT length(price) FROM t", manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("length") &&
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
            functions: [TypedFunction("length", "Int32", ("value", "String"))]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT length('hello') FROM t", manifest);

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
        Table("system.functions", "function_name", "category"),
        Table("system.function_parameters", "function_name", "parameter_name", "ordinal_position"),
        Table("system.indexes", "table_name", "column_name", "index_type", "entry_count"),
        Table("system.interactions", "left_column", "right_column", "pearson"),
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
            "SELECT function_name FROM system.functions", VirtualSchemaManifest());

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Analyze_DatumCatalogFunctionParameters_NoWarning()
    {
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT parameter_name FROM system.function_parameters", VirtualSchemaManifest());

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Analyze_DatumCatalogIndexes_NoWarning()
    {
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT index_type FROM system.indexes", VirtualSchemaManifest());

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Analyze_DatumCatalogInteractions_NoWarning()
    {
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT pearson FROM system.interactions", VirtualSchemaManifest());

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

    // ───────────────────── S7e — procedure-in-expression ─────────────────────

    [Fact]
    public void Procedure_InSelectExpression_EmitsWarning()
    {
        // S7d locks the rule: procedures REQUIRE CALL. The semantic analyzer
        // surfaces this at edit time so the user sees the diagnostic in
        // their editor before running the query.
        LanguageServerManifest manifest = new()
        {
            Tables = [Table("t", "id")],
            Functions = [],
            Keywords = [],
            Procedures =
            [
                new ProcedureEntry { SchemaName = "public", Name = "tally" },
            ],
        };

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT tally(id) FROM t", manifest);

        Assert.Contains(diagnostics, d =>
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.Contains("procedure", StringComparison.OrdinalIgnoreCase) &&
            d.Message.Contains("CALL"));
    }

    [Fact]
    public void Procedure_QualifiedInSelectExpression_EmitsWarning()
    {
        LanguageServerManifest manifest = new()
        {
            Tables = [Table("t", "id")],
            Functions = [],
            Keywords = [],
            Procedures =
            [
                new ProcedureEntry { SchemaName = "myapp", Name = "tally" },
            ],
        };

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT myapp.tally(id) FROM t", manifest);

        Assert.Contains(diagnostics, d =>
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.Contains("myapp.tally") &&
            d.Message.Contains("CALL"));
    }

    [Fact]
    public void Udf_QualifiedInSelect_ResolvesWithoutWarning()
    {
        LanguageServerManifest manifest = new()
        {
            Tables = [Table("t", "id")],
            Functions = [],
            Keywords = [],
            Udfs =
            [
                new UdfEntry { SchemaName = "myapp", Name = "shout" },
            ],
        };

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT myapp.shout(id) FROM t", manifest);

        Assert.DoesNotContain(diagnostics, d =>
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.Contains("Unknown function"));
    }

    [Fact]
    public void ModelCall_DoesNotEmitUnknownFunctionWarning()
    {
        // Models register through their own catalog (ModelEntry) rather
        // than the UDF or function registry. Without the dedicated
        // `models.` lookup in ResolvesToFunction, every `models.X(...)`
        // call surfaced as a spurious yellow squiggle in the editor.
        LanguageServerManifest manifest = new()
        {
            Tables = [Table("t", "img")],
            Functions = [],
            Keywords = [],
            Models =
            [
                new ModelEntry
                {
                    Name = "depth_anything_v3_large_meters",
                    OutputKind = "Array<Float32>",
                    Parameters =
                    [
                        new ParameterSignature { Name = "img", Kind = "Image" },
                    ],
                },
            ],
        };

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT models.depth_anything_v3_large_meters(img) FROM t", manifest);

        Assert.DoesNotContain(diagnostics, d =>
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.Contains("Unknown function"));
    }

    [Fact]
    public void ArrayReturn_FlowingIntoArrayParameter_DoesNotWarn()
    {
        // `models.M(img) → Array<Float32>` feeding into a consumer that
        // declares `Array<Float32>` must not surface a type mismatch.
        // Regression for the issue where the consumer's parameter Kind
        // dropped the `Array<...>` wrapper, so strict-equality and the
        // numeric-family check both rejected the legitimate flow.
        // Models are dual-registered as scalar functions in the catalog;
        // mirror that here so TryInferType can resolve the inner call's
        // return type through the Functions list.
        LanguageServerManifest manifest = new()
        {
            Tables =
            [
                TypedTable("t",
                    new TableColumnEntry { Name = "img", Kind = "Image", Nullable = false }),
            ],
            Functions =
            [
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "consume_floats",
                    Parameters =
                    [
                        new ParameterSignature { Name = "data", Kind = "Array<Float32>" },
                    ],
                    ReturnType = "Float32",
                },
                new FunctionSignature
                {
                    SchemaName = "models",
                    Name = "depth_anything_v3_large_meters",
                    Parameters = [new ParameterSignature { Name = "img", Kind = "Image" }],
                    ReturnType = "Array<Float32>",
                },
            ],
            Keywords = [],
            Models =
            [
                new ModelEntry
                {
                    Name = "depth_anything_v3_large_meters",
                    OutputKind = "Array<Float32>",
                    Parameters = [new ParameterSignature { Name = "img", Kind = "Image" }],
                },
            ],
        };

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT consume_floats(models.depth_anything_v3_large_meters(img)) FROM t",
            manifest);

        Assert.DoesNotContain(diagnostics, d =>
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.Contains("expects", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OverloadedFunction_AlternativeShapeMatch_DoesNotWarn()
    {
        // `point_cloud_from_depth_pinhole` has two signature variants —
        // (Image, Image, Float32) and (Image, Array<Float32>, Float32).
        // A call passing an Array<Float32> as the second arg matches the
        // second variant and must not warn. The manifest now carries the
        // alternative shape via AdditionalParameterShapes so the analyzer
        // can try every variant before flagging a mismatch.
        LanguageServerManifest manifest = new()
        {
            Tables =
            [
                TypedTable("t",
                    new TableColumnEntry { Name = "color", Kind = "Image", Nullable = false },
                    new TableColumnEntry { Name = "fov", Kind = "Float32", Nullable = false }),
            ],
            Functions =
            [
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "point_cloud_from_depth_pinhole",
                    Parameters =
                    [
                        new ParameterSignature { Name = "color", Kind = "Image" },
                        new ParameterSignature { Name = "depth", Kind = "Image" },
                        new ParameterSignature { Name = "fov_deg", Kind = "Float32" },
                    ],
                    ReturnType = "PointCloud",
                    AdditionalParameterShapes =
                    [
                        [
                            new ParameterSignature { Name = "color", Kind = "Image" },
                            new ParameterSignature { Name = "depth", Kind = "Array<Float32>" },
                            new ParameterSignature { Name = "fov_deg", Kind = "Float32" },
                        ],
                    ],
                },
                new FunctionSignature
                {
                    SchemaName = "models",
                    Name = "depth_model",
                    Parameters = [new ParameterSignature { Name = "img", Kind = "Image" }],
                    ReturnType = "Array<Float32>",
                },
            ],
            Keywords = [],
            Models =
            [
                new ModelEntry
                {
                    Name = "depth_model",
                    OutputKind = "Array<Float32>",
                    Parameters = [new ParameterSignature { Name = "img", Kind = "Image" }],
                },
            ],
        };

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT point_cloud_from_depth_pinhole(color, models.depth_model(color), fov) FROM t",
            manifest);

        Assert.DoesNotContain(diagnostics, d =>
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.Contains("expects", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("NumericScalar", "Float32")]
    [InlineData("NumericScalar", "Int32")]
    [InlineData("NumericScalar", "UInt64")]
    [InlineData("IntegerFamily", "Int32")]
    [InlineData("FloatFamily", "Float64")]
    [InlineData("Temporal", "Date")]
    [InlineData("TextLike", "String")]
    public void FamilyLabelled_ConcreteKind_DoesNotWarn(string familyLabel, string actualKind)
    {
        // Parameters declared via DataKindMatcher.Family(...) surface in
        // the manifest as the family enum name (`NumericScalar`,
        // `IntegerFamily`, etc.). The compatibility check now recognises
        // those labels and accepts any kind in the family — previously
        // every concrete-kind argument was wrongly flagged.
        LanguageServerManifest manifest = new()
        {
            Tables =
            [
                TypedTable("t",
                    new TableColumnEntry { Name = "v", Kind = actualKind, Nullable = false }),
            ],
            Functions =
            [
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "draw_particles",
                    Parameters =
                    [
                        new ParameterSignature { Name = "p", Kind = familyLabel },
                    ],
                    ReturnType = "Drawing",
                },
            ],
            Keywords = [],
        };

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT draw_particles(v) FROM t", manifest);

        Assert.DoesNotContain(diagnostics, d =>
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.Contains("expects", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StringEnumLabelled_StringArgument_DoesNotWarn()
    {
        // `StringEnumMatcher` (used by e.g. `blend(content, mode)`) renders
        // its Kind in the manifest as `"String (one of 17 values)"` — the
        // parenthesised tail is an LS hint, not a separate type. A plain
        // String argument must still be accepted; previously the
        // string-equality check rejected it with
        // `expects String (one of 17 values), got String`.
        LanguageServerManifest manifest = new()
        {
            Tables =
            [
                TypedTable("t",
                    new TableColumnEntry { Name = "s", Kind = "String", Nullable = false }),
            ],
            Functions =
            [
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "blend",
                    Parameters =
                    [
                        new ParameterSignature { Name = "content", Kind = "Drawing" },
                        new ParameterSignature
                        {
                            Name = "mode",
                            Kind = "String (one of 17 values)",
                            EnumValues = ["add", "multiply", "screen"],
                        },
                    ],
                    ReturnType = "Drawing",
                },
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "draw_rect",
                    Parameters = [],
                    ReturnType = "Drawing",
                },
            ],
            Keywords = [],
        };

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT blend(draw_rect(), s) FROM t", manifest);

        Assert.DoesNotContain(diagnostics, d =>
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.Contains("expects", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OneOfLabelled_ListedKind_DoesNotWarn()
    {
        // `OneOfMatcher` (used by e.g. `point_x(point)`) renders its Kind
        // in the manifest as `"one of Point2D, Point3D"`. Previously the
        // string-equality check rejected a legitimate Point3D argument
        // with `expects one of Point2D, Point3D, got Point3D`.
        LanguageServerManifest manifest = new()
        {
            Tables =
            [
                TypedTable("t",
                    new TableColumnEntry { Name = "p", Kind = "Point3D", Nullable = false }),
            ],
            Functions =
            [
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "point_x",
                    Parameters =
                    [
                        new ParameterSignature { Name = "point", Kind = "one of Point2D, Point3D" },
                    ],
                    ReturnType = "Float32",
                },
            ],
            Keywords = [],
        };

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT point_x(p) FROM t", manifest);

        Assert.DoesNotContain(diagnostics, d =>
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.Contains("expects", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OneOfLabelled_NonListedKind_StillWarns()
    {
        // Negative: a kind not in the `one of` list still warns.
        LanguageServerManifest manifest = new()
        {
            Tables =
            [
                TypedTable("t",
                    new TableColumnEntry { Name = "s", Kind = "String", Nullable = false }),
            ],
            Functions =
            [
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "point_x",
                    Parameters =
                    [
                        new ParameterSignature { Name = "point", Kind = "one of Point2D, Point3D" },
                    ],
                    ReturnType = "Float32",
                },
            ],
            Keywords = [],
        };

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT point_x(s) FROM t", manifest);

        Assert.Contains(diagnostics, d =>
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.Contains("expects", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FamilyLabelled_NonMemberKind_StillWarns()
    {
        // Negative test: `String` should not slip through a
        // `NumericScalar` slot just because the family branch was added.
        LanguageServerManifest manifest = new()
        {
            Tables =
            [
                TypedTable("t",
                    new TableColumnEntry { Name = "name", Kind = "String", Nullable = false }),
            ],
            Functions =
            [
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "draw_particles",
                    Parameters =
                    [
                        new ParameterSignature { Name = "p", Kind = "NumericScalar" },
                    ],
                    ReturnType = "Drawing",
                },
            ],
            Keywords = [],
        };

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT draw_particles(name) FROM t", manifest);

        Assert.Contains(diagnostics, d =>
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.Contains("NumericScalar"));
    }

    [Fact]
    public void FunctionCall_TooFewArguments_WarnsAtEditTime()
    {
        // `brighten(image, intensity)` requires two args; passing one
        // should surface a squiggle instead of waiting until the engine
        // throws at runtime. Without an arg-count gate, the per-arg type
        // loop iterates the args we have, finds them compatible, and
        // returns no diagnostic.
        LanguageServerManifest manifest = new()
        {
            Tables =
            [
                TypedTable("t",
                    new TableColumnEntry { Name = "img", Kind = "Image", Nullable = false }),
            ],
            Functions =
            [
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "brighten",
                    Parameters =
                    [
                        new ParameterSignature { Name = "image", Kind = "Image" },
                        new ParameterSignature { Name = "intensity", Kind = "NumericScalar" },
                    ],
                    ReturnType = "Image",
                },
            ],
            Keywords = [],
        };

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT brighten(img) FROM t", manifest);

        Assert.Contains(diagnostics, d =>
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.Contains("brighten()") &&
            d.Message.Contains("expects 2"));
    }

    [Fact]
    public void FunctionCall_TooManyArguments_WarnsAtEditTime()
    {
        LanguageServerManifest manifest = new()
        {
            Tables =
            [
                TypedTable("t",
                    new TableColumnEntry { Name = "img", Kind = "Image", Nullable = false }),
            ],
            Functions =
            [
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "to_string",
                    Parameters = [new ParameterSignature { Name = "value", Kind = "Any" }],
                    ReturnType = "String",
                },
            ],
            Keywords = [],
        };

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT to_string(img, img) FROM t", manifest);

        Assert.Contains(diagnostics, d =>
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.Contains("to_string()") &&
            d.Message.Contains("got 2"));
    }

    [Fact]
    public void FunctionCall_OptionalParameter_OmittedCallDoesNotWarn()
    {
        // Optional trailing parameter — call site may omit it, and the
        // arg-count gate must not false-positive in that case.
        LanguageServerManifest manifest = new()
        {
            Tables =
            [
                TypedTable("t",
                    new TableColumnEntry { Name = "img", Kind = "Image", Nullable = false }),
            ],
            Functions =
            [
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "brighten",
                    Parameters =
                    [
                        new ParameterSignature { Name = "image", Kind = "Image" },
                        new ParameterSignature { Name = "intensity", Kind = "NumericScalar", IsOptional = true },
                    ],
                    ReturnType = "Image",
                },
            ],
            Keywords = [],
        };

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT brighten(img) FROM t", manifest);

        Assert.DoesNotContain(diagnostics, d =>
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.Contains("brighten()"));
    }

    [Fact]
    public void FunctionCall_Variadic_AcceptsAnyExtraCount()
    {
        // Variadic functions (e.g. `concat(...)`) — the manifest renders
        // the variadic slot with a leading `...` name. The arity gate
        // must treat that as unbounded.
        LanguageServerManifest manifest = new()
        {
            Tables = [],
            Functions =
            [
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "concat",
                    Parameters =
                    [
                        new ParameterSignature { Name = "...parts", Kind = "String", IsOptional = true },
                    ],
                    ReturnType = "String",
                },
            ],
            Keywords = [],
        };

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT concat('a', 'b', 'c', 'd')", manifest);

        Assert.DoesNotContain(diagnostics, d =>
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.Contains("concat()"));
    }

    [Fact]
    public void ArrayLiteral_FlowingIntoArrayParameter_DoesNotWarn()
    {
        // `[draw_particles(...)]` desugars to `array(draw_particles(...))`.
        // The analyzer's `array`-aware element inference plus the
        // manifest's non-double-wrapped return type combine so the call
        // resolves to `Array<Drawing>` and matches `draw_group`'s
        // `Array<Drawing>` parameter cleanly.
        LanguageServerManifest manifest = new()
        {
            Tables = [],
            Functions =
            [
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "array",
                    Parameters =
                    [
                        new ParameterSignature { Name = "...elements", Kind = "Any", IsOptional = true },
                    ],
                    // Mirrors the post-fix manifest rendering for
                    // `ArrayOf(Custom(...))` rules — the placeholder text
                    // surfaces only when the analyzer can't infer the
                    // element kind from the args.
                    ReturnType = "Array<same as element kind (String when empty)>",
                },
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "draw_particles",
                    Parameters = [new ParameterSignature { Name = "count", Kind = "Int32" }],
                    ReturnType = "Drawing",
                },
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "draw_group",
                    Parameters =
                    [
                        new ParameterSignature { Name = "drawings", Kind = "Array<Drawing>" },
                    ],
                    ReturnType = "Drawing",
                },
            ],
            Keywords = [],
        };

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT draw_group([draw_particles(100)])", manifest);

        Assert.DoesNotContain(diagnostics, d =>
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.Contains("expects", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OverloadedFunction_NoShapeMatches_StillWarns()
    {
        // Negative test for the overload-tryout loop: a call that matches
        // neither variant must still surface a type warning rather than
        // be silently accepted.
        LanguageServerManifest manifest = new()
        {
            Tables =
            [
                TypedTable("t",
                    new TableColumnEntry { Name = "color", Kind = "Image", Nullable = false },
                    new TableColumnEntry { Name = "name", Kind = "String", Nullable = false }),
            ],
            Functions =
            [
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "point_cloud_from_depth_pinhole",
                    Parameters =
                    [
                        new ParameterSignature { Name = "color", Kind = "Image" },
                        new ParameterSignature { Name = "depth", Kind = "Image" },
                        new ParameterSignature { Name = "fov_deg", Kind = "Float32" },
                    ],
                    ReturnType = "PointCloud",
                    AdditionalParameterShapes =
                    [
                        [
                            new ParameterSignature { Name = "color", Kind = "Image" },
                            new ParameterSignature { Name = "depth", Kind = "Array<Float32>" },
                            new ParameterSignature { Name = "fov_deg", Kind = "Float32" },
                        ],
                    ],
                },
            ],
            Keywords = [],
        };

        // `name` is String — matches neither Image nor Array<Float32>.
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT point_cloud_from_depth_pinhole(color, name, 1.0) FROM t",
            manifest);

        Assert.Contains(diagnostics, d =>
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.Contains("expects"));
    }

    [Fact]
    public void ScalarFlowingIntoArrayParameter_StillWarns()
    {
        // Negative test: the array-aware compatibility check must not let
        // a bare scalar flow into an array parameter unchecked.
        LanguageServerManifest manifest = new()
        {
            Tables = [Table("t", "score")],
            Functions =
            [
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "consume_floats",
                    Parameters =
                    [
                        new ParameterSignature { Name = "data", Kind = "Array<Float32>" },
                    ],
                    ReturnType = "Float32",
                },
            ],
            Keywords = [],
        };

        // `score` is a Float32 column being passed into an Array<Float32> param.
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT consume_floats(score) FROM t", manifest);

        Assert.Contains(diagnostics, d =>
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.Contains("Array<Float32>"));
    }

    [Fact]
    public void UnregisteredModelCall_StillEmitsUnknownFunctionWarning()
    {
        // Negative test: a `models.X(...)` call with no matching ModelEntry
        // must still fire the unknown-function diagnostic. Guards against
        // the model lookup accidentally swallowing every `models.`-qualified
        // call regardless of whether the model is actually registered.
        LanguageServerManifest manifest = new()
        {
            Tables = [Table("t", "img")],
            Functions = [],
            Keywords = [],
            Models =
            [
                new ModelEntry { Name = "registered_model", OutputKind = "Float32" },
            ],
        };

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT models.never_registered(img) FROM t", manifest);

        Assert.Contains(diagnostics, d =>
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.Contains("Unknown function"));
    }
}
