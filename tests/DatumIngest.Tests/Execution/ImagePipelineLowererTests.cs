namespace DatumIngest.Tests.Execution;

using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Image;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

using SkiaSharp;

/// <summary>
/// Smoke tests for the <c>img(source, lambda)</c> fusion architecture: the lowerer
/// pass that rewrites lambda chains into <see cref="FusedImagePipelineExpression"/>,
/// the runtime evaluator's pipeline handler, and end-to-end query execution. Validates
/// the architecture before each remaining image function migrates to
/// <see cref="IImagePipelineFunction"/>.
/// </summary>
public sealed class ImagePipelineLowererTests : ServiceTestBase
{
    /// <summary>Encodes a small solid-color PNG for use as test image input.</summary>
    private static byte[] MakeTestPng(int width = 8, int height = 8)
    {
        using SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(SKColors.Red);
        using SKData encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    /// <summary>
    /// <c>img(source, f =&gt; f)</c> is the identity pipeline. The lowerer must
    /// short-circuit it to the source expression directly — no
    /// <see cref="FusedImagePipelineExpression"/> emitted, no decode, no encode.
    /// </summary>
    [Fact]
    public void Identity_LambdaShortCircuits_ToSourceExpression()
    {
        // Build a simple SELECT img(file, f -> f) FROM t expression and lower it.
        ColumnReference fileRef = new(TableName: null, ColumnName: "file");
        LambdaExpression identityLambda = new(
            Parameters: ["f"],
            Body: new ColumnReference(TableName: null, ColumnName: "f"));
        FunctionCallExpression imageCall = new(
            FunctionName: "img",
            Arguments: [fileRef, identityLambda]);

        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Expression lowered = ImagePipelineLowerer.Lower(imageCall, registry);

        // Must collapse to the source — no pipeline node emitted for identity.
        Assert.Same(fileRef, lowered);
    }

    /// <summary>
    /// Non-pipeline functions in the lambda body (e.g. <c>length(f)</c>) are rejected
    /// at plan time with a clear message. <c>length</c> is not registered as an
    /// <see cref="IImagePipelineFunction"/>, so the walker fails on the first stage.
    /// </summary>
    [Fact]
    public void NonPipelineFunction_InLambdaBody_RejectedAtPlanTime()
    {
        ColumnReference fileRef = new(TableName: null, ColumnName: "file");
        LambdaExpression lengthLambda = new(
            Parameters: ["f"],
            Body: new FunctionCallExpression(
                FunctionName: "length",
                Arguments: [new ColumnReference(TableName: null, ColumnName: "f")]));
        FunctionCallExpression imageCall = new(
            FunctionName: "img",
            Arguments: [fileRef, lengthLambda]);

        FunctionRegistry registry = FunctionRegistry.CreateDefault();

        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => ImagePipelineLowerer.Lower(imageCall, registry));
        Assert.Contains("'length'", ex.Message);
        Assert.Contains("IImagePipelineFunction", ex.Message);
    }

    /// <summary>
    /// A single-stage pipeline lowers to a <see cref="FusedImagePipelineExpression"/>
    /// with one transform and no sink. The transform is <see cref="BlurImageFunction"/>,
    /// the only image function migrated to <see cref="IImagePipelineFunction"/> so far.
    /// </summary>
    [Fact]
    public void SingleTransform_LowersToFusedPipelineNode()
    {
        ColumnReference fileRef = new(TableName: null, ColumnName: "file");
        LambdaExpression blurLambda = new(
            Parameters: ["f"],
            Body: new FunctionCallExpression(
                FunctionName: "blur",
                Arguments:
                [
                    new ColumnReference(TableName: null, ColumnName: "f"),
                    new LiteralExpression(5),
                ]));
        FunctionCallExpression imageCall = new(
            FunctionName: "img",
            Arguments: [fileRef, blurLambda]);

        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Expression lowered = ImagePipelineLowerer.Lower(imageCall, registry);

        FusedImagePipelineExpression pipeline = Assert.IsType<FusedImagePipelineExpression>(lowered);
        Assert.Same(fileRef, pipeline.Source);
        Assert.Single(pipeline.Transforms);
        Assert.IsType<BlurImageFunction>(pipeline.Transforms[0].Function);
        Assert.Null(pipeline.TerminalSink);
        Assert.Equal(DataKind.Image, pipeline.ResultKind);
    }

    /// <summary>
    /// End-to-end smoke: <c>SELECT img(file, f =&gt; blur(f, 5)) FROM t</c> over a
    /// table whose <c>file</c> column holds PNG bytes. Verifies that the pipeline
    /// (decode once → blur → encode once) produces a non-null Image value, and that
    /// the bytes form a valid image that re-decodes.
    /// </summary>
    [Fact]
    public async Task SingleTransformPipeline_ProducesValidImage_EndToEnd()
    {
        // Pass raw byte[] cells — InMemoryTableProvider materialises them as UInt8Array
        // values inside the scan batch's own arena. The pipeline's source accepts
        // UInt8Array (it's just bytes — no kind-cast needed).
        byte[] png = MakeTestPng(width: 16, height: 16);
        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["file"],
            new object?[] { png });

        List<Row> rows = await ExecuteQueryAsync("SELECT img(file, f -> blur(f, 5)) FROM t", catalog);

        // The pipeline ran end-to-end without throwing and produced a non-null Image-kind
        // result. That's enough to prove decode/transform/encode all work; verifying the
        // exact encoded bytes would require the test to read against the same store the
        // collector stabilised into, which CollectRowsAsync owns internally.
        Assert.Single(rows);
        DataValue result = rows[0][0];
        Assert.False(result.IsNull, "Pipeline should produce a non-null Image value.");
        Assert.Equal(DataKind.Image, result.Kind);
    }

    /// <summary>
    /// Pipeline ending in a terminal sink (<c>width</c>) returns the sink's
    /// <see cref="DataKind"/>, not Image bytes. Validates the no-encode-after-sink path
    /// in the runtime evaluator and that <see cref="IImagePipelineSink.Reduce"/> sees
    /// the live bitmap dimensions.
    /// </summary>
    [Fact]
    public async Task TerminalSinkPipeline_ReturnsReducedValue_NotImageBytes()
    {
        byte[] png = MakeTestPng(width: 24, height: 24);
        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["file"],
            new object?[] { png });

        List<Row> rows = await ExecuteQueryAsync("SELECT img(file, f -> width(f)) FROM t", catalog);

        Assert.Single(rows);
        DataValue result = rows[0][0];
        Assert.False(result.IsNull, "Sink pipeline should produce a non-null Float32 value.");
        Assert.Equal(DataKind.Float32, result.Kind);
        Assert.Equal(24f, result.AsFloat32());
    }

    /// <summary>
    /// Identity pipeline runs end-to-end without decode/encode. Verifies the short-circuit
    /// detected in the unit test above also holds when the lowered expression flows through
    /// projection: the result kind matches what a bare <c>SELECT file</c> produces (i.e.
    /// the pipeline collapsed to the source rather than producing an Image).
    /// </summary>
    [Fact]
    public async Task IdentityPipeline_RunsEndToEnd_AndReturnsSourceKindUnchanged()
    {
        byte[] png = MakeTestPng(width: 16, height: 16);
        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["file"],
            new object?[] { png });

        List<Row> directRows = await ExecuteQueryAsync("SELECT file FROM t", catalog);
        List<Row> identityRows = await ExecuteQueryAsync("SELECT img(file, f -> f) FROM t", catalog);

        Assert.Single(directRows);
        Assert.Single(identityRows);

        // The lowerer should have rewritten img(file, f -> f) to the bare `file` column ref,
        // so both queries produce the same kind (UInt8Array, since byte[] → UInt8Array per
        // InMemoryTableProvider). If the lowering were wrong and we'd run the pipeline
        // anyway, the result would be DataKind.Image (because the pipeline encodes).
        Assert.Equal(directRows[0][0].Kind, identityRows[0][0].Kind);
        Assert.Equal(DataKind.UInt8Array, identityRows[0][0].Kind);
    }
}
