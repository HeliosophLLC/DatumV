namespace DatumIngest.Tests.LanguageServer;

using DatumIngest.LanguageServer;
using DatumIngest.Manifest;

/// <summary>
/// Tests for <see cref="CompletionProvider"/> — context-aware SQL completion generation.
/// </summary>
public sealed class CompletionProviderTests : ServiceTestBase
{
    private static LanguageServerManifest CreateTestManifest()
    {
        return new LanguageServerManifest
        {
            Tables =
            [
                new TableSchemaEntry
                {
                    Name = "users",
                    Columns =
                    [
                        new TableColumnEntry { Name = "id", Kind = "Float32", Nullable = false },
                        new TableColumnEntry { Name = "name", Kind = "String", Nullable = false },
                        new TableColumnEntry { Name = "email", Kind = "String", Nullable = true },
                    ],
                },
                new TableSchemaEntry
                {
                    Name = "orders",
                    Columns =
                    [
                        new TableColumnEntry { Name = "order_id", Kind = "Float32", Nullable = false },
                        new TableColumnEntry { Name = "user_id", Kind = "Float32", Nullable = false },
                        new TableColumnEntry { Name = "total", Kind = "Float32", Nullable = false },
                    ],
                },
            ],
            Functions =
            [
                new FunctionSignature
                {
                    Name = "abs",
                    Parameters = [new ParameterSignature { Name = "value", Kind = "Float32" }],
                    ReturnType = "Float32",
                    Description = "Absolute value.",
                },
                new FunctionSignature
                {
                    Name = "unnest",
                    Parameters = [new ParameterSignature { Name = "array_column", Kind = "Vector" }],
                    ReturnType = "Float32",
                    Description = "Expands a vector column.",
                    IsTableValued = true,
                },
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "video_unnest_frames",
                    Parameters =
                    [
                        new ParameterSignature { Name = "source", Kind = "String" },
                        new ParameterSignature { Name = "start_frame", Kind = "Int32", IsOptional = true },
                    ],
                    ReturnType = "table(frame_index Int32, frame VideoFrame)",
                    Description = "Emits one row per video frame.",
                    IsTableValued = true,
                    OutputColumns =
                    [
                        new TableColumnEntry { Name = "frame_index", Kind = "Int32", Nullable = false },
                        new TableColumnEntry { Name = "frame", Kind = "VideoFrame", Nullable = false },
                    ],
                },
            ],
            Keywords = ["SELECT", "FROM", "WHERE", "JOIN", "ON", "ORDER", "BY", "AS", "INTO"],
        };
    }

    private static CompletionProvider CreateProvider()
    {
        return new CompletionProvider(CreateTestManifest());
    }

    // ───────────────────── Statement start ─────────────────────

    [Fact]
    public void GetCompletions_EmptyInput_OffersSelectKeyword()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("", 0);

        Assert.Contains(items, item => item.Label == "SELECT" && item.Kind == CompletionItemKind.Keyword);
    }

    // ───────────────────── After SELECT ─────────────────────

    [Fact]
    public void GetCompletions_AfterSelect_OffersColumnsAndFunctions()
    {
        CompletionProvider provider = CreateProvider();

        // FROM users brings users' columns into scope; without it we'd see
        // no column suggestions at all (see ColumnFiltering tests further
        // down).
        CompletionItem[] items = provider.GetCompletions("SELECT  FROM users", 7);

        Assert.Contains(items, item => item.Label == "id" && item.Kind == CompletionItemKind.Column);
        Assert.Contains(items, item => item.Label == "name" && item.Kind == CompletionItemKind.Column);
        Assert.Contains(items, item => item.Label == "abs" && item.Kind == CompletionItemKind.Function);
        Assert.Contains(items, item => item.Label == "FROM" && item.Kind == CompletionItemKind.Keyword);
    }

    [Fact]
    public void GetCompletions_AfterSelectWithPrefix_FiltersByPrefix()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT na FROM users", 9);

        Assert.Contains(items, item => item.Label == "name");
        Assert.DoesNotContain(items, item => item.Label == "id");
        Assert.DoesNotContain(items, item => item.Label == "abs");
    }

    // ───────────────────── TVF output columns ─────────────────────

    [Fact]
    public void GetCompletions_AfterSelect_OffersTvfOutputColumns()
    {
        CompletionProvider provider = CreateProvider();

        const string sql = "SELECT  FROM video_unnest_frames('x.mp4') vid";
        // Cursor sits in the gap after SELECT.
        CompletionItem[] items = provider.GetCompletions(sql, 7);

        Assert.Contains(items, item => item.Label == "frame_index" && item.Kind == CompletionItemKind.Column);
        Assert.Contains(items, item => item.Label == "frame" && item.Kind == CompletionItemKind.Column);
    }

    [Fact]
    public void GetCompletions_AfterAliasDot_ResolvesToTvfOutputColumns()
    {
        CompletionProvider provider = CreateProvider();

        // `vid.` — cursor right after the dot.
        const string sql = "SELECT vid. FROM video_unnest_frames('x.mp4') vid";
        int offset = sql.IndexOf("vid.", StringComparison.Ordinal) + "vid.".Length;
        CompletionItem[] items = provider.GetCompletions(sql, offset);

        Assert.Contains(items, item => item.Label == "frame_index" && item.Kind == CompletionItemKind.Column);
        Assert.Contains(items, item => item.Label == "frame" && item.Kind == CompletionItemKind.Column);
        // Persistent-table columns must not bleed into a TVF alias dot lookup.
        Assert.DoesNotContain(items, item => item.Label == "id");
    }

    [Fact]
    public void GetCompletions_PrefixFilteringWorksOverTvfColumns()
    {
        CompletionProvider provider = CreateProvider();

        const string sql = "SELECT fram FROM video_unnest_frames('x.mp4') vid";
        int offset = sql.IndexOf("fram", StringComparison.Ordinal) + "fram".Length;
        CompletionItem[] items = provider.GetCompletions(sql, offset);

        Assert.Contains(items, item => item.Label == "frame_index");
        Assert.Contains(items, item => item.Label == "frame");
    }

    // ───────────────────── CTE projections ─────────────────────

    [Fact]
    public void GetCompletions_AfterCteAliasDot_OffersCteProjectedColumns()
    {
        CompletionProvider provider = CreateProvider();

        // The cursor is positioned right after `f1.` in an otherwise
        // parseable query — using a complete query keeps the recovering
        // parser happy so the CTE schema is built, but the classifier still
        // sees AfterDot at the cursor.
        const string sql =
            "WITH frames AS (SELECT frame_index, frame FROM video_unnest_frames('x.mp4') vid) " +
            "SELECT f1.frame_index FROM frames f1";
        int offset = sql.LastIndexOf("f1.", StringComparison.Ordinal) + "f1.".Length;
        CompletionItem[] items = provider.GetCompletions(sql, offset);

        Assert.Contains(items, item => item.Label == "frame_index" && item.Kind == CompletionItemKind.Column);
        Assert.Contains(items, item => item.Label == "frame" && item.Kind == CompletionItemKind.Column);
        Assert.DoesNotContain(items, item => item.Label == "id");
    }

    [Fact]
    public void GetCompletions_AfterCteNameDot_OffersCteProjectedColumns()
    {
        CompletionProvider provider = CreateProvider();

        // No alias on the FROM source — bare CTE name should still qualify.
        const string sql =
            "WITH frames AS (SELECT frame_index FROM video_unnest_frames('x.mp4') vid) " +
            "SELECT frames.frame_index FROM frames";
        int offset = sql.LastIndexOf("frames.", StringComparison.Ordinal) + "frames.".Length;
        CompletionItem[] items = provider.GetCompletions(sql, offset);

        Assert.Contains(items, item => item.Label == "frame_index");
    }

    [Fact]
    public void GetCompletions_AfterSelect_OffersUnqualifiedCteColumns()
    {
        CompletionProvider provider = CreateProvider();

        const string sql =
            "WITH frames AS (SELECT frame_index, frame FROM video_unnest_frames('x.mp4') vid) " +
            "SELECT frame_index FROM frames";
        // Position cursor right after `SELECT ` (offset 76) so the
        // classifier lands in AfterSelect.
        int offset = sql.LastIndexOf("SELECT frame_index FROM frames", StringComparison.Ordinal)
            + "SELECT ".Length;
        CompletionItem[] items = provider.GetCompletions(sql, offset);

        Assert.Contains(items, item => item.Label == "frame_index");
        Assert.Contains(items, item => item.Label == "frame");
    }

    [Fact]
    public void GetCompletions_RenamedCteColumn_UsesAlias()
    {
        CompletionProvider provider = CreateProvider();

        // `f1.frame AS prev` renames the column in prev_curr's output.
        const string sql =
            "WITH frames AS (SELECT frame_index, frame FROM video_unnest_frames('x.mp4') vid)," +
            "prev_curr AS (SELECT f1.frame_index, f1.frame AS prev FROM frames f1) " +
            "SELECT prev_curr.prev FROM prev_curr";
        int offset = sql.LastIndexOf("prev_curr.", StringComparison.Ordinal) + "prev_curr.".Length;
        CompletionItem[] items = provider.GetCompletions(sql, offset);

        Assert.Contains(items, item => item.Label == "frame_index");
        Assert.Contains(items, item => item.Label == "prev");
        Assert.DoesNotContain(items, item => item.Label == "frame");
    }

    // ───────────────────── After FROM ─────────────────────

    [Fact]
    public void GetCompletions_AfterFrom_OffersTablesAndTableValuedFunctions()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT * FROM ", 14);

        Assert.Contains(items, item => item.Label == "users" && item.Kind == CompletionItemKind.Table);
        Assert.Contains(items, item => item.Label == "orders" && item.Kind == CompletionItemKind.Table);
        Assert.Contains(items, item => item.Label == "unnest" && item.Kind == CompletionItemKind.Function);
        // Should NOT include scalar functions in FROM context.
        Assert.DoesNotContain(items, item => item.Label == "abs");
    }

    // ───────────────────── After JOIN ─────────────────────

    [Fact]
    public void GetCompletions_AfterJoin_OffersTables()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT * FROM users JOIN ", 25);

        Assert.Contains(items, item => item.Label == "orders" && item.Kind == CompletionItemKind.Table);
    }

    // ───────────────────── After WHERE ─────────────────────

    [Fact]
    public void GetCompletions_AfterWhere_OffersColumnsAndExpressionKeywords()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT * FROM users WHERE ", 26);

        Assert.Contains(items, item => item.Label == "id" && item.Kind == CompletionItemKind.Column);
        Assert.Contains(items, item => item.Label == "AND" && item.Kind == CompletionItemKind.Keyword);
        Assert.Contains(items, item => item.Label == "OR" && item.Kind == CompletionItemKind.Keyword);
    }

    // ───────────────────── After ORDER BY ─────────────────────

    [Fact]
    public void GetCompletions_AfterOrderBy_OffersColumnsAndSortDirections()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT * FROM users ORDER BY ", 29);

        Assert.Contains(items, item => item.Label == "id" && item.Kind == CompletionItemKind.Column);
        Assert.Contains(items, item => item.Label == "ASC" && item.Kind == CompletionItemKind.Keyword);
        Assert.Contains(items, item => item.Label == "DESC" && item.Kind == CompletionItemKind.Keyword);
    }

    // ───────────────────── Dot-qualified ─────────────────────

    [Fact]
    public void GetCompletions_AfterDot_OffersColumnsFromQualifiedTable()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT users.", 13);

        Assert.Contains(items, item => item.Label == "id");
        Assert.Contains(items, item => item.Label == "name");
        Assert.Contains(items, item => item.Label == "email");
        // Columns from other tables should NOT appear.
        Assert.DoesNotContain(items, item => item.Label == "order_id");
    }

    [Fact]
    public void GetCompletions_AfterDotWithPrefix_FiltersQualifiedColumns()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT users.na", 15);

        Assert.Contains(items, item => item.Label == "name");
        Assert.DoesNotContain(items, item => item.Label == "id");
    }

    /// <summary>
    /// Regression: <c>CALL</c> followed by a space had no completion zone,
    /// so the classifier fell back to <see cref="CompletionZoneKind.StatementStart"/>
    /// and offered statement-start keywords instead of procedure names.
    /// After the fix, <c>AfterCall</c> surfaces procedures from search-path
    /// schemas plus off-search-path schema names for drill-in.
    /// </summary>
    [Fact]
    public void GetCompletions_AfterCall_OffersProceduresUdfsAndCallableSchemas()
    {
        LanguageServerManifest manifest = new()
        {
            SearchPath = ["public", "system"],
            Tables =
            [
                // Table-only schema: should NOT surface in the CALL popup —
                // datum_catalog hosts no callables (no procedures / UDFs /
                // built-in functions), so suggesting it would be misleading.
                new TableSchemaEntry
                {
                    Name = "datum_catalog.functions",
                    Columns = [],
                },
            ],
            Functions =
            [
                // Function-bearing off-search-path schema: SHOULD surface
                // — CALL lowers to SELECT, so tokenizer.encode is reachable.
                new FunctionSignature
                {
                    SchemaName = "tokenizer",
                    Name = "encode",
                    Parameters = [new ParameterSignature { Name = "text", Kind = "String" }],
                    Description = "Tokenize text.",
                },
                // Search-path scalar function: SHOULD surface as a bare
                // name. `CALL concat('a','b')` lowers to
                // `SELECT concat('a','b')` and works.
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "concat",
                    Parameters =
                    [
                        new ParameterSignature { Name = "a", Kind = "String" },
                        new ParameterSignature { Name = "b", Kind = "String" },
                    ],
                    ReturnType = "String",
                    Description = "Concatenate strings.",
                },
            ],
            Udfs =
            [
                // Search-path UDF: SHOULD surface as a bare name. This is
                // the user's reported case — `CREATE FUNCTION Test()` lands
                // in public and they expect `CALL Test()` to autocomplete.
                new UdfEntry
                {
                    SchemaName = "public",
                    Name = "Test",
                    Parameters = [],
                    ReturnType = "String",
                },
            ],
            Procedures =
            [
                new ProcedureEntry
                {
                    SchemaName = "public",
                    Name = "do_something",
                    Parameters = [new ParameterSignature { Name = "x", Kind = "Int32" }],
                },
                // Procedure-bearing custom schema: SHOULD surface so the
                // user can drill `admin.` for the procedure list.
                new ProcedureEntry
                {
                    SchemaName = "admin",
                    Name = "compact",
                    Parameters = [],
                },
            ],
            Keywords = ["SELECT", "FROM", "CALL"],
        };
        CompletionProvider provider = new(manifest);

        CompletionItem[] items = provider.GetCompletions("CALL ", 5);

        // Search-path procedures + UDFs + built-in scalars all surface
        // as bare names — every scalar-callable thing is reachable from
        // CALL via the lower-to-SELECT path.
        Assert.Contains(items, i => i.Label == "do_something" && i.Kind == CompletionItemKind.Function);
        Assert.Contains(items, i => i.Label == "Test" && i.Kind == CompletionItemKind.Function);
        Assert.Contains(items, i => i.Label == "concat" && i.Kind == CompletionItemKind.Function);

        // Procedure-bearing and function-bearing off-search-path schemas
        // both surface — anything CALLable via lower-to-SELECT counts.
        Assert.Contains(items, i => i.Label == "admin" && i.Kind == CompletionItemKind.Schema);
        Assert.Contains(items, i => i.Label == "tokenizer" && i.Kind == CompletionItemKind.Schema);

        // `public` also surfaces — users with UDFs/procedures in public
        // expect to be able to drill it explicitly.
        Assert.Contains(items, i => i.Label == "public" && i.Kind == CompletionItemKind.Schema);

        // Table-only schemas don't surface — nothing scalar-callable.
        Assert.DoesNotContain(items, i => i.Label == "datum_catalog" && i.Kind == CompletionItemKind.Schema);
    }

    /// <summary>
    /// Regression: aggregate and window functions must not leak into the
    /// CALL popup (or any scalar-expression popup). They need GROUP BY /
    /// OVER context, so a standalone <c>CALL SUM(x)</c> wouldn't plan.
    /// Earlier <c>AddScalarFunctions</c> filtered only <c>IsTableValued</c>
    /// — aggregates and window functions slipped through.
    /// </summary>
    [Fact]
    public void GetCompletions_AfterCall_DoesNotSurfaceAggregatesOrWindowFunctions()
    {
        LanguageServerManifest manifest = new()
        {
            SearchPath = ["public", "system"],
            Tables = [],
            Functions =
            [
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "sum",
                    Parameters = [new ParameterSignature { Name = "x", Kind = "Float32" }],
                    ReturnType = "Float32",
                    IsAggregate = true,
                },
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "row_number",
                    Parameters = [],
                    ReturnType = "Int64",
                    IsWindowFunction = true,
                },
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "abs",
                    Parameters = [new ParameterSignature { Name = "v", Kind = "Float32" }],
                    ReturnType = "Float32",
                },
            ],
            Keywords = ["CALL"],
        };
        CompletionProvider provider = new(manifest);

        CompletionItem[] items = provider.GetCompletions("CALL ", 5);

        Assert.Contains(items, i => i.Label == "abs");
        Assert.DoesNotContain(items, i => i.Label == "sum");
        Assert.DoesNotContain(items, i => i.Label == "row_number");
    }

    /// <summary>
    /// Regression: built-in functions registered in non-<c>system</c> schemas
    /// (e.g. <c>inference.onnx_inspect</c>, <c>tokenizer.encode</c>) must
    /// surface when the user qualifies with the schema name. Earlier the
    /// completion provider hardcoded <c>schema == "system"</c> before
    /// surfacing built-ins, which suppressed everything for the new schemas.
    /// </summary>
    [Fact]
    public void GetCompletions_AfterSchemaDot_OffersBuiltinsFromThatSchema()
    {
        LanguageServerManifest manifest = new()
        {
            SearchPath = ["public", "system"],
            Tables = [],
            Functions =
            [
                new FunctionSignature
                {
                    SchemaName = "inference",
                    Name = "onnx_inspect",
                    Parameters = [new ParameterSignature { Name = "path", Kind = "String" }],
                    IsTableValued = true,
                    Description = "Introspect ONNX file.",
                },
                new FunctionSignature
                {
                    SchemaName = "tokenizer",
                    Name = "encode",
                    Parameters =
                    [
                        new ParameterSignature { Name = "text", Kind = "String" },
                        new ParameterSignature { Name = "path", Kind = "String" },
                    ],
                    ReturnType = "Int64",
                    Description = "Tokenize text.",
                },
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "abs",
                    Parameters = [new ParameterSignature { Name = "value", Kind = "Float32" }],
                    ReturnType = "Float32",
                    Description = "Absolute value.",
                },
            ],
            Keywords = ["SELECT", "FROM"],
        };
        CompletionProvider provider = new(manifest);

        CompletionItem[] inferenceItems = provider.GetCompletions("SELECT * FROM inference.", 24);
        Assert.Contains(inferenceItems, i => i.Label == "onnx_inspect");
        Assert.DoesNotContain(inferenceItems, i => i.Label == "encode");
        Assert.DoesNotContain(inferenceItems, i => i.Label == "abs");

        CompletionItem[] tokenizerItems = provider.GetCompletions("SELECT tokenizer.", 17);
        Assert.Contains(tokenizerItems, i => i.Label == "encode");
        Assert.DoesNotContain(tokenizerItems, i => i.Label == "onnx_inspect");
        Assert.DoesNotContain(tokenizerItems, i => i.Label == "abs");

        // The qualified built-in's Detail line must show the actual schema,
        // not the legacy "system." hardcoded prefix.
        CompletionItem encodeItem = Assert.Single(tokenizerItems, i => i.Label == "encode");
        Assert.StartsWith("tokenizer.encode", encodeItem.Detail);
    }

    /// <summary>
    /// Regression: typing <c>CALL </c> must surface procedures from
    /// search-path schemas (bare) and off-search-path schemas (drillable).
    /// Earlier <c>CALL</c> wasn't recognised by the zone classifier and
    /// fell through to <c>StatementStart</c>, suggesting only top-level
    /// keywords.
    /// </summary>
    [Fact]
    public void GetCompletions_AfterCall_SurfacesProceduresAndSchemas()
    {
        LanguageServerManifest manifest = new()
        {
            SearchPath = ["public", "system"],
            Tables = [],
            Functions =
            [
                new FunctionSignature
                {
                    SchemaName = "tokenizer",
                    Name = "encode",
                    Parameters = [new ParameterSignature { Name = "text", Kind = "String" }],
                    ReturnType = "Int64",
                },
            ],
            Procedures =
            [
                new ProcedureEntry
                {
                    SchemaName = "public",
                    Name = "my_proc",
                    Parameters = [new ParameterSignature { Name = "x", Kind = "Int32" }],
                },
                new ProcedureEntry
                {
                    SchemaName = "tokenizer",
                    Name = "load_pretrained",
                    Parameters = [new ParameterSignature { Name = "name", Kind = "String" }],
                },
            ],
            Keywords = ["SELECT", "FROM", "CALL"],
        };
        CompletionProvider provider = new(manifest);

        CompletionItem[] items = provider.GetCompletions("CALL ", 5);

        // Search-path procedure is offered bare.
        Assert.Contains(items, i => i.Label == "my_proc");
        // Off-search-path procedure NOT offered bare (would need qualification).
        Assert.DoesNotContain(items, i => i.Label == "load_pretrained");
        // Off-search-path schema IS offered so user can drill into it.
        Assert.Contains(items, i => i.Label == "tokenizer" && i.Kind == CompletionItemKind.Schema);
    }

    /// <summary>
    /// Regression: typing a prefix that matches a schema name (e.g. <c>tok</c>
    /// for <c>tokenizer</c>) must surface the schema itself as a completion
    /// item — otherwise users can only reach qualified functions by typing
    /// the full schema name + dot, which assumes prior knowledge of the
    /// schema's existence.
    /// </summary>
    [Fact]
    public void GetCompletions_PrefixMatchesSchema_SurfacesSchemaName()
    {
        LanguageServerManifest manifest = new()
        {
            SearchPath = ["public", "system"],
            Tables = [],
            Functions =
            [
                new FunctionSignature
                {
                    SchemaName = "tokenizer",
                    Name = "encode",
                    Parameters = [new ParameterSignature { Name = "text", Kind = "String" }],
                    ReturnType = "Int64",
                    Description = "Tokenize text.",
                },
                new FunctionSignature
                {
                    SchemaName = "inference",
                    Name = "onnx_inspect",
                    Parameters = [new ParameterSignature { Name = "path", Kind = "String" }],
                    IsTableValued = true,
                    Description = "Introspect ONNX file.",
                },
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "abs",
                    Parameters = [new ParameterSignature { Name = "value", Kind = "Float32" }],
                    ReturnType = "Float32",
                    Description = "Absolute value.",
                },
            ],
            Keywords = ["SELECT", "FROM"],
        };
        CompletionProvider provider = new(manifest);

        // Typing `tok` in an expression context should surface `tokenizer`
        // as a Schema-kind item so the user can drill in.
        CompletionItem[] selectItems = provider.GetCompletions("SELECT tok", 10);
        CompletionItem tokenizerItem = Assert.Single(selectItems, i => i.Label == "tokenizer");
        Assert.Equal(CompletionItemKind.Schema, tokenizerItem.Kind);

        // FROM-zone should also surface schemas that host TVFs.
        CompletionItem[] fromItems = provider.GetCompletions("SELECT * FROM inf", 17);
        Assert.Contains(fromItems, i => i.Label == "inference" && i.Kind == CompletionItemKind.Schema);

        // Whether `system` / `public` surface as schema-kind items is a
        // property of AddSchemaNames, not of the prefix filter. Check it
        // against an unprefixed query — `SELECT ` — so the prefix filter
        // doesn't strip every schema whose label doesn't start with `tok`.
        // Both surface: `system` for catalog-introspection drill-in,
        // `public` because users who `CREATE FUNCTION foo()` land their
        // routines there and expect to see it in the dropdown.
        CompletionItem[] unfilteredItems = provider.GetCompletions("SELECT ", 7);
        Assert.Contains(unfilteredItems, i => i.Label == "system" && i.Kind == CompletionItemKind.Schema);
    }

    /// <summary>
    /// Regression: <c>models</c> surfaces as a schema whenever at least
    /// one model is registered, even though no <c>FunctionSignature</c>
    /// has <c>SchemaName == "models"</c> (models are a separate manifest
    /// list, not function-shaped).
    /// </summary>
    [Fact]
    public void GetCompletions_SurfacesModelsSchema_WhenAnyModelIsRegistered()
    {
        LanguageServerManifest manifest = new()
        {
            SearchPath = ["public", "system"],
            Tables = [],
            Functions = [],
            Models =
            [
                new ModelEntry
                {
                    Name = "mobilenet_v2",
                    OutputKind = "Int32",
                    Category = "vision",
                },
            ],
            Keywords = ["SELECT", "FROM"],
        };
        CompletionProvider provider = new(manifest);

        CompletionItem[] items = provider.GetCompletions("SELECT mod", 10);
        Assert.Contains(items, i => i.Label == "models" && i.Kind == CompletionItemKind.Schema);
    }

    /// <summary>
    /// Regression: unqualified completion must filter to schemas on the
    /// search path. Functions in <c>inference</c>/<c>tokenizer</c>/<c>templates</c>
    /// require qualification, so surfacing them as bare names would suggest
    /// identifiers that don't resolve at parse time.
    /// </summary>
    [Fact]
    public void GetCompletions_Unqualified_HidesNonSearchPathBuiltins()
    {
        LanguageServerManifest manifest = new()
        {
            SearchPath = ["public", "system"],
            Tables = [],
            Functions =
            [
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "abs",
                    Parameters = [new ParameterSignature { Name = "value", Kind = "Float32" }],
                    ReturnType = "Float32",
                    Description = "Absolute value.",
                },
                new FunctionSignature
                {
                    SchemaName = "tokenizer",
                    Name = "encode",
                    Parameters = [new ParameterSignature { Name = "text", Kind = "String" }],
                    ReturnType = "Int64",
                    Description = "Tokenize text.",
                },
            ],
            Keywords = ["SELECT", "FROM"],
        };
        CompletionProvider provider = new(manifest);

        CompletionItem[] items = provider.GetCompletions("SELECT ", 7);
        Assert.Contains(items, i => i.Label == "abs");
        Assert.DoesNotContain(items, i => i.Label == "encode");
    }

    // ───────────────────── INTO / AS — no completions ─────────────────────

    [Fact]
    public void GetCompletions_AfterInto_OffersShard()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT * FROM t INTO ", 21);

        Assert.Contains(items, item => item.Label == "SHARD" && item.Kind == CompletionItemKind.Keyword);
    }

    [Fact]
    public void GetCompletions_AfterAs_ReturnsEmpty()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT x AS ", 12);

        Assert.Empty(items);
    }

    // ───────────────────── Function arguments ─────────────────────

    [Fact]
    public void GetCompletions_InsideFunctionArgs_OffersColumnsAndFunctions()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT abs( FROM users", 11);

        Assert.Contains(items, item => item.Label == "id" && item.Kind == CompletionItemKind.Column);
        Assert.Contains(items, item => item.Label == "abs" && item.Kind == CompletionItemKind.Function);
    }

    // ───────────────────── Sort order ─────────────────────

    [Fact]
    public void GetCompletions_ResultsSortedBySortOrderThenLabel()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT * FROM ", 14);

        // Tables should appear before TVFs (both SortOrder 0 or 1), and within a group, alphabetical.
        int usersIndex = Array.FindIndex(items, item => item.Label == "users");
        int ordersIndex = Array.FindIndex(items, item => item.Label == "orders");
        Assert.True(ordersIndex < usersIndex, "Items should be sorted alphabetically within their sort order.");
    }

    // ───────────────────── Function insert text ─────────────────────

    [Fact]
    public void GetCompletions_FunctionItem_HasParenthesisInInsertText()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT ", 7);

        CompletionItem? absItem = Array.Find(items, item => item.Label == "abs");
        Assert.NotNull(absItem);
        Assert.Equal("abs(", absItem.InsertText);
    }

    // ───────────────────── Quoted table name insert text ─────────────────────

    [Fact]
    public void GetCompletions_TableWithDot_HasBracketQuotedInsertText()
    {
        LanguageServerManifest manifest = new()
        {
            Tables =
            [
                new TableSchemaEntry
                {
                    Name = "adult.data",
                    Columns = [new TableColumnEntry { Name = "age", Kind = "Float32", Nullable = false }],
                },
            ],
            Functions = [],
            Keywords = ["SELECT", "FROM"],
        };
        CompletionProvider provider = new(manifest);

        CompletionItem[] items = provider.GetCompletions("SELECT * FROM ", 14);

        CompletionItem? tableItem = Array.Find(items, item => item.Label == "adult.data");
        Assert.NotNull(tableItem);
        Assert.Equal("\"adult.data\"", tableItem.InsertText);
    }

    [Fact]
    public void GetCompletions_SafeTableName_HasNullInsertText()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT * FROM ", 14);

        CompletionItem? usersItem = Array.Find(items, item => item.Label == "users");
        Assert.NotNull(usersItem);
        Assert.Null(usersItem.InsertText);
    }

    // ───────────────────── DDL completions ─────────────────────

    [Fact]
    public void GetCompletions_AfterCreate_OffersTempAndTable()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("CREATE ", 7);

        Assert.Contains(items, item => item.Label == "TEMP");
        Assert.Contains(items, item => item.Label == "TABLE");
    }

    [Fact]
    public void GetCompletions_InsideCreateTableColumns_OffersTypes()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("CREATE TABLE #t (col1 ", 22);

        Assert.Contains(items, item => item.Label == "Int32");
        Assert.Contains(items, item => item.Label == "Float32");
        Assert.Contains(items, item => item.Label == "String");
        Assert.Contains(items, item => item.Label == "PRIMARY KEY");
    }

    [Fact]
    public void GetCompletions_AfterDrop_OffersTableAndIndex()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("DROP ", 5);

        Assert.Contains(items, item => item.Label == "TABLE");
        Assert.Contains(items, item => item.Label == "INDEX");
    }

    // ───────────────────── DML completions ─────────────────────

    [Fact]
    public void GetCompletions_AfterInsertInto_OffersTables()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("INSERT INTO ", 12);

        Assert.Contains(items, item => item.Label == "users" && item.Kind == CompletionItemKind.Table);
    }

    [Fact]
    public void GetCompletions_AfterUpdate_OffersTables()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("UPDATE ", 7);

        Assert.Contains(items, item => item.Label == "users" && item.Kind == CompletionItemKind.Table);
    }

    [Fact]
    public void GetCompletions_AfterUpdateSet_OffersColumnsAndKeywords()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("UPDATE #t SET ", 14);

        Assert.Contains(items, item => item.Label == "id" && item.Kind == CompletionItemKind.Column);
        Assert.Contains(items, item => item.Label == "WHERE" && item.Kind == CompletionItemKind.Keyword);
    }

    [Fact]
    public void GetCompletions_AfterDeleteFrom_OffersTables()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("DELETE FROM ", 12);

        Assert.Contains(items, item => item.Label == "users" && item.Kind == CompletionItemKind.Table);
    }

    [Fact]
    public void GetCompletions_AfterAlterTable_OffersTablesAndAdd()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("ALTER TABLE ", 12);

        Assert.Contains(items, item => item.Label == "users" && item.Kind == CompletionItemKind.Table);
        Assert.Contains(items, item => item.Label == "ADD" && item.Kind == CompletionItemKind.Keyword);
    }

    // ───────────────────── TABLESAMPLE contextual completions ─────────────────────

    [Fact]
    public void GetCompletions_AfterTablesample_OffersMethodNames()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions(
            "SELECT * FROM users TABLESAMPLE ", 32);

        Assert.Contains(items, item => item.Label == "BERNOULLI");
        Assert.Contains(items, item => item.Label == "SYSTEM");
        Assert.Contains(items, item => item.Label == "STRATIFIED");
        Assert.Contains(items, item => item.Label == "BALANCED");
        // Should NOT include tables or columns — only method names.
        Assert.DoesNotContain(items, item => item.Kind == CompletionItemKind.Table);
        Assert.DoesNotContain(items, item => item.Kind == CompletionItemKind.Column);
    }

    [Fact]
    public void GetCompletions_AfterTablesample_MethodsHaveDocumentation()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions(
            "SELECT * FROM users TABLESAMPLE ", 32);

        CompletionItem stratified = Assert.Single(items, item => item.Label == "STRATIFIED");
        Assert.NotNull(stratified.Detail);
        Assert.NotNull(stratified.Documentation);
        Assert.Contains("ON", stratified.Documentation!);
    }

    [Fact]
    public void GetCompletions_AfterTablesample_WithPrefix_FiltersMethods()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions(
            "SELECT * FROM users TABLESAMPLE B", 33);

        Assert.Contains(items, item => item.Label == "BERNOULLI");
        Assert.Contains(items, item => item.Label == "BALANCED");
        Assert.DoesNotContain(items, item => item.Label == "STRATIFIED");
        Assert.DoesNotContain(items, item => item.Label == "SYSTEM");
    }

    [Fact]
    public void GetCompletions_AfterTablesampleMethodArg_OffersOnAndRepeatable()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions(
            "SELECT * FROM users TABLESAMPLE STRATIFIED(10) ", 47);

        Assert.Contains(items, item => item.Label == "ON");
        Assert.Contains(items, item => item.Label == "REPEATABLE");
        // Should NOT include method names or tables.
        Assert.DoesNotContain(items, item => item.Label == "BERNOULLI");
        Assert.DoesNotContain(items, item => item.Kind == CompletionItemKind.Table);
    }

    [Fact]
    public void GetCompletions_InsideTablesampleArg_ReturnsEmpty()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions(
            "SELECT * FROM users TABLESAMPLE STRATIFIED(", 44);

        // No column names, no functions — the argument is a numeric literal.
        Assert.Empty(items);
    }

    [Fact]
    public void GetCompletions_AfterTablesampleMethodsInsertParens()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions(
            "SELECT * FROM users TABLESAMPLE ", 32);

        CompletionItem balanced = Assert.Single(items, item => item.Label == "BALANCED");
        Assert.Equal("BALANCED(", balanced.InsertText);
    }

    // ───────────────────── Clause continuation: FROM source ─────────────────────

    [Fact]
    public void GetCompletions_AfterFromSource_OffersClauseKeywords()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT * FROM users ", 20);

        Assert.Contains(items, item => item.Label == "WHERE" && item.Kind == CompletionItemKind.Keyword);
        Assert.Contains(items, item => item.Label == "JOIN" && item.Kind == CompletionItemKind.Keyword);
        Assert.Contains(items, item => item.Label == "LEFT JOIN" && item.Kind == CompletionItemKind.Keyword);
        Assert.Contains(items, item => item.Label == "GROUP BY" && item.Kind == CompletionItemKind.Keyword);
        Assert.Contains(items, item => item.Label == "ORDER BY" && item.Kind == CompletionItemKind.Keyword);
        Assert.Contains(items, item => item.Label == "TABLESAMPLE" && item.Kind == CompletionItemKind.Keyword);
        Assert.Contains(items, item => item.Label == "CROSS VALIDATE" && item.Kind == CompletionItemKind.Keyword);
        Assert.Contains(items, item => item.Label == "LIMIT" && item.Kind == CompletionItemKind.Keyword);
        Assert.Contains(items, item => item.Label == "OFFSET" && item.Kind == CompletionItemKind.Keyword);
    }

    [Fact]
    public void GetCompletions_AfterFromSource_DoesNotOfferTableNames()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT * FROM users ", 20);

        Assert.DoesNotContain(items, item => item.Kind == CompletionItemKind.Table);
        Assert.DoesNotContain(items, item => item.Kind == CompletionItemKind.Column);
    }

    [Fact]
    public void GetCompletions_AfterFromSourceWithPrefix_FiltersByPrefix()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT * FROM users W", 21);

        Assert.Contains(items, item => item.Label == "WHERE");
        Assert.DoesNotContain(items, item => item.Label == "JOIN");
        Assert.DoesNotContain(items, item => item.Label == "GROUP BY");
    }

    // ───────────────────── Clause continuation: JOIN source ─────────────────────

    [Fact]
    public void GetCompletions_AfterJoinSource_OffersOnAndClauseKeywords()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT * FROM a JOIN b ", 23);

        Assert.Contains(items, item => item.Label == "ON" && item.Kind == CompletionItemKind.Keyword);
        Assert.Contains(items, item => item.Label == "WHERE" && item.Kind == CompletionItemKind.Keyword);
        Assert.Contains(items, item => item.Label == "JOIN" && item.Kind == CompletionItemKind.Keyword);
    }

    // ───────────────────── Clause continuation: WHERE ─────────────────────

    [Fact]
    public void GetCompletions_AfterWhereCondition_OffersNextClauses()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT * FROM users WHERE id = 1 ", 33);

        // Expression keywords still present.
        Assert.Contains(items, item => item.Label == "AND");
        Assert.Contains(items, item => item.Label == "OR");
        // Clause continuation keywords now also present.
        Assert.Contains(items, item => item.Label == "GROUP BY");
        Assert.Contains(items, item => item.Label == "ORDER BY");
        Assert.Contains(items, item => item.Label == "CROSS VALIDATE");
    }

    // ───────────────────── Clause continuation: GROUP BY ─────────────────────

    [Fact]
    public void GetCompletions_AfterGroupByColumn_OffersNextClauses()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT * FROM users GROUP BY name ", 34);

        Assert.Contains(items, item => item.Label == "HAVING");
        Assert.Contains(items, item => item.Label == "ORDER BY");
        Assert.Contains(items, item => item.Label == "QUALIFY");
    }

    // ───────────────────── Clause continuation: ORDER BY ─────────────────────

    [Fact]
    public void GetCompletions_AfterOrderByColumn_OffersLimitOffset()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT * FROM users ORDER BY id ", 32);

        Assert.Contains(items, item => item.Label == "LIMIT");
        Assert.Contains(items, item => item.Label == "OFFSET");
        Assert.Contains(items, item => item.Label == "ASC");
        Assert.Contains(items, item => item.Label == "DESC");
    }

    // ───────────────────── CROSS VALIDATE discoverability ─────────────────────

    [Fact]
    public void GetCompletions_CrossValidate_DiscoverableAfterFromSource()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT * FROM users ", 20);

        Assert.Contains(items, item => item.Label == "CROSS VALIDATE");
    }

    [Fact]
    public void GetCompletions_CrossValidate_DiscoverableWithCrossPrefix()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT * FROM users CR", 22);

        Assert.Contains(items, item => item.Label == "CROSS VALIDATE");
        Assert.Contains(items, item => item.Label == "CROSS JOIN");
    }

    // ───────────────────── Type completions ─────────────────────

    [Fact]
    public void GetCompletions_AfterDeclareName_OffersTypeKeywords()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("DECLARE x ", 11);

        // Coverage of the runtime DataKind enum: classic scalars + Array
        // wrapper + extended numerics that came in after the original list.
        Assert.Contains(items, item => item.Label == "Int32" && item.Kind == CompletionItemKind.Keyword);
        Assert.Contains(items, item => item.Label == "String" && item.Kind == CompletionItemKind.Keyword);
        Assert.Contains(items, item => item.Label == "Boolean" && item.Kind == CompletionItemKind.Keyword);
        Assert.Contains(items, item => item.Label == "Float64" && item.Kind == CompletionItemKind.Keyword);
        Assert.Contains(items, item => item.Label == "Array" && item.Kind == CompletionItemKind.Keyword);
        Assert.Contains(items, item => item.Label == "Json" && item.Kind == CompletionItemKind.Keyword);
    }

    [Fact]
    public void GetCompletions_AfterCastAs_OffersTypeKeywords()
    {
        // CAST(x AS |) — previously suppressed, now offers types.
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT CAST(id AS ", 18);

        Assert.Contains(items, item => item.Label == "Int32");
        Assert.Contains(items, item => item.Label == "String");
        Assert.Contains(items, item => item.Label == "Array");
    }

    [Fact]
    public void GetCompletions_AfterReturns_OffersTypeKeywords()
    {
        // CREATE FUNCTION foo() RETURNS | — type position.
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("CREATE FUNCTION sq(x INT32) RETURNS ", 37);

        Assert.Contains(items, item => item.Label == "Int32");
        Assert.Contains(items, item => item.Label == "Float64");
    }

    [Fact]
    public void GetCompletions_AfterFromAlias_StillSuppressesCompletions()
    {
        // Regression: AS used for table aliasing must not start showing
        // type names. The CAST-detection path is paren-scoped.
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT * FROM users AS ", 23);

        Assert.DoesNotContain(items, item => item.Label == "Int32");
    }

    // ───────────────────── models.X completions ─────────────────────

    private static LanguageServerManifest CreateManifestWithModels()
    {
        return new LanguageServerManifest
        {
            Tables = [],
            Functions = [],
            Keywords = ["SELECT", "FROM"],
            Models =
            [
                new ModelEntry
                {
                    Name = "mobilenetv2",
                    OutputKind = "String",
                    Category = "classifier",
                    Backend = "onnx",
                    DisplayName = "MobileNetV2 ImageNet Classifier",
                },
                new ModelEntry
                {
                    Name = "llama31_8b",
                    OutputKind = "String",
                    Category = "llm",
                    Backend = "llama",
                    DisplayName = "Llama 3.1 8B Instruct",
                },
            ],
        };
    }

    [Fact]
    public void GetCompletions_AfterModelsDot_OffersModelNames()
    {
        CompletionProvider provider = new(CreateManifestWithModels());

        CompletionItem[] items = provider.GetCompletions("SELECT models.", 14);

        Assert.Contains(items, item => item.Label == "mobilenetv2" && item.Kind == CompletionItemKind.Function);
        Assert.Contains(items, item => item.Label == "llama31_8b" && item.Kind == CompletionItemKind.Function);
    }

    [Fact]
    public void GetCompletions_AfterModelsDotWithPrefix_FiltersByPrefix()
    {
        CompletionProvider provider = new(CreateManifestWithModels());

        CompletionItem[] items = provider.GetCompletions("SELECT models.mob", 17);

        Assert.Contains(items, item => item.Label == "mobilenetv2");
        Assert.DoesNotContain(items, item => item.Label == "llama31_8b");
    }

    [Fact]
    public void GetCompletions_AfterModelsDot_ItemCarriesCategoryAndOutputKind()
    {
        CompletionProvider provider = new(CreateManifestWithModels());

        CompletionItem[] items = provider.GetCompletions("SELECT models.", 14);

        CompletionItem mobilenet = Assert.Single(items, item => item.Label == "mobilenetv2");
        Assert.Contains("classifier", mobilenet.Detail);
        Assert.Contains("String", mobilenet.Detail);
        // InsertText opens the call so the user just types the args next.
        Assert.Equal("mobilenetv2(", mobilenet.InsertText);
    }

    [Fact]
    public void GetCompletions_AfterModelsDot_CaseInsensitiveQualifier()
    {
        CompletionProvider provider = new(CreateManifestWithModels());

        CompletionItem[] items = provider.GetCompletions("SELECT MODELS.", 14);

        Assert.Contains(items, item => item.Label == "mobilenetv2");
    }

    [Fact]
    public void GetCompletions_AfterModelsDot_NoModelsManifest_ReturnsEmpty()
    {
        // CreateProvider's manifest doesn't include a Models field; the
        // dispatch should noop instead of throwing.
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT models.", 14);

        Assert.Empty(items);
    }

    [Fact]
    public void GetCompletions_AfterModelsDot_DetailIncludesParameterSignature()
    {
        // Models with InputKinds + OptionalArgKinds should surface the full
        // call shape in Detail so the popup acts as a signature hint.
        LanguageServerManifest manifest = new()
        {
            Tables = [],
            Functions = [],
            Keywords = [],
            Models =
            [
                new ModelEntry
                {
                    Name = "llama31_8b",
                    OutputKind = "String",
                    Category = "llm",
                    Parameters =
                    [
                        new ParameterSignature { Name = "input", Kind = "String" },
                        new ParameterSignature { Name = "option", Kind = "Float64", IsOptional = true },
                    ],
                },
            ],
        };
        CompletionProvider provider = new(manifest);

        CompletionItem item = Assert.Single(provider.GetCompletions("SELECT models.", 14));

        Assert.Contains("input: String", item.Detail);
        Assert.Contains("option: Float64?", item.Detail);
        Assert.Contains("→ String", item.Detail);
    }

    // ───────────────────── udf.X completions ─────────────────────

    private static LanguageServerManifest CreateManifestWithUdfs()
    {
        return new LanguageServerManifest
        {
            Tables = [],
            Functions = [],
            Keywords = ["SELECT", "FROM"],
            Udfs =
            [
                new UdfEntry
                {
                    SchemaName = "public",
                    Name = "shout",
                    ReturnType = "STRING",
                    BodyKind = "macro",
                    IsPure = false,
                    Parameters = [new ParameterSignature { Name = "name", Kind = "STRING" }],
                },
                new UdfEntry
                {
                    SchemaName = "public",
                    Name = "RewriteCaption",
                    ReturnType = "STRING",
                    BodyKind = "procedural",
                    IsPure = true,
                    Parameters = [new ParameterSignature { Name = "caption", Kind = "STRING" }],
                },
            ],
        };
    }

    [Fact]
    public void GetCompletions_AfterSchemaDot_OffersUdfsInThatSchema()
    {
        // Post-S7d UDFs live in real schemas (typically `public`); typing
        // `public.` surfaces every UDF registered under that schema.
        CompletionProvider provider = new(CreateManifestWithUdfs());

        CompletionItem[] items = provider.GetCompletions("SELECT public.", 14);

        Assert.Contains(items, item => item.Label == "shout" && item.Kind == CompletionItemKind.Function);
        Assert.Contains(items, item => item.Label == "RewriteCaption" && item.Kind == CompletionItemKind.Function);
    }

    [Fact]
    public void GetCompletions_AfterSchemaDotWithPrefix_FiltersByPrefix()
    {
        CompletionProvider provider = new(CreateManifestWithUdfs());

        CompletionItem[] items = provider.GetCompletions("SELECT public.Re", 16);

        Assert.Contains(items, item => item.Label == "RewriteCaption");
        Assert.DoesNotContain(items, item => item.Label == "shout");
    }

    [Fact]
    public void GetCompletions_AfterSchemaDot_DetailCarriesBodyKindAndPurity()
    {
        // Procedural + pure UDFs should surface both flags in Detail so users
        // see at a glance whether they're invoking an inlined macro or a
        // per-row procedural body, and whether CSE will fold call sites.
        CompletionProvider provider = new(CreateManifestWithUdfs());

        CompletionItem[] items = provider.GetCompletions("SELECT public.", 14);
        CompletionItem rewrite = Assert.Single(items, i => i.Label == "RewriteCaption");
        CompletionItem shout = Assert.Single(items, i => i.Label == "shout");

        Assert.Contains("procedural", rewrite.Detail);
        Assert.Contains("pure", rewrite.Detail);
        Assert.Contains("→ STRING", rewrite.Detail);
        Assert.Contains("macro", shout.Detail);
        Assert.DoesNotContain("pure", shout.Detail);
    }

    [Fact]
    public void GetCompletions_AfterSchemaDot_NoUdfsInSchema_ReturnsEmpty()
    {
        // Schema-qualified completion is filtered by SchemaName — a schema
        // that owns no UDFs returns no items.
        LanguageServerManifest manifest = new()
        {
            Tables = [], Functions = [], Keywords = [], Udfs = [],
        };
        CompletionProvider provider = new(manifest);

        CompletionItem[] items = provider.GetCompletions("SELECT public.", 14);

        Assert.Empty(items);
    }

    // ───────────────────── Function argument surfacing ─────────────────────

    [Fact]
    public void GetCompletions_ScalarFunction_DetailIncludesParameterSignature()
    {
        // Scalar functions with populated Parameters should show their
        // call shape in Detail, the same hint shape models use.
        LanguageServerManifest manifest = new()
        {
            Tables = [],
            Functions =
            [
                new FunctionSignature
                {
                    Name = "upper",
                    Parameters = [new ParameterSignature { Name = "value", Kind = "String" }],
                    ReturnType = "String",
                    Category = FunctionCategory.String,
                },
            ],
            Keywords = ["SELECT"],
        };
        CompletionProvider provider = new(manifest);

        CompletionItem item = Assert.Single(provider.GetCompletions("SELECT ", 7),
            i => i.Label == "upper");

        Assert.Contains("upper(value: String)", item.Detail);
        Assert.Contains("→ String", item.Detail);
    }

    // ───────────────────── In-scope column filtering ─────────────────────

    [Fact]
    public void GetCompletions_InsideFunctionCall_NoFrom_DoesNotOfferColumns()
    {
        // Reported bug: `SELECT array_length()` with cursor in the parens
        // dumped every column from every table in the catalog into the
        // popup (anova_f, backend, body, …). Expected: no FROM means no
        // columns — only functions/keywords/variables apply.
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT array_length()", 20);

        Assert.DoesNotContain(items, item => item.Kind == CompletionItemKind.Column);
    }

    [Fact]
    public void GetCompletions_InsideFunctionCall_WithFrom_OffersOnlyInScopeColumns()
    {
        // The same shape with a FROM should still offer columns — but only
        // from tables that are in scope. The provider's manifest has two
        // tables (users, orders); a query reading users should not surface
        // orders' columns inside the inner function call.
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions(
            "SELECT array_length() FROM users", 20);

        Assert.Contains(items, item => item.Label == "id" && item.Kind == CompletionItemKind.Column);
        Assert.Contains(items, item => item.Label == "name" && item.Kind == CompletionItemKind.Column);
        Assert.DoesNotContain(items,
            item => item.Label == "order_id" && item.Kind == CompletionItemKind.Column);
        Assert.DoesNotContain(items,
            item => item.Label == "total" && item.Kind == CompletionItemKind.Column);
    }

    [Fact]
    public void GetCompletions_AfterSelect_NoFrom_DoesNotOfferColumns()
    {
        // Same scoping rule for the AfterSelect zone — listing every column
        // before any FROM is parsed is just noise.
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT ", 7);

        Assert.DoesNotContain(items, item => item.Kind == CompletionItemKind.Column);
    }

    [Fact]
    public void GetCompletions_ScalarFunction_VariadicRendersWithEllipsis()
    {
        // The builder lowers VariadicTrailing into a "...name" optional
        // parameter; the surfaced Detail should show the ellipsis form.
        LanguageServerManifest manifest = new()
        {
            Tables = [],
            Functions =
            [
                new FunctionSignature
                {
                    Name = "concat",
                    Parameters =
                    [
                        new ParameterSignature { Name = "...elements", Kind = "Any", IsOptional = true },
                    ],
                    ReturnType = "String",
                    Category = FunctionCategory.String,
                },
            ],
            Keywords = [],
        };
        CompletionProvider provider = new(manifest);

        CompletionItem item = Assert.Single(provider.GetCompletions("SELECT ", 7),
            i => i.Label == "concat");

        Assert.Contains("...elements: Any?", item.Detail);
    }
}
