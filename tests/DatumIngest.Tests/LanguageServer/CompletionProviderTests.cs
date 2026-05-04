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
    public void GetCompletions_InsideTemplateSplice_OffersOuterFromColumns()
    {
        // Cursor lands inside `${…}` of a template string. Pre-fix the
        // existing splice-aware classifier returned an Expression zone
        // but with empty TablesInScope (because the recursion classified
        // only the splice text), so column completions were missing.
        // Now the outer FROM scope is re-extracted and overlaid onto the
        // splice's classified zone — `users`'s columns surface.
        //
        // The splice is closed (editor auto-insert of `}` is typical) so
        // the full-SQL tokenization succeeds and ExtractTablesInScope
        // sees `users`. Cursor sits between `na` and the `}`.
        CompletionProvider provider = CreateProvider();

        const string sql = "SELECT `User: ${na}` FROM users";
        int cursor = sql.IndexOf("${na", System.StringComparison.Ordinal) + "${na".Length;
        CompletionItem[] items = provider.GetCompletions(sql, cursor);

        Assert.Contains(items, item =>
            item.Label == "name" && item.Kind == CompletionItemKind.Column);
    }

    [Fact]
    public void GetCompletions_InsideUnterminatedSplice_StillOffersOuterFromColumns()
    {
        // Regression for the repair pass: user is mid-typing inside a
        // `${…}` splice with no closing `}` (auto-close disabled or
        // mid-paste). Pre-repair the whole-SQL tokenization failed on
        // the unterminated template, so the outer-scope overlay returned
        // empty TablesInScope and no columns surfaced. TokenizeRepair
        // appends `}` + `` ` `` so the tokenizer succeeds; column
        // completions now work. Diagnostics still flag the unterminated
        // template independently because they run on the raw input.
        CompletionProvider provider = CreateProvider();

        const string sql = "SELECT `User: ${na FROM users";
        int cursor = sql.IndexOf("${na", System.StringComparison.Ordinal) + "${na".Length;
        CompletionItem[] items = provider.GetCompletions(sql, cursor);

        Assert.Contains(items, item =>
            item.Label == "name" && item.Kind == CompletionItemKind.Column);
    }

    [Fact]
    public void GetCompletions_InsideTemplateSplice_OffersScalarFunctions()
    {
        // Splice content is an expression — scalar functions should
        // surface alongside columns. Same closed-splice shape as the
        // previous test.
        CompletionProvider provider = CreateProvider();

        const string sql = "SELECT `Value: ${ab}` FROM users";
        int cursor = sql.IndexOf("${ab", System.StringComparison.Ordinal) + "${ab".Length;
        CompletionItem[] items = provider.GetCompletions(sql, cursor);

        Assert.Contains(items, item =>
            item.Label == "abs" && item.Kind == CompletionItemKind.Function);
    }

    [Fact]
    public void GetCompletions_AfterUpdateSet_OnlyOffersTargetTableColumns()
    {
        // Regression: `UPDATE conversations SET |` was passing
        // tablesInScope=null to AddColumns, which surfaced columns from
        // every catalog table. The UPDATE target IS in zone.TablesInScope
        // via the DML-target detection — feed it through.
        CompletionProvider provider = CreateProvider();

        const string sql = "UPDATE users SET ";
        CompletionItem[] items = provider.GetCompletions(sql, sql.Length);

        // `users` columns surface.
        Assert.Contains(items, item =>
            item.Label == "name" && item.Kind == CompletionItemKind.Column);
        Assert.Contains(items, item =>
            item.Label == "email" && item.Kind == CompletionItemKind.Column);
        // `orders` columns must NOT surface — different table.
        Assert.DoesNotContain(items, item =>
            item.Label == "order_id" && item.Kind == CompletionItemKind.Column);
        Assert.DoesNotContain(items, item =>
            item.Label == "total" && item.Kind == CompletionItemKind.Column);
    }

    [Fact]
    public void GetCompletions_AfterInsertTable_OnlyOffersTargetTableColumns()
    {
        // Symmetric to the UPDATE SET case: `INSERT INTO users (|` should
        // suggest columns from `users`, not every table.
        CompletionProvider provider = CreateProvider();

        const string sql = "INSERT INTO users (";
        CompletionItem[] items = provider.GetCompletions(sql, sql.Length);

        Assert.Contains(items, item =>
            item.Label == "name" && item.Kind == CompletionItemKind.Column);
        Assert.DoesNotContain(items, item =>
            item.Label == "order_id" && item.Kind == CompletionItemKind.Column);
    }

    [Fact]
    public void GetCompletions_AfterWhere_IncludesLetBindingFromSameSelect()
    {
        // Regression for item-7: a LET binding declared earlier in the
        // same SELECT must surface in any expression-context completion
        // zone the way columns do. Cursor lands inside the WHERE clause
        // of a SELECT that defines `LET my_calc = id + 1`.
        CompletionProvider provider = CreateProvider();

        const string sql = "SELECT LET my_calc = id + 1, id FROM users WHERE ";
        CompletionItem[] items = provider.GetCompletions(sql, sql.Length);

        CompletionItem? hit = System.Linq.Enumerable.FirstOrDefault(
            items, item => item.Label == "my_calc");
        Assert.NotNull(hit);
        Assert.Equal(CompletionItemKind.Variable, hit.Kind);
        Assert.Contains("LET binding", hit.Detail ?? "");
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

    // ───────────────────── CTE AS-position keywords ─────────────────────

    [Fact]
    public void GetCompletions_AfterCteAs_OffersMaterializationHints()
    {
        CompletionProvider provider = CreateProvider();

        const string sql = "WITH foo AS  (SELECT 1)";
        // Cursor in the gap after `AS `.
        int offset = sql.IndexOf("AS ", StringComparison.Ordinal) + "AS ".Length;
        CompletionItem[] items = provider.GetCompletions(sql, offset);

        Assert.Contains(items, item => item.Label == "MATERIALIZED" && item.Kind == CompletionItemKind.Keyword);
        Assert.Contains(items, item => item.Label == "NOT MATERIALIZED" && item.Kind == CompletionItemKind.Keyword);
    }

    [Fact]
    public void GetCompletions_AfterCteAs_WithColumnList_StillOffersHints()
    {
        CompletionProvider provider = CreateProvider();

        // `WITH foo (a, b) AS |` — the column-list parens between the CTE
        // name and AS must not throw off the classifier.
        const string sql = "WITH foo (a, b) AS  (SELECT 1, 2)";
        int offset = sql.IndexOf("AS ", StringComparison.Ordinal) + "AS ".Length;
        CompletionItem[] items = provider.GetCompletions(sql, offset);

        Assert.Contains(items, item => item.Label == "MATERIALIZED");
        Assert.Contains(items, item => item.Label == "NOT MATERIALIZED");
    }

    [Fact]
    public void GetCompletions_AfterChainedCteAs_OffersMaterializationHints()
    {
        CompletionProvider provider = CreateProvider();

        // Second CTE in a chain — `WITH a AS (...), b AS |`. Detection
        // must walk back past the comma and confirm WITH precedes it.
        const string sql = "WITH a AS (SELECT 1), b AS  (SELECT 2)";
        // Use LastIndexOf to land on the second AS, not the first.
        int offset = sql.LastIndexOf("AS ", StringComparison.Ordinal) + "AS ".Length;
        CompletionItem[] items = provider.GetCompletions(sql, offset);

        Assert.Contains(items, item => item.Label == "MATERIALIZED");
    }

    [Fact]
    public void GetCompletions_AliasingAs_OutsideCte_DoesNotOfferMaterializationHints()
    {
        CompletionProvider provider = CreateProvider();

        // Regular `SELECT col AS alias` position — must NOT offer
        // MATERIALIZED. The plain alias-typing AfterAs zone returns no
        // keywords; this guards against an overzealous CTE detection.
        const string sql = "SELECT name AS  FROM users";
        int offset = sql.IndexOf("AS ", StringComparison.Ordinal) + "AS ".Length;
        CompletionItem[] items = provider.GetCompletions(sql, offset);

        Assert.DoesNotContain(items, item => item.Label == "MATERIALIZED");
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

    [Fact]
    public void GetCompletions_AfterTableAliasDot_ResolvesToUnderlyingTableColumns()
    {
        // `FROM users u` binds `u` as an alias for `users`. Typing `u.` in
        // the SELECT list must surface users' columns — without alias
        // resolution the qualifier was looked up directly in the manifest,
        // found nothing, and left the popup empty.
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT u. FROM users u", 9);

        Assert.Contains(items, item => item.Label == "id");
        Assert.Contains(items, item => item.Label == "name");
        Assert.Contains(items, item => item.Label == "email");
        Assert.DoesNotContain(items, item => item.Label == "order_id");
    }

    [Fact]
    public void GetCompletions_AfterSchemaDot_OffersTablesInThatSchema()
    {
        // Typing `FROM information_schema.` should surface the tables
        // registered under that schema, not just routines or columns.
        LanguageServerManifest manifest = new()
        {
            Tables =
            [
                new TableSchemaEntry
                {
                    Name = "information_schema.tables",
                    Columns = [new TableColumnEntry { Name = "table_name", Kind = "String", Nullable = false }],
                },
                new TableSchemaEntry
                {
                    Name = "information_schema.columns",
                    Columns = [new TableColumnEntry { Name = "column_name", Kind = "String", Nullable = false }],
                },
                new TableSchemaEntry
                {
                    Name = "public.users",
                    Columns = [new TableColumnEntry { Name = "id", Kind = "Float32", Nullable = false }],
                },
            ],
            Functions = [],
            Keywords = ["SELECT", "FROM"],
        };
        CompletionProvider provider = new(manifest);

        CompletionItem[] items = provider.GetCompletions("SELECT * FROM information_schema.", 33);

        Assert.Contains(items, item => item.Label == "tables" && item.Kind == CompletionItemKind.Table);
        Assert.Contains(items, item => item.Label == "columns" && item.Kind == CompletionItemKind.Table);
        // Tables from other schemas must not bleed in.
        Assert.DoesNotContain(items, item => item.Label == "users");
    }

    [Fact]
    public void GetCompletions_AfterTableAliasDot_HonorsExplicitAsKeyword()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT u. FROM users AS u", 9);

        Assert.Contains(items, item => item.Label == "id");
        Assert.Contains(items, item => item.Label == "name");
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
    public void GetCompletions_SchemaQualifiedTable_InsertsUnquotedDottedName()
    {
        // Manifest entries are schema-qualified — the dot separates schema
        // from table, so the inserted text must keep the dot bare. Wrapping
        // the whole thing in quotes (the old behavior) produced
        // "schema.table" which the parser reads as one quoted identifier
        // and fails to resolve at execution time.
        LanguageServerManifest manifest = new()
        {
            Tables =
            [
                new TableSchemaEntry
                {
                    Name = "public.users",
                    Columns = [new TableColumnEntry { Name = "id", Kind = "Float32", Nullable = false }],
                },
            ],
            Functions = [],
            Keywords = ["SELECT", "FROM"],
        };
        CompletionProvider provider = new(manifest);

        CompletionItem[] items = provider.GetCompletions("SELECT * FROM ", 14);

        CompletionItem? tableItem = Array.Find(items, item => item.Label == "public.users");
        Assert.NotNull(tableItem);
        Assert.Null(tableItem.InsertText);
    }

    [Fact]
    public void GetCompletions_SchemaQualifiedTableWithReservedSegment_QuotesOnlyThatSegment()
    {
        // When a segment is a reserved keyword (here `order`), quote only
        // that segment — not the whole dotted path.
        LanguageServerManifest manifest = new()
        {
            Tables =
            [
                new TableSchemaEntry
                {
                    Name = "public.order",
                    Columns = [new TableColumnEntry { Name = "id", Kind = "Float32", Nullable = false }],
                },
            ],
            Functions = [],
            Keywords = ["SELECT", "FROM"],
        };
        CompletionProvider provider = new(manifest);

        CompletionItem[] items = provider.GetCompletions("SELECT * FROM ", 14);

        CompletionItem? tableItem = Array.Find(items, item => item.Label == "public.order");
        Assert.NotNull(tableItem);
        Assert.Equal("public.\"order\"", tableItem.InsertText);
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

        // `users` is in the test manifest; the column-scoping pass now
        // restricts results to the UPDATE target, so this exercises the
        // augmented-tables-in-scope path (UPDATE x feeds the same lookup
        // FROM x would).
        CompletionItem[] items = provider.GetCompletions("UPDATE users SET ", 17);

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

    // ───────────────────── Lambda-context filtering ─────────────────────

    /// <summary>
    /// Manifest that mixes a globally-visible scalar (`color`) with a
    /// context-restricted curve (`oscillate`, tagged `animation`) and a
    /// higher-order function (`animate_frames`) whose last parameter is
    /// a `Lambda<animation, Drawing>`. Mirrors the runtime shape just
    /// closely enough to exercise the context filter.
    /// </summary>
    private static LanguageServerManifest CreateLambdaContextManifest()
    {
        return new LanguageServerManifest
        {
            Tables = [],
            Keywords = ["SELECT"],
            SearchPath = ["public", "system"],
            Functions =
            [
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "color",
                    Parameters =
                    [
                        new ParameterSignature { Name = "r", Kind = "Int32" },
                        new ParameterSignature { Name = "g", Kind = "Int32" },
                        new ParameterSignature { Name = "b", Kind = "Int32" },
                    ],
                    ReturnType = "Color",
                    Category = FunctionCategory.Drawing,
                },
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "oscillate",
                    Parameters =
                    [
                        new ParameterSignature { Name = "t", Kind = "Float32" },
                        new ParameterSignature { Name = "low", Kind = "Float32" },
                        new ParameterSignature { Name = "high", Kind = "Float32" },
                    ],
                    ReturnType = "Float32",
                    Category = FunctionCategory.Drawing,
                    Contexts = ["animation"],
                },
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "animate_frames",
                    Parameters =
                    [
                        new ParameterSignature { Name = "duration", Kind = "Float32" },
                        new ParameterSignature { Name = "fps", Kind = "Int32" },
                        new ParameterSignature { Name = "size", Kind = "Point2D" },
                        new ParameterSignature { Name = "render_frame", Kind = "Lambda<animation, returns: Drawing>", LambdaContextName = "animation" },
                    ],
                    ReturnType = "Array<Image>",
                    Category = FunctionCategory.Drawing,
                },
            ],
            FunctionContexts =
            [
                new FunctionContextEntry { Name = "pure", Parameters = [], ParentName = null, Borrows = [] },
                new FunctionContextEntry
                {
                    Name = "animation",
                    Parameters = [new LambdaParameterEntry { Name = "t", Kind = "Float32" }],
                    ParentName = "pure",
                    Borrows = [],
                },
            ],
        };
    }

    [Fact]
    public void OutsideLambda_ContextRestrictedFunctions_NotSuggested()
    {
        CompletionProvider provider = new(CreateLambdaContextManifest());

        // Cursor in a plain SELECT expression — `oscillate` is animation-context-
        // restricted and would never resolve here. Should NOT appear.
        CompletionItem[] items = provider.GetCompletions("SELECT ", 7);

        Assert.Contains(items, i => i.Label == "color");          // globally visible
        Assert.Contains(items, i => i.Label == "animate_frames"); // globally visible
        Assert.DoesNotContain(items, i => i.Label == "oscillate"); // context-restricted
    }

    [Fact]
    public void InsideAnimationLambda_AnimationFunctions_AreSuggested()
    {
        CompletionProvider provider = new(CreateLambdaContextManifest());

        // Cursor right after the arrow, inside the animate_frames lambda body.
        // `oscillate` (tagged `animation`) should now be in scope; `color`
        // (global) also stays.
        const string sql = "SELECT animate_frames(1.0, 24, point2d(64, 64), (t) -> )";
        int cursor = sql.IndexOf("-> ") + 3;
        CompletionItem[] items = provider.GetCompletions(sql, cursor);

        Assert.Contains(items, i => i.Label == "oscillate"); // now visible
        Assert.Contains(items, i => i.Label == "color");     // globally visible
    }

    [Fact]
    public void InsideLambda_NestedInsideArrayLiteral_StillResolvesContext()
    {
        // The lambda body extends through nested `(...)` AND `[...]`. Cursor
        // inside `draw_group([...])` should still see the animation-context
        // whitelist — `oscillate` must show up.
        CompletionProvider provider = new(CreateLambdaContextManifest());

        const string sql = "SELECT animate_frames(1.0, 24, point2d(64, 64), (t) -> draw_group([]))";
        int cursor = sql.IndexOf("[]") + 1; // between [ and ]
        CompletionItem[] items = provider.GetCompletions(sql, cursor);

        Assert.Contains(items, i => i.Label == "oscillate");
    }

    [Fact]
    public void InsideLambda_AfterCommaInsideArrayLiteral_StillResolvesContext()
    {
        // Cursor placed after a comma inside the array literal — matching
        // the user's flame example where they'd type a new entry mid-array.
        // The lambda scope must persist past the comma.
        CompletionProvider provider = new(CreateLambdaContextManifest());

        const string sql =
            "SELECT animate_frames(1.0, 24, point2d(64, 64), (t) -> draw_group([color(1, 2, 3), ]))";
        // Cursor between `, ` and `]` — right where a new element would be typed.
        int cursor = sql.IndexOf(", ]") + 2;
        CompletionItem[] items = provider.GetCompletions(sql, cursor);

        Assert.Contains(items, i => i.Label == "oscillate");
    }

    [Fact]
    public void InsideLambda_LambdaParameter_IsSuggestedInBody()
    {
        // The lambda parameter `t` should be suggested by name inside the
        // lambda body — without this, the user can't tell which name was
        // assigned (they might have renamed `t -> u -> v`) and the
        // completion popup is missing a key piece of context.
        CompletionProvider provider = new(CreateLambdaContextManifest());

        const string sql = "SELECT animate_frames(1.0, 24, point2d(64, 64), (t) -> )";
        int cursor = sql.IndexOf("-> ") + 3;
        CompletionItem[] items = provider.GetCompletions(sql, cursor);

        Assert.Contains(items, i => i.Label == "t");
    }

    [Fact]
    public void InsideStringLiteral_OfEnumParameter_SuggestsEnumValues()
    {
        // Manifest with a `blend(content, mode)` function whose `mode`
        // parameter carries an EnumValues list. Cursor placed inside the
        // string literal should surface the enum values, not the usual
        // function/keyword set.
        LanguageServerManifest manifest = new()
        {
            Tables = [],
            Keywords = ["SELECT"],
            SearchPath = ["public", "system"],
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
                            Kind = "String (add | multiply | screen)",
                            EnumValues = ["add", "multiply", "screen"],
                        },
                    ],
                    ReturnType = "Drawing",
                    Category = FunctionCategory.Drawing,
                },
            ],
        };
        CompletionProvider provider = new(manifest);

        const string sql = "SELECT blend(my_drawing, '')";
        int cursor = sql.IndexOf("''") + 1; // between the two quotes
        CompletionItem[] items = provider.GetCompletions(sql, cursor);

        Assert.Contains(items, i => i.Label == "add" && i.Kind == CompletionItemKind.EnumMember);
        Assert.Contains(items, i => i.Label == "multiply" && i.Kind == CompletionItemKind.EnumMember);
        Assert.Contains(items, i => i.Label == "screen" && i.Kind == CompletionItemKind.EnumMember);
        // Plain-function items (e.g. animate_frames from the lambda manifest)
        // shouldn't leak into the string-position popup.
        Assert.DoesNotContain(items, i => i.Kind == CompletionItemKind.Function);
    }

    [Fact]
    public void InsideStringLiteral_OfNonEnumParameter_StaysEmpty()
    {
        // Manifest's blend function exists but the `mode` param has no
        // EnumValues; cursor inside the string should produce NO completions
        // (current behaviour for any string position without an enum hint).
        LanguageServerManifest manifest = new()
        {
            Tables = [],
            Keywords = ["SELECT"],
            SearchPath = ["public", "system"],
            Functions =
            [
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "blend",
                    Parameters =
                    [
                        new ParameterSignature { Name = "content", Kind = "Drawing" },
                        new ParameterSignature { Name = "mode", Kind = "String" }, // no EnumValues
                    ],
                    ReturnType = "Drawing",
                    Category = FunctionCategory.Drawing,
                },
            ],
        };
        CompletionProvider provider = new(manifest);

        const string sql = "SELECT blend(my_drawing, '')";
        int cursor = sql.IndexOf("''") + 1;
        CompletionItem[] items = provider.GetCompletions(sql, cursor);

        Assert.Empty(items);
    }

    [Fact]
    public void InsideStringLiteral_NoFunctionSuggestions()
    {
        // Cursor inside a string literal — providers shouldn't surface
        // functions / keywords. SQL parsers don't tokenize identifiers
        // inside strings; the completion popup shouldn't either.
        CompletionProvider provider = new(CreateLambdaContextManifest());

        // Cursor placed at the middle of 'abc' string.
        const string sql = "SELECT 'abc'";
        int cursor = sql.IndexOf('a') + 1; // between a and b
        CompletionItem[] items = provider.GetCompletions(sql, cursor);

        // Functions like `color` should NOT appear when typing inside a string.
        Assert.DoesNotContain(items, i => i.Label == "color");
        Assert.DoesNotContain(items, i => i.Label == "animate_frames");
    }
}
