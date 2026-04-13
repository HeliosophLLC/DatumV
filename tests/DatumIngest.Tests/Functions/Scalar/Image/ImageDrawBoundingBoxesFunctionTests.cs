using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Image;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Pooling;
using SkiaSharp;

namespace DatumIngest.Tests.Functions.Scalar.Image;

/// <summary>
/// Tests for <see cref="ImageDrawBoundingBoxesFunction"/>. The function is the
/// canary for the Phase 1 Array&lt;Struct&gt;-into-function plumbing — happy path
/// proves that a struct array column lifts cleanly into a function ValueRef
/// argument, recursive struct field access works, and field-name resolution
/// via the per-query <see cref="TypeRegistry"/> picks the right indices.
/// </summary>
public sealed class ImageDrawBoundingBoxesFunctionTests : ServiceTestBase
{
    [Fact]
    public void Metadata_ExposesNameCategoryAndDescription()
    {
        Assert.Equal("image_draw_bounding_boxes", ImageDrawBoundingBoxesFunction.Name);
        Assert.Equal(FunctionCategory.Image, ImageDrawBoundingBoxesFunction.Category);
        Assert.False(string.IsNullOrWhiteSpace(ImageDrawBoundingBoxesFunction.Description));
    }

    [Fact]
    public void Validate_AcceptsImageAndStructArray()
    {
        DataKind kind = new ImageDrawBoundingBoxesFunction()
            .ValidateArguments([DataKind.Image, DataKind.Struct]);
        Assert.Equal(DataKind.Image, kind);
    }

    [Fact]
    public async Task Execute_DrawsBoxFromYoloShapedDetection_ReturnsSameDimensionImage()
    {
        // Build a 100×100 white SKBitmap as the source image. Round-tripping
        // through PNG keeps the exact shape verifiable.
        using SKBitmap source = MakeWhiteBitmap(100, 100);
        ValueRef imgArg = ValueRef.FromImage(source);

        // YOLO-shaped detection struct: label, score, x, y, w, h.
        TypeRegistry registry = new();
        int detectionTypeId = registry.InternStructType(
        [
            new StructFieldDescriptor("label", registry.InternScalarType(DataKind.String)),
            new StructFieldDescriptor("score", registry.InternScalarType(DataKind.Float32)),
            new StructFieldDescriptor("x", registry.InternScalarType(DataKind.Float32)),
            new StructFieldDescriptor("y", registry.InternScalarType(DataKind.Float32)),
            new StructFieldDescriptor("w", registry.InternScalarType(DataKind.Float32)),
            new StructFieldDescriptor("h", registry.InternScalarType(DataKind.Float32)),
        ]);

        ValueRef[] fields =
        [
            ValueRef.FromString("cat"),
            ValueRef.FromFloat32(0.94f),
            ValueRef.FromFloat32(10f),
            ValueRef.FromFloat32(20f),
            ValueRef.FromFloat32(40f),
            ValueRef.FromFloat32(30f),
        ];
        ValueRef boxesArg = ValueRef.FromArray(
            DataKind.Struct,
            [ValueRef.FromStruct(fields, (ushort)detectionTypeId)]);

        EvaluationFrame frame = MakeFrame(registry);

        ValueRef result = await new ImageDrawBoundingBoxesFunction()
            .ExecuteAsync(new ValueRef[] { imgArg, boxesArg }, frame, default);

        Assert.False(result.IsNull);
        Assert.Equal(DataKind.Image, result.Kind);

        SKBitmap drawn = result.AsImage();
        Assert.Equal(100, drawn.Width);
        Assert.Equal(100, drawn.Height);

        // The output must differ from the all-white source — drawing a red box
        // should change *some* pixel inside the box's footprint.
        Assert.True(HasNonWhitePixel(drawn, 10, 20, 40, 30),
            "expected at least one non-white pixel inside the drawn box's footprint.");
    }

    [Fact]
    public async Task Execute_EmptyBoxesArray_PassesImageThrough()
    {
        using SKBitmap source = MakeWhiteBitmap(50, 50);
        ValueRef imgArg = ValueRef.FromImage(source);
        ValueRef boxesArg = ValueRef.FromArray(DataKind.Struct, Array.Empty<ValueRef>());

        TypeRegistry registry = new();
        EvaluationFrame frame = MakeFrame(registry);

        ValueRef result = await new ImageDrawBoundingBoxesFunction()
            .ExecuteAsync(new ValueRef[] { imgArg, boxesArg }, frame, default);

        SKBitmap drawn = result.AsImage();
        Assert.Equal(50, drawn.Width);
        Assert.Equal(50, drawn.Height);
        // No boxes drawn — no non-white pixels anywhere.
        Assert.False(HasNonWhitePixel(drawn, 0, 0, 50, 50),
            "expected the image to be unchanged when boxes is empty.");
    }

    [Fact]
    public async Task Execute_NullImage_ReturnsNullImage()
    {
        ValueRef boxesArg = ValueRef.FromArray(DataKind.Struct, Array.Empty<ValueRef>());
        EvaluationFrame frame = MakeFrame(new TypeRegistry());

        ValueRef result = await new ImageDrawBoundingBoxesFunction()
            .ExecuteAsync(
                new ValueRef[] { ValueRef.Null(DataKind.Image), boxesArg },
                frame, default);

        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Image, result.Kind);
    }

    [Fact]
    public async Task Execute_StructMissingRequiredField_ThrowsHelpfulError()
    {
        // Struct with only x, y, w (no h) — must throw with the field list in
        // the message so the caller can see what they actually emitted.
        using SKBitmap source = MakeWhiteBitmap(50, 50);
        ValueRef imgArg = ValueRef.FromImage(source);

        TypeRegistry registry = new();
        int typeId = registry.InternStructType(
        [
            new StructFieldDescriptor("x", registry.InternScalarType(DataKind.Float32)),
            new StructFieldDescriptor("y", registry.InternScalarType(DataKind.Float32)),
            new StructFieldDescriptor("w", registry.InternScalarType(DataKind.Float32)),
        ]);

        ValueRef[] fields =
        [
            ValueRef.FromFloat32(0f), ValueRef.FromFloat32(0f), ValueRef.FromFloat32(10f),
        ];
        ValueRef boxesArg = ValueRef.FromArray(
            DataKind.Struct, [ValueRef.FromStruct(fields, (ushort)typeId)]);

        EvaluationFrame frame = MakeFrame(registry);

        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await new ImageDrawBoundingBoxesFunction()
                .ExecuteAsync(new ValueRef[] { imgArg, boxesArg }, frame, default));

        Assert.Contains("x, y, w, h", ex.Message);
        Assert.Contains("[x, y, w]", ex.Message);
    }

    /// <summary>
    /// Allocates a minimal opaque white <see cref="SKBitmap"/> for use as a
    /// canvas in the tests. Caller owns disposal.
    /// </summary>
    private static SKBitmap MakeWhiteBitmap(int width, int height)
    {
        SKBitmap bmp = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using SKCanvas canvas = new(bmp);
        canvas.Clear(SKColors.White);
        return bmp;
    }

    /// <summary>
    /// Returns true when at least one pixel in the given rectangle differs
    /// from pure white. Used as a quick "did anything draw?" check without
    /// requiring exact pixel coordinates that depend on stroke width.
    /// </summary>
    private static bool HasNonWhitePixel(SKBitmap bmp, int x, int y, int w, int h)
    {
        int xMax = Math.Min(bmp.Width, x + w);
        int yMax = Math.Min(bmp.Height, y + h);
        for (int j = Math.Max(0, y); j < yMax; j++)
        {
            for (int i = Math.Max(0, x); i < xMax; i++)
            {
                SKColor c = bmp.GetPixel(i, j);
                if (c.Red != 255 || c.Green != 255 || c.Blue != 255) return true;
            }
        }
        return false;
    }

    private EvaluationFrame MakeFrame(TypeRegistry registry)
    {
        DatumIngest.Execution.ExecutionContext context = CreateExecutionContext(typeRegistry: registry);
        return CreateEvaluationFrame(context);
    }
}
