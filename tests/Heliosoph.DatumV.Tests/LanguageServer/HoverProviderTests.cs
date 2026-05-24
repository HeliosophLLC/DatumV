namespace Heliosoph.DatumV.Tests.LanguageServer;

using Heliosoph.DatumV.LanguageServer;
using Heliosoph.DatumV.Manifest;

/// <summary>
/// Tests for <see cref="HoverProvider"/> — hover information for SQL tokens.
/// </summary>
public sealed class HoverProviderTests : ServiceTestBase
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
                        new TableColumnEntry { Name = "name", Kind = "String", Nullable = true },
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
            Keywords = ["SELECT", "FROM", "WHERE"],
        };
    }

    private static HoverProvider CreateProvider()
    {
        return new HoverProvider(CreateTestManifest());
    }

    // ───────────────────── Null / empty ─────────────────────

    [Fact]
    public void GetHover_EmptyString_ReturnsNull()
    {
        HoverProvider provider = CreateProvider();

        HoverResult? result = provider.GetHover("", 0);

        Assert.Null(result);
    }

    [Fact]
    public void GetHover_CursorOutOfRange_ReturnsNull()
    {
        HoverProvider provider = CreateProvider();

        HoverResult? result = provider.GetHover("SELECT", 99);

        Assert.Null(result);
    }

    // ───────────────────── Keyword hover ─────────────────────

    [Fact]
    public void GetHover_SelectKeyword_ReturnsKeywordDocumentation()
    {
        HoverProvider provider = CreateProvider();

        HoverResult? result = provider.GetHover("SELECT x FROM t", 0);

        Assert.NotNull(result);
        Assert.Contains("SELECT", result.Contents);
    }

    [Fact]
    public void GetHover_WhereKeyword_ReturnsKeywordDocumentation()
    {
        HoverProvider provider = CreateProvider();

        HoverResult? result = provider.GetHover("SELECT x FROM t WHERE x = 1", 16);

        Assert.NotNull(result);
        Assert.Contains("WHERE", result.Contents);
    }

    // ───────────────────── Function hover ─────────────────────

    [Fact]
    public void GetHover_FunctionName_ReturnsSignature()
    {
        HoverProvider provider = CreateProvider();

        // "abs(" — cursor on "abs" at offset 7
        HoverResult? result = provider.GetHover("SELECT abs(x) FROM t", 7);

        Assert.NotNull(result);
        Assert.Contains("abs", result.Contents);
        Assert.Contains("Absolute value", result.Contents);
    }

    [Fact]
    public void GetHover_FunctionName_OnSecondLine_ReturnsSignature()
    {
        // Regression: previously FindTokenAtOffset compared the cursor's
        // absolute document offset against the token's column-within-line,
        // so any token below the first newline silently missed.
        HoverProvider provider = CreateProvider();

        // Layout: "SELECT\n  abs(x) FROM t" — `abs` starts at offset 9
        // (6 chars + '\n' + 2 spaces).
        const string sql = "SELECT\n  abs(x) FROM t";
        HoverResult? result = provider.GetHover(sql, 9);

        Assert.NotNull(result);
        Assert.Contains("abs", result.Contents);
        Assert.Contains("Absolute value", result.Contents);
    }

    // ───────────────────── Table hover ─────────────────────

    [Fact]
    public void GetHover_TableName_ReturnsColumnList()
    {
        HoverProvider provider = CreateProvider();

        // "users" starts at offset 14
        HoverResult? result = provider.GetHover("SELECT * FROM users", 14);

        Assert.NotNull(result);
        Assert.Contains("users", result.Contents);
        Assert.Contains("id", result.Contents);
        Assert.Contains("name", result.Contents);
        Assert.Contains("Table:", result.Contents);
    }

    [Fact]
    public void GetHover_ViewName_RendersViewLabel()
    {
        // A manifest entry with Kind = "VIEW" should hover with "View:"
        // rather than "Table:" so the editor surface matches the catalog
        // semantics. Borrows the default manifest scaffold so name
        // extraction and keyword tables aren't a confound.
        LanguageServerManifest manifest = new()
        {
            Tables =
            [
                new TableSchemaEntry
                {
                    Name = "users",
                    Kind = "VIEW",
                    Columns =
                    [
                        new TableColumnEntry { Name = "id", Kind = "Float32", Nullable = false },
                        new TableColumnEntry { Name = "name", Kind = "String", Nullable = true },
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
            ],
            Keywords = ["SELECT", "FROM", "WHERE"],
        };
        HoverProvider provider = new(manifest);

        // Same SQL + offset shape as GetHover_TableName_ReturnsColumnList —
        // only difference is Kind on the manifest entry — so we isolate
        // the label rendering from any name-extraction confound.
        HoverResult? result = provider.GetHover("SELECT * FROM users", 14);

        Assert.NotNull(result);
        Assert.Contains("View: users", result.Contents);
        Assert.DoesNotContain("Table:", result.Contents);
    }

    // ───────────────────── Column hover ─────────────────────

    [Fact]
    public void GetHover_ColumnName_ReturnsTypeInfo()
    {
        HoverProvider provider = CreateProvider();

        // "id" at offset 7—8 in "SELECT id FROM users"
        HoverResult? result = provider.GetHover("SELECT id FROM users", 7);

        Assert.NotNull(result);
        Assert.Contains("id", result.Contents);
        Assert.Contains("Float32", result.Contents);
    }

    // ───────────────────── TVF output column hover ─────────────────────

    [Fact]
    public void GetHover_UnqualifiedTvfOutputColumn_ReturnsTypeInfo()
    {
        HoverProvider provider = CreateProvider();

        const string sql = "SELECT frame_index FROM video_unnest_frames('x.mp4') vid";
        // "frame_index" starts at offset 7.
        HoverResult? result = provider.GetHover(sql, 7);

        Assert.NotNull(result);
        Assert.Contains("frame_index", result.Contents);
        Assert.Contains("Int32", result.Contents);
        Assert.Contains("video_unnest_frames", result.Contents);
    }

    [Fact]
    public void GetHover_QualifiedTvfOutputColumn_ResolvesThroughAlias()
    {
        HoverProvider provider = CreateProvider();

        const string sql = "SELECT vid.frame FROM video_unnest_frames('x.mp4') vid";
        // "frame" inside "vid.frame" starts at offset 11.
        HoverResult? result = provider.GetHover(sql, 11);

        Assert.NotNull(result);
        Assert.Contains("frame", result.Contents);
        Assert.Contains("VideoFrame", result.Contents);
    }

    [Fact]
    public void GetHover_BareTableAlias_ResolvesToUnderlyingTable()
    {
        // Hover on `u` (alias for `users` from `FROM users u`) must surface
        // the users table card. Previously the manifest lookup found
        // nothing named `u` and produced no hover at all.
        HoverProvider provider = CreateProvider();

        // Hover on the trailing alias `u` of `FROM users u`.
        const string sql = "SELECT u.id FROM users u";
        int offset = sql.LastIndexOf('u');

        HoverResult? result = provider.GetHover(sql, offset);

        Assert.NotNull(result);
        Assert.Contains("Table: users", result.Contents);
        Assert.Contains("id", result.Contents);
        Assert.Contains("name", result.Contents);
    }

    [Fact]
    public void GetHover_BareTableAlias_InSecondStatementOfBatch_ResolvesToUnderlyingTable()
    {
        // Multi-statement batch — alias-collection used to stop at the
        // first QueryStatement (EffectiveQuery only returns one), so the
        // second statement's alias was invisible to hover.
        HoverProvider provider = CreateProvider();

        const string sql = "SELECT id FROM users; SELECT u.id FROM users u";
        int offset = sql.LastIndexOf('u');

        HoverResult? result = provider.GetHover(sql, offset);

        Assert.NotNull(result);
        Assert.Contains("Table: users", result.Contents);
    }

    [Fact]
    public void GetHover_QualifiedColumnThroughTableAlias_ResolvesToTableColumn()
    {
        // `u.id` where `u` is an alias for `users` — column hover should
        // resolve through the alias to surface the users.id column entry.
        HoverProvider provider = CreateProvider();

        const string sql = "SELECT u.id FROM users u";
        // "id" inside "u.id" starts at offset 9.
        int offset = sql.IndexOf("u.id", StringComparison.Ordinal) + 2;

        HoverResult? result = provider.GetHover(sql, offset);

        Assert.NotNull(result);
        Assert.Contains("id", result.Contents);
        Assert.Contains("Float32", result.Contents);
    }

    [Fact]
    public void GetHover_TvfOutputColumn_VisibleAcrossCteBoundary()
    {
        HoverProvider provider = CreateProvider();

        // The TVF source is defined in a CTE; the hover target sits in the
        // outer SELECT. Validates that the alias map is collected
        // statement-wide (CTEs included), not just from the immediate FROM.
        const string sql =
            "WITH frames AS (SELECT frame_index, frame FROM video_unnest_frames('x.mp4') vid) " +
            "SELECT frame_index FROM frames";
        int offset = sql.IndexOf("SELECT frame_index FROM frames", StringComparison.Ordinal)
            + "SELECT ".Length;

        HoverResult? result = provider.GetHover(sql, offset);

        Assert.NotNull(result);
        Assert.Contains("frame_index", result.Contents);
        Assert.Contains("Int32", result.Contents);
    }

    // ───────────────────── CTE projection hover ─────────────────────

    [Fact]
    public void GetHover_QualifiedCteColumn_ResolvesThroughCteSchema()
    {
        HoverProvider provider = CreateProvider();

        const string sql =
            "WITH frames AS (SELECT frame_index, frame FROM video_unnest_frames('x.mp4') vid) " +
            "SELECT f1.frame_index FROM frames f1";
        int offset = sql.LastIndexOf("f1.frame_index", StringComparison.Ordinal)
            + "f1.".Length;

        HoverResult? result = provider.GetHover(sql, offset);

        Assert.NotNull(result);
        Assert.Contains("frame_index", result.Contents);
        Assert.Contains("Int32", result.Contents);
        Assert.Contains("CTE", result.Contents);
    }

    [Fact]
    public void GetHover_UnqualifiedCteColumn_ResolvesThroughCteSchema()
    {
        HoverProvider provider = CreateProvider();

        // Cursor on the unqualified `frame_index` in the outer SELECT,
        // referencing the projected column from the `frames` CTE.
        const string sql =
            "WITH frames AS (SELECT frame_index FROM video_unnest_frames('x.mp4') vid) " +
            "SELECT frame_index FROM frames";
        int offset = sql.LastIndexOf("SELECT frame_index FROM frames", StringComparison.Ordinal)
            + "SELECT ".Length;

        HoverResult? result = provider.GetHover(sql, offset);

        Assert.NotNull(result);
        Assert.Contains("frame_index", result.Contents);
        Assert.Contains("Int32", result.Contents);
    }

    [Fact]
    public void GetHover_RenamedCteColumn_SurfacesAliasedName()
    {
        HoverProvider provider = CreateProvider();

        // `prev_curr` renames `frames.frame` to `prev`. Hovering on `prev`
        // through the outer FROM should resolve to VideoFrame (the
        // renamed column's underlying kind).
        const string sql =
            "WITH frames AS (SELECT frame_index, frame FROM video_unnest_frames('x.mp4') vid)," +
            "prev_curr AS (SELECT f1.frame AS prev FROM frames f1) " +
            "SELECT prev FROM prev_curr";
        int offset = sql.LastIndexOf("SELECT prev FROM prev_curr", StringComparison.Ordinal)
            + "SELECT ".Length;

        HoverResult? result = provider.GetHover(sql, offset);

        Assert.NotNull(result);
        Assert.Contains("prev", result.Contents);
        Assert.Contains("VideoFrame", result.Contents);
    }

    // ───────────────────── LET binding hover ─────────────────────

    [Fact]
    public void GetHover_UnprojectedLetBinding_ResolvesThroughExpressionKind()
    {
        // `LET curr_depth = models.X(...)` declared inside a CTE but never
        // surfaced through the CTE's SELECT list. The LET map carries
        // every binding's resolved kind separately so hover still finds it.
        LanguageServerManifest manifest = new()
        {
            Tables = [],
            Functions =
            [
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "video_unnest_frames",
                    Parameters = [new ParameterSignature { Name = "source", Kind = "String" }],
                    ReturnType = "table(frame_index Int32, frame VideoFrame)",
                    IsTableValued = true,
                    OutputColumns =
                    [
                        new TableColumnEntry { Name = "frame_index", Kind = "Int32", Nullable = false },
                        new TableColumnEntry { Name = "frame", Kind = "VideoFrame", Nullable = false },
                    ],
                },
                new FunctionSignature
                {
                    SchemaName = "models",
                    Name = "depth_full",
                    Parameters = [new ParameterSignature { Name = "img", Kind = "Image" }],
                    ReturnType = "Struct<depth: Array<Float32>, intrinsics: Array<Float32>>",
                },
            ],
            Keywords = [],
        };
        HoverProvider provider = new(manifest);

        const string sql =
            "WITH thumb AS (SELECT " +
            "  LET curr_depth = models.depth_full(frame), " +
            "  frame_index " +
            "FROM video_unnest_frames('x.mp4') vid) " +
            "SELECT frame_index FROM thumb";

        // Hover on `curr_depth` inside the LET binding.
        int offset = sql.IndexOf("curr_depth", StringComparison.Ordinal);
        HoverResult? result = provider.GetHover(sql, offset);

        Assert.NotNull(result);
        Assert.Contains("curr_depth", result.Contents);
        Assert.Contains("Struct<depth: Array<Float32>, intrinsics: Array<Float32>>", result.Contents);
        Assert.Contains("LET binding", result.Contents);
    }

    [Fact]
    public void GetHover_LetBindingInCte_AfterLeadingDeclare_StillResolves()
    {
        // Regression: when a DECLARE statement precedes the WITH clause,
        // ParseResult.Query is null (Query is reserved for single-query
        // inputs; multi-statement batches use Statements). CteSchemaResolver
        // and HoverProvider's TVF-alias map used to guard on
        // parseResult.Query and bail, leaving the LET map empty so hover on
        // a LET name returned nothing. EffectiveQuery falls through into
        // Statements[N] for the QueryStatement, restoring hover info.
        LanguageServerManifest manifest = new()
        {
            Tables = [],
            Functions =
            [
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "video_unnest_frames",
                    Parameters = [new ParameterSignature { Name = "source", Kind = "String" }],
                    ReturnType = "table(frame_index Int32, frame VideoFrame)",
                    IsTableValued = true,
                    OutputColumns =
                    [
                        new TableColumnEntry { Name = "frame_index", Kind = "Int32", Nullable = false },
                        new TableColumnEntry { Name = "frame", Kind = "VideoFrame", Nullable = false },
                    ],
                },
                new FunctionSignature
                {
                    SchemaName = "models",
                    Name = "depth_full",
                    Parameters = [new ParameterSignature { Name = "img", Kind = "Image" }],
                    ReturnType = "Struct<depth: Array<Float32>, intrinsics: Array<Float32>>",
                },
            ],
            Keywords = [],
        };
        HoverProvider provider = new(manifest);

        const string sql =
            "DECLARE model_in_w Float32 = 518.0::Float32;\n" +
            "WITH thumb AS (SELECT " +
            "  LET curr_depth = models.depth_full(frame), " +
            "  frame_index " +
            "FROM video_unnest_frames('x.mp4') vid) " +
            "SELECT frame_index FROM thumb";

        int offset = sql.IndexOf("curr_depth", StringComparison.Ordinal);
        HoverResult? result = provider.GetHover(sql, offset);

        Assert.NotNull(result);
        Assert.Contains("curr_depth", result.Contents);
        Assert.Contains("Struct<depth: Array<Float32>, intrinsics: Array<Float32>>", result.Contents);
        Assert.Contains("LET binding", result.Contents);
    }

    [Fact]
    public void GetHover_DeclaredVariable_AtDeclarationSite_ShowsType()
    {
        LanguageServerManifest manifest = new()
        {
            Tables = [],
            Functions = [],
            Keywords = [],
        };
        HoverProvider provider = new(manifest);

        const string sql = "DECLARE model_in_w Float32 = 518.0::Float32";
        int offset = sql.IndexOf("model_in_w", StringComparison.Ordinal);
        HoverResult? result = provider.GetHover(sql, offset);

        Assert.NotNull(result);
        Assert.Contains("model_in_w", result.Contents);
        Assert.Contains("Float32", result.Contents);
        Assert.Contains("DECLAREd variable", result.Contents);
    }

    [Fact]
    public void GetHover_DeclaredVariable_ReferencedInsideCte_ShowsType()
    {
        // Regression: when a DECLAREd variable is referenced inside a CTE's
        // SELECT expression, hover should resolve it through the variable
        // map built from the batch's Statements list. Without the lookup,
        // the identifier falls through to GetColumnHover and returns null.
        LanguageServerManifest manifest = new()
        {
            Tables = [],
            Functions =
            [
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "video_frame_to_image",
                    Parameters =
                    [
                        new ParameterSignature { Name = "frame", Kind = "VideoFrame" },
                        new ParameterSignature { Name = "width", Kind = "Float32" },
                    ],
                    ReturnType = "Image",
                },
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "video_unnest_frames",
                    Parameters = [new ParameterSignature { Name = "source", Kind = "String" }],
                    ReturnType = "table(frame_index Int32, frame VideoFrame)",
                    IsTableValued = true,
                    OutputColumns =
                    [
                        new TableColumnEntry { Name = "frame", Kind = "VideoFrame", Nullable = false },
                    ],
                },
            ],
            Keywords = [],
        };
        HoverProvider provider = new(manifest);

        const string sql =
            "DECLARE model_in_w Float32 = 518.0::Float32;\n" +
            "WITH frames AS (SELECT " +
            "  LET img = video_frame_to_image(vid.frame, model_in_w), " +
            "  frame " +
            "FROM video_unnest_frames('x.mp4') vid) " +
            "SELECT frame FROM frames";

        // Cursor on the inner reference to model_in_w (inside the CTE's
        // SELECT-list expression), not the declaration.
        int offset = sql.LastIndexOf("model_in_w", StringComparison.Ordinal);
        HoverResult? result = provider.GetHover(sql, offset);

        Assert.NotNull(result);
        Assert.Contains("model_in_w", result.Contents);
        Assert.Contains("Float32", result.Contents);
        Assert.Contains("DECLAREd variable", result.Contents);
    }

    [Fact]
    public void GetHover_LetBinding_BinaryDivide_PromotesToFloat()
    {
        // Regression: `LET sx = width::Float32 / model_in_w` where width is
        // Int32 (a CTE column) and model_in_w is a DECLAREd Float32. The
        // CTE resolver used to fall through BinaryExpression to `?`, so
        // hover on `sx` returned nothing. Now the resolver promotes the
        // arithmetic (Float32 / Float32 → Float32) and reads model_in_w's
        // kind from the batch's DECLARE map.
        LanguageServerManifest manifest = new()
        {
            Tables = [],
            Functions =
            [
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "image_width",
                    Parameters = [new ParameterSignature { Name = "img", Kind = "Image" }],
                    ReturnType = "Int32",
                },
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "image_height",
                    Parameters = [new ParameterSignature { Name = "img", Kind = "Image" }],
                    ReturnType = "Int32",
                },
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "video_frame_to_image",
                    Parameters =
                    [
                        new ParameterSignature { Name = "frame", Kind = "VideoFrame" },
                        new ParameterSignature { Name = "width", Kind = "Float32" },
                    ],
                    ReturnType = "Image",
                },
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "video_unnest_frames",
                    Parameters = [new ParameterSignature { Name = "source", Kind = "String" }],
                    ReturnType = "table(frame_index Int32, frame VideoFrame)",
                    IsTableValued = true,
                    OutputColumns =
                    [
                        new TableColumnEntry { Name = "frame", Kind = "VideoFrame", Nullable = false },
                    ],
                },
            ],
            Keywords = [],
        };
        HoverProvider provider = new(manifest);

        const string sql =
            "DECLARE model_in_w Float32 = 518.0::Float32;\n" +
            "WITH frames AS (SELECT " +
            "  LET img = video_frame_to_image(vid.frame, model_in_w), " +
            "  LET width = image_width(img), " +
            "  LET height = image_height(img), " +
            "  LET sx = width::Float32 / model_in_w, " +
            "  LET sy = height / model_in_w, " +
            "  frame " +
            "FROM video_unnest_frames('x.mp4') vid) " +
            "SELECT frame FROM frames";

        // Hover on `sx`: Float32 / Float32 → Float32.
        int sxOffset = sql.IndexOf("LET sx", StringComparison.Ordinal) + "LET ".Length;
        HoverResult? sxResult = provider.GetHover(sql, sxOffset);
        Assert.NotNull(sxResult);
        Assert.Contains("sx", sxResult.Contents);
        Assert.Contains("Float32", sxResult.Contents);

        // Hover on `sy`: Int32 / Float32 → Float32 (engine promotion rule).
        int syOffset = sql.IndexOf("LET sy", StringComparison.Ordinal) + "LET ".Length;
        HoverResult? syResult = provider.GetHover(sql, syOffset);
        Assert.NotNull(syResult);
        Assert.Contains("sy", syResult.Contents);
        Assert.Contains("Float32", syResult.Contents);
    }

    [Theory]
    [InlineData("x BETWEEN 0 AND 10")]
    [InlineData("x IN (1, 2, 3)")]
    [InlineData("x IS NULL")]
    [InlineData("name LIKE 'a%'")]
    [InlineData("NOT (x = 0)")]
    public void GetHover_LetBinding_BooleanResultExpressions_ResolveToBoolean(string boolExpr)
    {
        LanguageServerManifest manifest = new()
        {
            Tables =
            [
                new TableSchemaEntry
                {
                    Name = "t",
                    Columns =
                    [
                        new TableColumnEntry { Name = "x", Kind = "Int32", Nullable = false },
                        new TableColumnEntry { Name = "name", Kind = "String", Nullable = false },
                    ],
                },
            ],
            Functions = [],
            Keywords = [],
        };
        HoverProvider provider = new(manifest);

        string sql = $"WITH cte AS (SELECT LET flag = {boolExpr}, x FROM t) SELECT flag FROM cte";
        int offset = sql.IndexOf("LET flag", StringComparison.Ordinal) + "LET ".Length;
        HoverResult? result = provider.GetHover(sql, offset);

        Assert.NotNull(result);
        Assert.Contains("flag", result.Contents);
        Assert.Contains("Boolean", result.Contents);
    }

    [Fact]
    public void GetHover_LetBinding_UnaryNegate_PreservesOperandKind()
    {
        LanguageServerManifest manifest = new()
        {
            Tables =
            [
                new TableSchemaEntry
                {
                    Name = "t",
                    Columns =
                    [
                        new TableColumnEntry { Name = "amount", Kind = "Float32", Nullable = false },
                    ],
                },
            ],
            Functions = [],
            Keywords = [],
        };
        HoverProvider provider = new(manifest);

        const string sql =
            "WITH cte AS (SELECT LET neg = -amount, amount FROM t) SELECT neg FROM cte";
        int offset = sql.IndexOf("LET neg", StringComparison.Ordinal) + "LET ".Length;
        HoverResult? result = provider.GetHover(sql, offset);

        Assert.NotNull(result);
        Assert.Contains("neg", result.Contents);
        Assert.Contains("Float32", result.Contents);
    }

    [Fact]
    public void GetHover_LetBinding_Case_ResolvesViaFirstBranch()
    {
        LanguageServerManifest manifest = new()
        {
            Tables =
            [
                new TableSchemaEntry
                {
                    Name = "t",
                    Columns =
                    [
                        new TableColumnEntry { Name = "score", Kind = "Int32", Nullable = false },
                    ],
                },
            ],
            Functions = [],
            Keywords = [],
        };
        HoverProvider provider = new(manifest);

        const string sql =
            "WITH cte AS (SELECT LET grade = CASE WHEN score > 90 THEN 'A' ELSE 'B' END, score FROM t) " +
            "SELECT grade FROM cte";
        int offset = sql.IndexOf("LET grade", StringComparison.Ordinal) + "LET ".Length;
        HoverResult? result = provider.GetHover(sql, offset);

        Assert.NotNull(result);
        Assert.Contains("grade", result.Contents);
        Assert.Contains("String", result.Contents);
    }

    // ───────────────────── Procedural variable hover ─────────────────────

    [Fact]
    public void GetHover_ForCounterVariable_ResolvesToInt32()
    {
        LanguageServerManifest manifest = new()
        {
            Tables = [],
            Functions = [],
            Keywords = [],
        };
        HoverProvider provider = new(manifest);

        // Counter variable name is `idx`; cursor lands on its reference
        // inside the loop body, not the declaration.
        const string sql = "FOR idx = 1 TO 10 SELECT idx";
        int offset = sql.LastIndexOf("idx", StringComparison.Ordinal);
        HoverResult? result = provider.GetHover(sql, offset);

        Assert.NotNull(result);
        Assert.Contains("idx", result.Contents);
        Assert.Contains("Int32", result.Contents);
    }

    [Fact]
    public void GetHover_CatchErrorVariable_ResolvesToString()
    {
        LanguageServerManifest manifest = new()
        {
            Tables = [],
            Functions = [],
            Keywords = [],
        };
        HoverProvider provider = new(manifest);

        const string sql = "TRY SELECT 1 CATCH err SELECT err";
        int offset = sql.LastIndexOf("err", StringComparison.Ordinal);
        HoverResult? result = provider.GetHover(sql, offset);

        Assert.NotNull(result);
        Assert.Contains("err", result.Contents);
        Assert.Contains("String", result.Contents);
    }

    [Fact]
    public void GetHover_CreateProcedureParameter_ResolvesToDeclaredType()
    {
        LanguageServerManifest manifest = new()
        {
            Tables = [],
            Functions = [],
            Keywords = [],
        };
        HoverProvider provider = new(manifest);

        const string sql = "CREATE PROCEDURE inc(p Int32) AS BEGIN SET p = p + 1 END";
        // Cursor on the reference to `p` inside the body, not the parameter
        // declaration.
        int offset = sql.IndexOf("SET p", StringComparison.Ordinal) + "SET ".Length;
        HoverResult? result = provider.GetHover(sql, offset);

        Assert.NotNull(result);
        Assert.Contains("p", result.Contents);
        Assert.Contains("Int32", result.Contents);
    }

    [Fact]
    public void GetHover_CreateFunctionParameter_ResolvesToDeclaredType()
    {
        LanguageServerManifest manifest = new()
        {
            Tables = [],
            Functions = [],
            Keywords = [],
        };
        HoverProvider provider = new(manifest);

        // Macro UDF body — cursor on the first `x` in `x * x` (the body
        // expression). Parameter map is walked across the whole batch so
        // body refs resolve through the same path as DECLAREs.
        const string sql = "CREATE FUNCTION square(x Int32) RETURNS Int32 AS x * x";
        int offset = sql.IndexOf("AS x", StringComparison.Ordinal) + "AS ".Length;
        HoverResult? result = provider.GetHover(sql, offset);

        Assert.NotNull(result);
        Assert.Contains("x", result.Contents);
        Assert.Contains("Int32", result.Contents);
    }

    // ───────────────────── Template-string splice hover ─────────────────────

    [Fact]
    public void GetHover_IdentifierInsideTemplateSplice_ResolvesAsColumn()
    {
        // Cursor on `id` inside a `${…}` splice — splice-aware hover
        // re-tokenizes the body and runs the regular identifier
        // resolution against the column visible in the outer FROM scope.
        LanguageServerManifest manifest = new()
        {
            Tables =
            [
                new TableSchemaEntry
                {
                    Name = "users",
                    Columns =
                    [
                        new TableColumnEntry { Name = "id", Kind = "Int32", Nullable = false },
                    ],
                },
            ],
            Functions = [],
            Keywords = [],
        };
        HoverProvider provider = new(manifest);

        const string sql = "SELECT `User: ${id}` FROM users";
        // Cursor on the `i` of `id` inside the splice.
        int offset = sql.IndexOf("${id}", System.StringComparison.Ordinal) + 2;
        HoverResult? result = provider.GetHover(sql, offset);

        Assert.NotNull(result);
        Assert.Contains("id", result.Contents);
        Assert.Contains("Int32", result.Contents);
    }

    [Fact]
    public void GetHover_OutsideSpliceOnLiteralChunk_ReturnsGenericTemplateBlurb()
    {
        // Cursor on a literal chunk of a template string (between
        // splices, or before the first splice). Falls through to the
        // generic template-string hover, not the splice-internal path.
        LanguageServerManifest manifest = new()
        {
            Tables = [], Functions = [], Keywords = [],
        };
        HoverProvider provider = new(manifest);

        const string sql = "SELECT `prefix ${col} suffix`";
        int offset = sql.IndexOf("prefix", System.StringComparison.Ordinal);
        HoverResult? result = provider.GetHover(sql, offset);

        Assert.NotNull(result);
        Assert.Contains("Template string", result.Contents);
    }

    [Fact]
    public void GetHover_InsideUnterminatedSplice_StillResolvesIdentifier()
    {
        // Regression for the repair pass on the hover path. The user is
        // mid-typing inside `${id` with no closing `}` (auto-close
        // disabled, partial paste, etc.). Pre-repair, outer-SQL
        // tokenization failed on the unterminated template, no
        // TemplateString token was produced, and hover returned null.
        // With TokenizeRepair appending `}` + `` ` ``, the outer
        // tokenizer succeeds, the TemplateString hit is found, the
        // locator points at the splice body, and the inner resolver
        // surfaces `id`'s manifest kind.
        LanguageServerManifest manifest = new()
        {
            Tables =
            [
                new TableSchemaEntry
                {
                    Name = "users",
                    Columns =
                    [
                        new TableColumnEntry { Name = "id", Kind = "Int32", Nullable = false },
                    ],
                },
            ],
            Functions = [],
            Keywords = [],
        };
        HoverProvider provider = new(manifest);

        const string sql = "SELECT `User: ${id FROM users";
        int offset = sql.IndexOf("id", System.StringComparison.Ordinal);
        HoverResult? result = provider.GetHover(sql, offset);

        Assert.NotNull(result);
        Assert.Contains("Int32", result.Contents);
    }

    [Fact]
    public void GetHover_MultiLineSpliceIdentifier_TranslatesPositionToOuterCoordinates()
    {
        // Splice spans multiple lines. The inner tokenizer reports
        // splice-local 0-based positions; the hover translates back to
        // outer-SQL coordinates so Monaco's highlight lands on the right
        // characters. Assert the reported StartLine/StartColumn are
        // consistent with the actual outer-SQL position of `id`.
        LanguageServerManifest manifest = new()
        {
            Tables =
            [
                new TableSchemaEntry
                {
                    Name = "users",
                    Columns = [new TableColumnEntry { Name = "id", Kind = "Int32", Nullable = false }],
                },
            ],
            Functions = [],
            Keywords = [],
        };
        HoverProvider provider = new(manifest);

        const string sql = "SELECT `\n  prefix ${\n    id\n  } suffix\n` FROM users";
        // Cursor on `id` (line 2 of sql, 0-based).
        int idOffset = sql.IndexOf("id", System.StringComparison.Ordinal);
        HoverResult? result = provider.GetHover(sql, idOffset);

        Assert.NotNull(result);
        Assert.Contains("Int32", result.Contents);
        // Derive expected (line, column) from the absolute offset and
        // compare with what hover reported.
        int line = 0, col = 0;
        for (int i = 0; i < idOffset; i++)
        {
            if (sql[i] == '\n') { line++; col = 0; }
            else col++;
        }
        Assert.Equal(line, result.StartLine);
        Assert.Equal(col, result.StartColumn);
    }

    // ───────────────────── Literal token hover ─────────────────────

    [Theory]
    [InlineData("42", "Int8")]      // narrows to sbyte
    [InlineData("300", "Int16")]    // outside sbyte, fits short
    [InlineData("100000", "Int32")] // outside short, fits int
    [InlineData("3.14", "Float64")] // float roundtrip not exact
    [InlineData("1.0", "Int8")]     // whole fractional narrows through integer ladder
    public void GetHover_NumberLiteral_ReportsNarrowedKind(string literal, string expectedKind)
    {
        LanguageServerManifest manifest = new()
        {
            Tables = [], Functions = [], Keywords = [],
        };
        HoverProvider provider = new(manifest);

        string sql = $"SELECT {literal}";
        int offset = sql.IndexOf(literal, StringComparison.Ordinal);
        HoverResult? result = provider.GetHover(sql, offset);

        Assert.NotNull(result);
        Assert.Contains("Numeric literal", result.Contents);
        Assert.Contains(expectedKind, result.Contents);
    }

    [Fact]
    public void GetHover_StringLiteral_ReportsKindAndPreview()
    {
        LanguageServerManifest manifest = new()
        {
            Tables = [], Functions = [], Keywords = [],
        };
        HoverProvider provider = new(manifest);

        const string sql = "SELECT 'hello world'";
        int offset = sql.IndexOf('\'');
        HoverResult? result = provider.GetHover(sql, offset);

        Assert.NotNull(result);
        Assert.Contains("String literal", result.Contents);
        Assert.Contains("String", result.Contents);
        Assert.Contains("hello world", result.Contents);
    }

    [Fact]
    public void GetHover_UnprojectedLetStructFieldAccess_ResolvesField()
    {
        // `LET curr_depth = ...` then `curr_depth.depth` — the LET isn't
        // in the CTE output, but the field-access path consults the LET
        // map to surface the field's kind.
        LanguageServerManifest manifest = new()
        {
            Tables = [],
            Functions =
            [
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "video_unnest_frames",
                    Parameters = [new ParameterSignature { Name = "source", Kind = "String" }],
                    ReturnType = "table(frame_index Int32, frame VideoFrame)",
                    IsTableValued = true,
                    OutputColumns =
                    [
                        new TableColumnEntry { Name = "frame", Kind = "VideoFrame", Nullable = false },
                    ],
                },
                new FunctionSignature
                {
                    SchemaName = "models",
                    Name = "depth_full",
                    Parameters = [new ParameterSignature { Name = "img", Kind = "Image" }],
                    ReturnType = "Struct<depth: Array<Float32>, intrinsics: Array<Float32>>",
                },
            ],
            Keywords = [],
        };
        HoverProvider provider = new(manifest);

        // `curr_depth.depth AS chosen` in the SELECT list so the
        // recovering parser sees a complete expression. Cursor lands on
        // `depth` after the dot.
        const string sql =
            "WITH thumb AS (SELECT " +
            "  LET curr_depth = models.depth_full(frame), " +
            "  curr_depth.depth AS chosen " +
            "FROM video_unnest_frames('x.mp4') vid) " +
            "SELECT chosen FROM thumb";

        int offset = sql.LastIndexOf("curr_depth.depth", StringComparison.Ordinal)
            + "curr_depth.".Length;
        HoverResult? result = provider.GetHover(sql, offset);

        Assert.NotNull(result);
        Assert.Contains("depth", result.Contents);
        Assert.Contains("Array<Float32>", result.Contents);
        Assert.Contains("LET", result.Contents);
    }

    // ───────────────────── unnest output synthesis ─────────────────────

    [Fact]
    public void GetHover_UnnestValue_OfModelArrayReturn_SynthesisesElementKind()
    {
        // `unnest(models.X(file))` has no static OutputColumns in the
        // manifest, but its `value` column kind follows the array element
        // type of arg[0]. Hover on `c.value` should surface the
        // `Struct<…>` annotation stripped from `Array<Struct<…>>`.
        LanguageServerManifest manifest = new()
        {
            Tables = [new TableSchemaEntry { Name = "items", Columns =
                [new TableColumnEntry { Name = "file", Kind = "Image", Nullable = false }] }],
            Functions =
            [
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "unnest",
                    Parameters = [new ParameterSignature { Name = "array", Kind = "Any" }],
                    IsTableValued = true,
                },
                new FunctionSignature
                {
                    SchemaName = "models",
                    Name = "yolox_darknet",
                    Parameters = [new ParameterSignature { Name = "img", Kind = "Image" }],
                    ReturnType = "Array<Struct<bbox: Struct<x: Float32, y: Float32, w: Float32, h: Float32>, label: String, score: Float32>>",
                },
            ],
            Keywords = [],
        };
        HoverProvider provider = new(manifest);

        const string sql =
            "SELECT c.value FROM items a CROSS JOIN unnest(models.yolox_darknet(a.file)) c";
        int offset = sql.IndexOf("c.value", StringComparison.Ordinal) + "c.".Length;
        HoverResult? result = provider.GetHover(sql, offset);

        Assert.NotNull(result);
        Assert.Contains("Struct<bbox", result.Contents);
        Assert.Contains("label: String", result.Contents);
    }

    [Fact]
    public void GetHover_StructFieldChain_ThroughUnnestOfModelCall_ResolvesField()
    {
        // Full 3-segment chain: `c.value.label` where `c` is `unnest(models.X(file))`.
        // The deepest segment (cursor on `label`) drives the new 3-segment
        // branch, which resolves `c.value` to the synthesized struct kind
        // and looks `label` up among its fields.
        LanguageServerManifest manifest = new()
        {
            Tables = [new TableSchemaEntry { Name = "items", Columns =
                [new TableColumnEntry { Name = "file", Kind = "Image", Nullable = false }] }],
            Functions =
            [
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "unnest",
                    Parameters = [new ParameterSignature { Name = "array", Kind = "Any" }],
                    IsTableValued = true,
                },
                new FunctionSignature
                {
                    SchemaName = "models",
                    Name = "yolox_darknet",
                    Parameters = [new ParameterSignature { Name = "img", Kind = "Image" }],
                    ReturnType = "Array<Struct<bbox: Struct<x: Float32, y: Float32, w: Float32, h: Float32>, label: String, score: Float32>>",
                },
            ],
            Keywords = [],
        };
        HoverProvider provider = new(manifest);

        const string sql =
            "SELECT c.value.label FROM items a CROSS JOIN unnest(models.yolox_darknet(a.file)) c";
        int offset = sql.IndexOf("c.value.label", StringComparison.Ordinal) + "c.value.".Length;
        HoverResult? result = provider.GetHover(sql, offset);

        Assert.NotNull(result);
        Assert.Contains("c.value.label", result.Contents);
        Assert.Contains("String", result.Contents);
    }

    [Fact]
    public void GetHover_StructFieldChain_ThroughUnnestOfLetBinding_ResolvesField()
    {
        // Same end shape, but unnest's arg is a LET name rather than a
        // direct function call. The resolver should follow
        // cteSchemas.LetBindingKinds to the LET's declared kind, strip
        // the Array<…> wrapper, and surface the field hover.
        LanguageServerManifest manifest = new()
        {
            Tables = [new TableSchemaEntry { Name = "items", Columns =
                [new TableColumnEntry { Name = "file", Kind = "Image", Nullable = false }] }],
            Functions =
            [
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "unnest",
                    Parameters = [new ParameterSignature { Name = "array", Kind = "Any" }],
                    IsTableValued = true,
                },
                new FunctionSignature
                {
                    SchemaName = "models",
                    Name = "yolox_darknet",
                    Parameters = [new ParameterSignature { Name = "img", Kind = "Image" }],
                    ReturnType = "Array<Struct<bbox: Struct<x: Float32, y: Float32, w: Float32, h: Float32>, label: String, score: Float32>>",
                },
            ],
            Keywords = [],
        };
        HoverProvider provider = new(manifest);

        const string sql =
            "SELECT LET classes = models.yolox_darknet(a.file), c.value.label "
            + "FROM items a CROSS JOIN unnest(classes) c";
        int offset = sql.IndexOf("c.value.label", StringComparison.Ordinal) + "c.value.".Length;
        HoverResult? result = provider.GetHover(sql, offset);

        Assert.NotNull(result);
        Assert.Contains("c.value.label", result.Contents);
        Assert.Contains("String", result.Contents);
    }

    [Fact]
    public void GetHover_StructFieldChain_ThroughUnnestOfNamedTypeReturn_ResolvesField()
    {
        // End-to-end tier C: the model's return type is `Array<LabeledDetection>`
        // (named-type, not inline struct). The manifest ships the named-type
        // vocabulary so the hover resolver can expand `LabeledDetection` into
        // its constituent fields and surface `label` as `String`. Without the
        // NamedTypes section the deepest hover would fail (StructTypeAnnotation
        // can't parse a bare named-type reference).
        LanguageServerManifest manifest = new()
        {
            Tables = [new TableSchemaEntry { Name = "items", Columns =
                [new TableColumnEntry { Name = "file", Kind = "Image", Nullable = false }] }],
            Functions =
            [
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "unnest",
                    Parameters = [new ParameterSignature { Name = "array", Kind = "Any" }],
                    IsTableValued = true,
                },
                new FunctionSignature
                {
                    SchemaName = "models",
                    Name = "yolox_darknet",
                    Parameters = [new ParameterSignature { Name = "img", Kind = "Image" }],
                    ReturnType = "Array<LabeledDetection>",
                },
            ],
            NamedTypes =
            [
                new NamedTypeEntry
                {
                    Name = "BoundingBox",
                    Description = "Struct<x: Float32, y: Float32, w: Float32, h: Float32>",
                },
                new NamedTypeEntry
                {
                    Name = "LabeledDetection",
                    Description = "Struct<bbox: BoundingBox, label: String, score: Float32>",
                },
            ],
            Keywords = [],
        };
        HoverProvider provider = new(manifest);

        const string sql =
            "SELECT c.value.label FROM items a CROSS JOIN unnest(models.yolox_darknet(a.file)) c";
        int offset = sql.IndexOf("c.value.label", StringComparison.Ordinal) + "c.value.".Length;
        HoverResult? result = provider.GetHover(sql, offset);

        Assert.NotNull(result);
        Assert.Contains("c.value.label", result.Contents);
        Assert.Contains("String", result.Contents);
    }

    [Fact]
    public void GetHover_UnnestValue_OfNamedTypeArrayReturn_SurfacesNamedType()
    {
        // Cascade check: with the function's ReturnType preserved as
        // `Array<LabeledDetection>`, hovering on `c.value` should strip
        // the array wrapper and surface `LabeledDetection` — keeping the
        // named-type identity intact rather than flattening to `Struct`.
        LanguageServerManifest manifest = new()
        {
            Tables = [new TableSchemaEntry { Name = "items", Columns =
                [new TableColumnEntry { Name = "file", Kind = "Image", Nullable = false }] }],
            Functions =
            [
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "unnest",
                    Parameters = [new ParameterSignature { Name = "array", Kind = "Any" }],
                    IsTableValued = true,
                },
                new FunctionSignature
                {
                    SchemaName = "models",
                    Name = "yolox_darknet",
                    Parameters = [new ParameterSignature { Name = "img", Kind = "Image" }],
                    ReturnType = "Array<LabeledDetection>",
                },
            ],
            Keywords = [],
        };
        HoverProvider provider = new(manifest);

        const string sql =
            "SELECT c.value FROM items a CROSS JOIN unnest(models.yolox_darknet(a.file)) c";
        int offset = sql.IndexOf("c.value", StringComparison.Ordinal) + "c.".Length;
        HoverResult? result = provider.GetHover(sql, offset);

        Assert.NotNull(result);
        Assert.Contains("LabeledDetection", result.Contents);
    }

    // ───────────────────── Markdown-safe kind rendering ─────────────────────

    [Fact]
    public void GetHover_FunctionWithArrayReturn_WrapsKindInBackticks()
    {
        // Hover content is markdown, so a bare `Array<Float32>` would be
        // mistaken for an HTML tag and stripped by editor sanitisers. The
        // renderer wraps kinds in backticks so the angle brackets survive
        // as inline code.
        LanguageServerManifest manifest = new()
        {
            Tables = [],
            Functions =
            [
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "make_floats",
                    Parameters = [new ParameterSignature { Name = "n", Kind = "Int32" }],
                    ReturnType = "Array<Float32>",
                    Description = "Returns N floats.",
                },
            ],
            Keywords = [],
        };
        HoverProvider provider = new(manifest);

        const string sql = "SELECT make_floats(3)";
        HoverResult? result = provider.GetHover(sql, sql.IndexOf("make_floats", StringComparison.Ordinal) + 1);

        Assert.NotNull(result);
        // The Array<Float32> return type renders inside backticks so the
        // IDE's markdown sanitiser keeps the angle-bracket content visible.
        Assert.Contains("`Array<Float32>`", result.Contents);
        // The parameter kind likewise wraps in backticks.
        Assert.Contains("`Int32`", result.Contents);
    }

    // ───────────────────── Hover span ─────────────────────

    [Fact]
    public void GetHover_ReturnedResult_HasValidSpan()
    {
        HoverProvider provider = CreateProvider();

        HoverResult? result = provider.GetHover("SELECT x FROM t", 0);

        Assert.NotNull(result);
        Assert.True(result.StartLine >= 0);
        Assert.True(result.StartColumn >= 0);
        Assert.True(result.EndColumn > result.StartColumn || result.EndLine > result.StartLine);
    }

    // ───────────────────── DDL / DML keyword hover ─────────────────────

    [Fact]
    public void GetHover_CreateKeyword_ReturnsDocumentation()
    {
        HoverProvider provider = CreateProvider();

        HoverResult? result = provider.GetHover("CREATE TEMP TABLE #t (id Int32)", 0);

        Assert.NotNull(result);
        Assert.Contains("CREATE", result.Contents);
    }

    [Fact]
    public void GetHover_TableKeyword_ReturnsDocumentation()
    {
        HoverProvider provider = CreateProvider();

        HoverResult? result = provider.GetHover("CREATE TEMP TABLE #t (id Int32)", 12);

        Assert.NotNull(result);
        Assert.Contains("TABLE", result.Contents);
    }

    [Fact]
    public void GetHover_InsertKeyword_ReturnsDocumentation()
    {
        HoverProvider provider = CreateProvider();

        HoverResult? result = provider.GetHover("INSERT INTO #t VALUES (1)", 0);

        Assert.NotNull(result);
        Assert.Contains("INSERT INTO", result.Contents);
    }

    [Fact]
    public void GetHover_ValuesKeyword_ReturnsDocumentation()
    {
        HoverProvider provider = CreateProvider();

        HoverResult? result = provider.GetHover("INSERT INTO #t VALUES (1)", 15);

        Assert.NotNull(result);
        Assert.Contains("VALUES", result.Contents);
    }

    [Fact]
    public void GetHover_UpdateKeyword_ReturnsDocumentation()
    {
        HoverProvider provider = CreateProvider();

        HoverResult? result = provider.GetHover("UPDATE #t SET col = 1", 0);

        Assert.NotNull(result);
        Assert.Contains("UPDATE", result.Contents);
    }

    [Fact]
    public void GetHover_SetKeyword_ReturnsDocumentation()
    {
        HoverProvider provider = CreateProvider();

        HoverResult? result = provider.GetHover("UPDATE #t SET col = 1", 10);

        Assert.NotNull(result);
        Assert.Contains("SET", result.Contents);
    }

    // ───────────────────── Data type hover ─────────────────────

    [Fact]
    public void GetHover_Int16Type_ReturnsTypeDocumentation()
    {
        HoverProvider provider = CreateProvider();

        // "Int16" starts at offset 24 in this statement.
        HoverResult? result = provider.GetHover("CREATE TABLE #t (col1 Int16)", 22);

        Assert.NotNull(result);
        Assert.Contains("Int16", result.Contents);
        Assert.Contains("16-bit integer", result.Contents);
    }

    [Fact]
    public void GetHover_Float32Type_ReturnsTypeDocumentation()
    {
        HoverProvider provider = CreateProvider();

        HoverResult? result = provider.GetHover("CREATE TABLE #t (col1 Float32)", 22);

        Assert.NotNull(result);
        Assert.Contains("Float32", result.Contents);
        Assert.Contains("floating-point", result.Contents);
    }

    [Fact]
    public void GetHover_StringType_ReturnsTypeDocumentation()
    {
        HoverProvider provider = CreateProvider();

        HoverResult? result = provider.GetHover("CREATE TABLE #t (col1 String)", 22);

        Assert.NotNull(result);
        Assert.Contains("String", result.Contents);
        Assert.Contains("UTF-8", result.Contents);
    }

    // ───────────────────── Lambda parameter hover ─────────────────────

    /// <summary>
    /// Manifest with a context-aware higher-order function plus the
    /// AnimationContext + ParticleContext descriptors — enough to render
    /// the kind + parent-chain info the lambda-parameter hover surfaces.
    /// </summary>
    private static LanguageServerManifest CreateLambdaTestManifest()
    {
        return new LanguageServerManifest
        {
            Tables = [],
            Keywords = ["SELECT", "FROM"],
            Functions =
            [
                new FunctionSignature
                {
                    Name = "animate_frames",
                    Parameters =
                    [
                        new ParameterSignature { Name = "duration", Kind = "Float32" },
                        new ParameterSignature { Name = "fps", Kind = "Int32" },
                        new ParameterSignature { Name = "size", Kind = "Point2D" },
                        new ParameterSignature { Name = "render_frame", Kind = "Lambda<animation, returns: Drawing>", LambdaContextName = "animation" },
                    ],
                    ReturnType = "Array<Image>",
                    Description = "Drives an animation lambda over duration*fps frames.",
                },
                new FunctionSignature
                {
                    Name = "draw_particles",
                    // Primary variant uses a static Drawing sprite; the lambda
                    // sprite_fn is in an additional shape — same as the runtime.
                    Parameters =
                    [
                        new ParameterSignature { Name = "t", Kind = "Float32" },
                        new ParameterSignature { Name = "emit_at", Kind = "Point2D" },
                        new ParameterSignature { Name = "rate", Kind = "Float32" },
                        new ParameterSignature { Name = "lifetime", Kind = "Float32" },
                        new ParameterSignature { Name = "velocity", Kind = "Point2D" },
                        new ParameterSignature { Name = "jitter", Kind = "Float32" },
                        new ParameterSignature { Name = "sprite", Kind = "Drawing" },
                    ],
                    AdditionalParameterShapes =
                    [
                        [
                            new ParameterSignature { Name = "t", Kind = "Float32" },
                            new ParameterSignature { Name = "emit_at", Kind = "Point2D" },
                            new ParameterSignature { Name = "rate", Kind = "Float32" },
                            new ParameterSignature { Name = "lifetime", Kind = "Float32" },
                            new ParameterSignature { Name = "velocity", Kind = "Point2D" },
                            new ParameterSignature { Name = "jitter", Kind = "Float32" },
                            new ParameterSignature { Name = "sprite_fn", Kind = "Lambda<particle, returns: Drawing>", LambdaContextName = "particle" },
                        ],
                    ],
                    ReturnType = "Drawing",
                    Description = "Deterministic particle emitter.",
                },
            ],
            FunctionContexts =
            [
                new FunctionContextEntry
                {
                    Name = "animation",
                    Parameters = [new LambdaParameterEntry { Name = "t", Kind = "Float32" }],
                    ParentName = "pure",
                    Borrows = [],
                },
                new FunctionContextEntry
                {
                    Name = "particle",
                    Parameters = [new LambdaParameterEntry { Name = "x", Kind = "Float32" }],
                    ParentName = "animation",
                    Borrows = [],
                },
                new FunctionContextEntry
                {
                    Name = "pure",
                    Parameters = [],
                    ParentName = null,
                    Borrows = [],
                },
            ],
        };
    }

    [Fact]
    public void GetHover_LambdaParam_T_InsideAnimateFrames_ReportsAnimationContext()
    {
        HoverProvider provider = new(CreateLambdaTestManifest());

        // `t` appears at offset 56 (inside the lambda body).
        const string sql = "SELECT animate_frames(1.0, 8, point2d(64,64), (t) -> t)";
        int cursor = sql.LastIndexOf('t');
        HoverResult? result = provider.GetHover(sql, cursor);

        Assert.NotNull(result);
        // Header shows kind + name.
        Assert.Contains("**t**", result.Contents);
        Assert.Contains("Float32", result.Contents);
        // Context label + parent chain breadcrumb.
        Assert.Contains("animation", result.Contents);
        Assert.Contains("pure", result.Contents);
        // Bound-by line mentions the outer call.
        Assert.Contains("animate_frames", result.Contents);
        // Human-readable description.
        Assert.Contains("normalised time", result.Contents);
    }

    [Fact]
    public void GetHover_LambdaParam_X_InsideDrawParticles_ResolvesViaAdditionalShape()
    {
        // draw_particles' primary signature has a static `sprite Drawing`
        // at position 6. The lambda variant uses Lambda<particle> at the
        // same position; the hover should reach into AdditionalParameterShapes
        // to find the lambda slot rather than reporting the static Drawing.
        HoverProvider provider = new(CreateLambdaTestManifest());

        const string sql =
            "SELECT draw_particles(t, point2d(32,56), 20, 0.4, point2d(0,-80), 0, x -> x)";
        int cursor = sql.LastIndexOf('x');
        HoverResult? result = provider.GetHover(sql, cursor);

        Assert.NotNull(result);
        Assert.Contains("**x**", result.Contents);
        Assert.Contains("Float32", result.Contents);
        Assert.Contains("particle", result.Contents);
        // Parent chain follows particle → animation → pure.
        Assert.Contains("animation", result.Contents);
        Assert.Contains("normalised age", result.Contents);
    }

    [Fact]
    public void GetHover_OnLambdaParameterDeclaration_ShowsLambdaCard()
    {
        // Regression for: hover on the parameter `t` in `(t) -> body`
        // (cursor on the declaration, BEFORE the arrow) returned nothing
        // because the active-scope walker only pushes scopes once it
        // processes `->`. The forward-looking declaration detection in
        // TryFindLambdaScopeForParameterDeclaration synthesises the
        // scope so hover renders the same card you'd get inside the body.
        HoverProvider provider = new(CreateLambdaTestManifest());

        const string sql = "SELECT animate_frames(1.0, 8, point2d(64, 64), (t) -> t)";
        // Cursor on the `t` inside (t), not the `t` in the body.
        int cursor = sql.IndexOf("(t)") + 1;
        HoverResult? result = provider.GetHover(sql, cursor);

        Assert.NotNull(result);
        Assert.Contains("**t**", result.Contents);
        Assert.Contains("animation", result.Contents);
    }

    [Fact]
    public void GetHover_OnSingleParamLambdaDeclaration_ShowsLambdaCard()
    {
        // Single-parameter form: `x -> body`. Cursor on the `x`. Same
        // detection but via the form-1 branch (no enclosing parens).
        HoverProvider provider = new(CreateLambdaTestManifest());

        const string sql =
            "SELECT draw_particles(t, point2d(32,56), 20, 0.4, point2d(0,-80), 0, x -> x)";
        int cursor = sql.IndexOf("x ->");
        HoverResult? result = provider.GetHover(sql, cursor);

        Assert.NotNull(result);
        Assert.Contains("**x**", result.Contents);
        Assert.Contains("particle", result.Contents);
    }

    [Fact]
    public void GetHover_RenamedLambdaParam_StillResolves()
    {
        // The user may rename the canonical `t` to `u`. The lambda scope
        // walker tracks the actual parameter name; the manifest's canonical
        // info comes from the context, but the hover header should keep the
        // user's name verbatim.
        HoverProvider provider = new(CreateLambdaTestManifest());

        const string sql = "SELECT animate_frames(1.0, 8, point2d(64,64), u -> u)";
        int cursor = sql.LastIndexOf('u');
        HoverResult? result = provider.GetHover(sql, cursor);

        Assert.NotNull(result);
        Assert.Contains("**u**", result.Contents);
        Assert.Contains("Float32", result.Contents);
        Assert.Contains("animation", result.Contents);
    }

    [Fact]
    public void GetHover_TwoParameterLambda_BothResolveByName()
    {
        // (a, b) -> a + b — both names should resolve to lambda parameters.
        HoverProvider provider = new(CreateLambdaTestManifest());

        // Use a hypothetical 2-arg lambda construct via array_transform.
        // We don't have that in the test manifest, but we don't need an
        // outer-call match for the walker to recognise the parameters —
        // the hover degrades gracefully to "Lambda parameter".
        const string sql = "SELECT (a, b) -> a + b";
        int cursorA = sql.IndexOf("-> a") + 3;  // first `a` after the arrow
        HoverResult? resultA = provider.GetHover(sql, cursorA);
        Assert.NotNull(resultA);
        Assert.Contains("**a**", resultA.Contents);

        int cursorB = sql.LastIndexOf('b');
        HoverResult? resultB = provider.GetHover(sql, cursorB);
        Assert.NotNull(resultB);
        Assert.Contains("**b**", resultB.Contents);
    }

    [Fact]
    public void GetHover_IdentifierOutsideLambda_FallsThroughToColumn()
    {
        // An identifier that's genuinely outside any lambda — neither in a
        // lambda body nor a parameter-declaration position — should NOT
        // pick up a lambda card. Use a column-ish bare identifier outside
        // every lambda's reach.
        HoverProvider provider = new(CreateLambdaTestManifest());

        // `xyz` is a column-style identifier in a plain SELECT — no lambda
        // anywhere in this SQL, so the provider must not synthesise a
        // lambda card. (Returns null since the test manifest has no `xyz`
        // column; the assertion is that no lambda info leaks in.)
        const string sql = "SELECT xyz FROM t";
        int cursor = sql.IndexOf("xyz");
        HoverResult? result = provider.GetHover(sql, cursor);
        if (result is not null)
        {
            Assert.DoesNotContain("Bound by", result.Contents);
            Assert.DoesNotContain("lambda", result.Contents);
        }
    }

    [Fact]
    public void GetHover_NestedLambdaShadowing_InnerWins()
    {
        // Outer lambda's `t` is shadowed by inner lambda's `t`. The
        // innermost matching scope should win; since the inner lambda's
        // outer call isn't recognised (no manifest entry), we still get
        // a lambda-parameter hover (with degraded context info).
        HoverProvider provider = new(CreateLambdaTestManifest());

        const string sql =
            "SELECT animate_frames(1.0, 8, point2d(64,64), (t) -> array_transform([t], (t) -> t * 2))";
        int cursor = sql.LastIndexOf("t * 2");
        HoverResult? result = provider.GetHover(sql, cursor);
        Assert.NotNull(result);
        Assert.Contains("**t**", result.Contents);
    }
}
