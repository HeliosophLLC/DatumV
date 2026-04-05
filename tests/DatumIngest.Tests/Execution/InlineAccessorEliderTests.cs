using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests the planner-time elision pass that rewrites
/// <see cref="FunctionCallExpression"/> calls to
/// <see cref="IInlineMetadataAccessor"/>-marked functions into
/// <see cref="InlineAccessorExpression"/> nodes. Covers plan-shape
/// rewriting, end-to-end execution parity (stamped + unstamped paths),
/// and CSE deduplication on the rewritten node's record equality.
/// </summary>
public sealed class InlineAccessorEliderTests : ServiceTestBase
{
    private static QueryOperator PlanQuery(string sql, TableCatalog catalog)
    {
        QueryExpression query = SqlParser.Parse(sql);
        QueryPlanner planner = new(catalog, FunctionRegistry.CreateDefault());
        return planner.Plan(query);
    }

    /// <summary>
    /// Builds a single-row catalog with an Image column carrying the supplied
    /// raw bytes. The <see cref="InMemoryTableProvider"/> materialises the
    /// <c>byte[]</c> through <see cref="DatumIngest.Functions.Image.ImageDataValueFactory.FromEncodedBytes"/>,
    /// so PNG/JPEG/WebP/BMP signatures get inline (width, height, channels)
    /// stamped on the resulting <see cref="DataValue"/> — exercising the
    /// elider's fast path. The unstamped fallback path is covered by the
    /// per-function tests in <c>ImageDimensionFunctionsTests</c>; replicating
    /// it here would require constructing an Image with an unparseable
    /// header, which the provider doesn't expose a hook for.
    /// </summary>
    private TableCatalog CreateImageCatalog(byte[] imageBytes)
    {
        Pool pool = CreatePool();
        TableCatalog catalog = CreateCatalog(pool);
        catalog.Add(new InMemoryTableProvider(pool, "t",
            columns: ["img"],
            columnKinds: [DataKind.Image],
            rows: [[imageBytes]]));
        return catalog;
    }

    private static byte[] MakeTinyPng(int width = 1, int height = 1)
    {
        // Built via SKBitmap → PNG encode (rather than a hex literal) so we
        // don't pin the test to a specific codec output. PNG's IHDR is the
        // first chunk after the 8-byte signature, so the header parser stamps
        // (width, height, channels) without needing a full decode.
        SkiaSharp.SKBitmap bmp = new(width, height, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul);
        using SkiaSharp.SKImage image = SkiaSharp.SKImage.FromBitmap(bmp);
        using SkiaSharp.SKData data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // ─────────────── Plan-shape rewriting ───────────────

    [Fact]
    public void ImageWidthCall_RewritesToInlineAccessorExpression()
    {
        TableCatalog catalog = CreateImageCatalog(MakeTinyPng(width: 1920, height: 1080));
        QueryOperator plan = PlanQuery("SELECT image_width(img) FROM t", catalog);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);
        InlineAccessorExpression elided = Assert.IsType<InlineAccessorExpression>(
            project.Columns[0].Expression);
        Assert.Equal(InlineAccessorField.ImageWidth, elided.Field);
        Assert.IsType<ColumnReference>(elided.Argument);
    }

    [Fact]
    public void NestedAccessorInsideArithmetic_RewritesUnderBinary()
    {
        // image_width(img) / 2 — the accessor inside the binary's left operand
        // should be elided even though it's not the top-level expression.
        TableCatalog catalog = CreateImageCatalog(MakeTinyPng(width: 1920, height: 1080));
        QueryOperator plan = PlanQuery("SELECT image_width(img) / 2 AS half FROM t", catalog);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);
        BinaryExpression binary = Assert.IsType<BinaryExpression>(project.Columns[0].Expression);
        InlineAccessorExpression elided = Assert.IsType<InlineAccessorExpression>(binary.Left);
        Assert.Equal(InlineAccessorField.ImageWidth, elided.Field);
    }

    [Fact]
    public void NonAccessorFunction_NotElided()
    {
        // image_brightness_mean has no IInlineMetadataAccessor marker — its
        // body requires a full pixel walk, not a struct-byte read — so it must
        // pass through the elider unchanged. Guards against the elider over-
        // firing on functions that merely share the image_* prefix.
        TableCatalog catalog = CreateImageCatalog(MakeTinyPng(width: 1920, height: 1080));
        QueryOperator plan = PlanQuery("SELECT image_brightness_mean(img) FROM t", catalog);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);
        FunctionCallExpression call = Assert.IsType<FunctionCallExpression>(
            project.Columns[0].Expression);
        Assert.Equal("image_brightness_mean", call.FunctionName);
    }

    [Fact]
    public void CrossClauseDuplicateAccessor_HoistsOnceViaCse()
    {
        // image_width(img) in WHERE + SELECT should collapse to a single hoisted
        // read — exercises CSE on the post-elision InlineAccessorExpression's
        // record equality.
        TableCatalog catalog = CreateImageCatalog(MakeTinyPng(width: 1920, height: 1080));
        QueryOperator plan = PlanQuery(
            "SELECT image_width(img) FROM t WHERE image_width(img) > 100",
            catalog);

        int enricherCount = 0;
        QueryOperator? cursor = plan;
        while (cursor is not null)
        {
            if (cursor is RowEnricherOperator enricher)
            {
                enricherCount++;
                // The hoisted enrichment should carry the elided accessor.
                Assert.IsType<InlineAccessorExpression>(enricher.Enrichments[0].Expression);
            }
            cursor = cursor switch
            {
                ProjectOperator p => p.Source,
                FilterOperator f => f.Source,
                RowEnricherOperator r => r.Source,
                _ => null,
            };
        }

        Assert.Equal(1, enricherCount);
    }

    // ─────────────── Operator coverage: elision fires through every path ───────────────
    //
    // Project (SELECT) is covered by ImageWidthCall_RewritesToInlineAccessorExpression.
    // Filter (WHERE) is covered by CrossClauseDuplicateAccessor_HoistsOnceViaCse.
    // The probes below confirm the rewrite reaches ORDER BY, GROUP BY, and ASSERT
    // — all should share the planner pass via QueryOperator.RewriteExpressions.

    [Fact]
    public void OrderByExpression_IsElided()
    {
        TableCatalog catalog = CreateImageCatalog(MakeTinyPng(width: 320, height: 240));
        QueryOperator plan = PlanQuery(
            "SELECT img FROM t ORDER BY image_width(img)",
            catalog);

        OrderByOperator? orderBy = FindOperator<OrderByOperator>(plan);
        Assert.NotNull(orderBy);
        InlineAccessorExpression elided = Assert.IsType<InlineAccessorExpression>(
            orderBy.OrderByItems[0].Expression);
        Assert.Equal(InlineAccessorField.ImageWidth, elided.Field);
    }

    [Fact]
    public void GroupByKey_IsElided()
    {
        TableCatalog catalog = CreateImageCatalog(MakeTinyPng(width: 320, height: 240));
        QueryOperator plan = PlanQuery(
            "SELECT image_width(img), count(*) FROM t GROUP BY image_width(img)",
            catalog);

        GroupByOperator? groupBy = FindOperator<GroupByOperator>(plan);
        Assert.NotNull(groupBy);
        InlineAccessorExpression elided = Assert.IsType<InlineAccessorExpression>(
            groupBy.GroupByExpressions[0]);
        Assert.Equal(InlineAccessorField.ImageWidth, elided.Field);
    }

    [Fact]
    public void AssertPredicate_IsElided()
    {
        // ASSERT runs as a clause on ProjectOperator (DEFINE { ASSERT ... }).
        // The predicate is an Expression slot that QueryOperator.RewriteExpressions
        // walks, so the elider must rewrite it like any other expression.
        TableCatalog catalog = CreateImageCatalog(MakeTinyPng(width: 320, height: 240));
        QueryOperator plan = PlanQuery(
            "SELECT DEFINE { ASSERT image_width(img) > 0; } img FROM t",
            catalog);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);
        Assert.NotNull(project.Assertions);
        BinaryExpression predicate = Assert.IsType<BinaryExpression>(
            project.Assertions[0].Predicate);
        InlineAccessorExpression elided = Assert.IsType<InlineAccessorExpression>(
            predicate.Left);
        Assert.Equal(InlineAccessorField.ImageWidth, elided.Field);
    }

    /// <summary>
    /// Walks the operator tree looking for the first instance of
    /// <typeparamref name="T"/>. Returns <c>null</c> when no such operator exists.
    /// Handles the operator chains exercised by the probe tests above.
    /// </summary>
    private static T? FindOperator<T>(QueryOperator root) where T : QueryOperator
    {
        QueryOperator? cursor = root;
        while (cursor is not null)
        {
            if (cursor is T match) return match;
            cursor = cursor switch
            {
                ProjectOperator p => p.Source,
                FilterOperator f => f.Source,
                OrderByOperator o => o.Source,
                GroupByOperator g => g.Source,
                RowEnricherOperator r => r.Source,
                _ => null,
            };
        }
        return null;
    }

    // ─────────────── End-to-end execution parity ───────────────

    [Fact]
    public async Task ImageWidth_StampedPath_ReturnsInlineWidth()
    {
        TableCatalog catalog = CreateImageCatalog(MakeTinyPng(width: 1920, height: 1080));
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT image_width(img) AS w, image_height(img) AS h FROM t",
            catalog);

        Assert.Single(rows);
        Assert.Equal(1920, rows[0]["w"].AsInt32());
        Assert.Equal(1080, rows[0]["h"].AsInt32());
    }

    [Fact]
    public async Task ImageWidth_NullImage_ReturnsNull()
    {
        // NULL propagation matches the original function: NULL in → NULL out,
        // typed to the function's declared result kind (Int32 here).
        Pool pool = CreatePool();
        TableCatalog catalog = CreateCatalog(pool);
        catalog.Add(new InMemoryTableProvider(pool, "t",
            columns: ["img"],
            columnKinds: [DataKind.Image],
            rows: [[null]]));

        List<Row> rows = await ExecuteQueryAsync("SELECT image_width(img) AS w FROM t", catalog);

        Assert.Single(rows);
        Assert.True(rows[0]["w"].IsNull);
        Assert.Equal(DataKind.Int32, rows[0]["w"].Kind);
    }

    // ─────────────── EXPLAIN / fingerprint ───────────────

    [Fact]
    public void FormatExpression_RendersAsCanonicalFunctionCall()
    {
        // The elided node should round-trip through EXPLAIN as the original
        // function syntax — readers shouldn't have to learn a new printer.
        InlineAccessorExpression node = new(
            new ColumnReference(null, "img"),
            InlineAccessorField.ImageWidth);

        Assert.Equal("image_width(img)", QueryExplainer.FormatExpression(node));
        Assert.Equal("image_width(img)", QueryExplainer.Fingerprint(node));
    }
}
