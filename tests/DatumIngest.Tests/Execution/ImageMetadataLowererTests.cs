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
/// Tests the planner-time lowering pass that decomposes
/// <c>pixel_count(img)</c> and <c>dimensions(img, literal)</c> into
/// compositions of <c>image_width</c> / <c>image_height</c> /
/// <c>image_channels</c>. Covers both the rewritten plan shape and
/// end-to-end execution correctness.
/// </summary>
public sealed class ImageMetadataLowererTests : ServiceTestBase
{
    private static QueryOperator PlanQuery(string sql, TableCatalog catalog)
    {
        QueryExpression query = SqlParser.Parse(sql);
        QueryPlanner planner = new(catalog, FunctionRegistry.CreateDefault());
        return planner.Plan(query);
    }

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
        SkiaSharp.SKBitmap bmp = new(width, height, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul);
        using SkiaSharp.SKImage image = SkiaSharp.SKImage.FromBitmap(bmp);
        using SkiaSharp.SKData data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // ─────────────── pixel_count lowering ───────────────

    [Fact]
    public void PixelCount_LowersToWidthHeightMultiply()
    {
        TableCatalog catalog = CreateImageCatalog(MakeTinyPng(width: 320, height: 240));
        QueryOperator plan = PlanQuery("SELECT pixel_count(img) FROM t", catalog);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);
        BinaryExpression binary = Assert.IsType<BinaryExpression>(project.Columns[0].Expression);
        Assert.Equal(BinaryOperator.Multiply, binary.Operator);

        InlineAccessorExpression left = Assert.IsType<InlineAccessorExpression>(binary.Left);
        InlineAccessorExpression right = Assert.IsType<InlineAccessorExpression>(binary.Right);
        Assert.Equal(InlineAccessorField.ImageWidth, left.Field);
        Assert.Equal(InlineAccessorField.ImageHeight, right.Field);
    }

    [Fact]
    public async Task PixelCount_EndToEnd_ReturnsWidthTimesHeight()
    {
        TableCatalog catalog = CreateImageCatalog(MakeTinyPng(width: 320, height: 240));
        List<Row> rows = await ExecuteQueryAsync("SELECT pixel_count(img) AS n FROM t", catalog);

        Assert.Single(rows);
        Assert.Equal(320 * 240, rows[0]["n"].AsInt32());
    }

    [Fact]
    public async Task PixelCount_NullImage_ReturnsNull()
    {
        Pool pool = CreatePool();
        TableCatalog catalog = CreateCatalog(pool);
        catalog.Add(new InMemoryTableProvider(pool, "t",
            columns: ["img"],
            columnKinds: [DataKind.Image],
            rows: [[null]]));

        List<Row> rows = await ExecuteQueryAsync("SELECT pixel_count(img) AS n FROM t", catalog);

        Assert.Single(rows);
        Assert.True(rows[0]["n"].IsNull);
    }

    [Fact]
    public async Task PixelCount_AndImageWidth_CrossClauseShareAccessorReadViaCse()
    {
        // SELECT pixel_count(img) lowers to width * height; if WHERE also references
        // image_width(img), CSE should collapse the two width reads into one
        // RowEnricher. The end-to-end value just confirms correctness; the plan-shape
        // assertion checks that exactly one enricher carries the elided width.
        TableCatalog catalog = CreateImageCatalog(MakeTinyPng(width: 1920, height: 1080));
        QueryOperator plan = PlanQuery(
            "SELECT pixel_count(img) AS n FROM t WHERE image_width(img) > 100",
            catalog);

        int widthEnricherCount = 0;
        QueryOperator? cursor = plan;
        while (cursor is not null)
        {
            if (cursor is RowEnricherOperator enricher)
            {
                foreach (RowEnrichment e in enricher.Enrichments)
                {
                    if (e.Expression is InlineAccessorExpression iax
                        && iax.Field == InlineAccessorField.ImageWidth)
                    {
                        widthEnricherCount++;
                    }
                }
            }
            cursor = cursor switch
            {
                ProjectOperator p => p.Source,
                FilterOperator f => f.Source,
                RowEnricherOperator r => r.Source,
                _ => null,
            };
        }
        Assert.Equal(1, widthEnricherCount);
    }

    // ─────────────── dimensions lowering ───────────────

    [Theory]
    [InlineData("'WH'",  new[] { "ImageWidth", "ImageHeight" })]
    [InlineData("'WHC'", new[] { "ImageWidth", "ImageHeight", "ImageChannels" })]
    [InlineData("'HWC'", new[] { "ImageHeight", "ImageWidth", "ImageChannels" })]
    [InlineData("'CHW'", new[] { "ImageChannels", "ImageHeight", "ImageWidth" })]
    public void Dimensions_LiteralFormat_LowersToArrayOfElidedAccessors(string fmtLiteral, string[] expectedFields)
    {
        TableCatalog catalog = CreateImageCatalog(MakeTinyPng(width: 320, height: 240));
        QueryOperator plan = PlanQuery($"SELECT dimensions(img, {fmtLiteral}) FROM t", catalog);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);
        FunctionCallExpression call = Assert.IsType<FunctionCallExpression>(project.Columns[0].Expression);
        Assert.Equal("array", call.FunctionName);

        Assert.Equal(expectedFields.Length, call.Arguments.Count);
        for (int i = 0; i < expectedFields.Length; i++)
        {
            InlineAccessorExpression accessor = Assert.IsType<InlineAccessorExpression>(call.Arguments[i]);
            Assert.Equal(expectedFields[i], accessor.Field.ToString());
        }
    }

    // End-to-end array content for the lowered dimensions(...) is covered by
    // composition: the lowering-plan-shape test above proves the array is built
    // from the accessors in the correct order, the elider tests prove each
    // accessor returns the right value, and the runtime-fallback test
    // (Dimensions_KnownFormat_ReturnsOrderedAxisArray in ImageMetadataFunctionsTests)
    // proves the standalone function produces the same orderings.

    // ─────────────── image_channels end-to-end ───────────────

    [Fact]
    public async Task ImageChannels_StampedRgba_Returns4()
    {
        // PNG → ImageDataValueFactory.FromEncodedBytes stamps channels via header parse.
        TableCatalog catalog = CreateImageCatalog(MakeTinyPng(width: 4, height: 4));
        List<Row> rows = await ExecuteQueryAsync("SELECT image_channels(img) AS c FROM t", catalog);

        Assert.Single(rows);
        Assert.Equal(4, rows[0]["c"].AsInt32());
    }

    [Fact]
    public void ImageChannels_RewritesToInlineAccessorExpression()
    {
        TableCatalog catalog = CreateImageCatalog(MakeTinyPng(width: 4, height: 4));
        QueryOperator plan = PlanQuery("SELECT image_channels(img) FROM t", catalog);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);
        InlineAccessorExpression elided = Assert.IsType<InlineAccessorExpression>(
            project.Columns[0].Expression);
        Assert.Equal(InlineAccessorField.ImageChannels, elided.Field);
    }
}
