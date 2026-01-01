namespace DatumIngest.Tests.Models.Onnx;

using DatumIngest.Catalog;
using DatumIngest.Model;
using DatumIngest.Models;
using DatumIngest.Models.Onnx;
using DatumIngest.Pooling;

using SkiaSharp;

/// <summary>
/// Smoke + behaviour tests for <see cref="MobileSamModel"/> (prompted
/// segmentation). Self-skips when the encoder or decoder ONNX file is
/// absent so CI machines without the artefacts don't fail.
/// </summary>
public sealed class MobileSamModelTests : ServiceTestBase
{
    public static IEnumerable<object[]> Variants() =>
    [
        // Multi-mask decoder (default registration). Argmax over the
        // candidate masks picks the highest-IoU one.
        ["mobilesam_prompted_multi", BuiltinModels.MobileSamMaskDecoderMultiFilename],
        // Single-mask decoder. argmax-of-1 picks the only mask.
        ["mobilesam_prompted_single", BuiltinModels.MobileSamMaskDecoderSingleFilename],
    ];

    private static string ResolvePath(string filename) =>
        Path.Combine(ModelCatalog.DefaultModelDirectory, filename);

    private static byte[] MakeSolidPng(int width, int height, SKColor color)
    {
        using SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(color);
        using SKData encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    /// <summary>
    /// Renders <paramref name="canvasW"/>×<paramref name="canvasH"/> dark
    /// canvas with one white axis-aligned rectangle at (rectX, rectY,
    /// rectW, rectH). Used to give the segmenter a synthetic but
    /// well-defined object to find.
    /// </summary>
    private static byte[] MakeRectanglePng(
        int canvasW, int canvasH,
        int rectX, int rectY, int rectW, int rectH)
    {
        using SKBitmap bitmap = new(canvasW, canvasH, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(SKColors.Black);
        using SKCanvas canvas = new(bitmap);
        using SKPaint paint = new() { Color = SKColors.White, IsAntialias = false };
        canvas.DrawRect(new SKRect(rectX, rectY, rectX + rectW, rectY + rectH), paint);
        canvas.Flush();
        using SKData encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    /// <summary>
    /// Cheapest signal: the encoder + decoder load and the model declares
    /// the expected three-input signature (Image + Float64 + Float64) →
    /// Image, deterministic.
    /// </summary>
    [Theory]
    [MemberData(nameof(Variants))]
    public void Load_ExposesExpectedSignature(string modelName, string decoderFilename)
    {
        string encoderPath = ResolvePath(BuiltinModels.MobileSamEncoderFilename);
        string decoderPath = ResolvePath(decoderFilename);
        if (!File.Exists(encoderPath) || !File.Exists(decoderPath)) return;

        using MobileSamModel model = new(modelName, encoderPath, decoderPath);

        Assert.Equal(modelName, model.Name);
        Assert.True(model.IsDeterministic);
        Assert.Equal(DataKind.Image, model.OutputKind);
        Assert.Equal(3, model.InputKinds.Count);
        Assert.Equal(DataKind.Image, model.InputKinds[0]);
        Assert.Equal(DataKind.Float64, model.InputKinds[1]);
        Assert.Equal(DataKind.Float64, model.InputKinds[2]);
    }

    /// <summary>
    /// End-to-end: feed a non-square solid-colour PNG with a centre prompt
    /// through the model and verify the result is a same-sized image
    /// (mask resized back to input dims by the decoder's trailing Resize op).
    /// </summary>
    [Theory]
    [MemberData(nameof(Variants))]
    public async Task InferBatch_PreservesInputDimensions(string modelName, string decoderFilename)
    {
        string encoderPath = ResolvePath(BuiltinModels.MobileSamEncoderFilename);
        string decoderPath = ResolvePath(decoderFilename);
        if (!File.Exists(encoderPath) || !File.Exists(decoderPath)) return;

        Pool pool = GetService<Pool>();
        Arena inputArena = pool.Backing.RentArena();
        Arena targetArena = pool.Backing.RentArena();

        try
        {
            using MobileSamModel model = new(modelName, encoderPath, decoderPath);

            byte[] png = MakeSolidPng(200, 150, SKColors.SteelBlue);

            DatumIngest.Functions.ValueRef[][] inputs =
            [
                [
                    DatumIngest.Functions.ValueRef.FromBytes(DataKind.Image, png),
                    DatumIngest.Functions.ValueRef.FromFloat64(100.0),
                    DatumIngest.Functions.ValueRef.FromFloat64(75.0),
                ],
            ];
            DatumIngest.Functions.ValueRef[][] overrides = [[]];
            IReadOnlyList<DatumIngest.Functions.ValueRef> outputs = await model.InferBatchAsync(
                inputs,
                overrides,
                cancellationToken: CancellationToken.None);

            DatumIngest.Functions.ValueRef result = Assert.Single(outputs);
            Assert.False(result.IsNull);
            Assert.Equal(DataKind.Image, result.Kind);

            SKBitmap mask = result.AsImage();
            Assert.NotNull(mask);
            Assert.Equal(200, mask.Width);
            Assert.Equal(150, mask.Height);
        }
        finally
        {
            pool.Backing.TryReturn(inputArena);
            pool.Backing.TryReturn(targetArena);
        }
    }

    /// <summary>
    /// Behavioural sanity: prompt at the centre of a bright rectangle on a
    /// dark canvas; the returned mask should be non-empty and cover most
    /// of the rectangle while staying mostly inside its bounds. This
    /// validates that the prompt coordinate is being interpreted in the
    /// correct space (original-image pixels, top-left origin) and that
    /// the IoU-argmax picks a mask that actually corresponds to the prompt.
    /// </summary>
    [Theory]
    [MemberData(nameof(Variants))]
    public async Task InferBatch_MaskConcentratesAtPromptedObject(string modelName, string decoderFilename)
    {
        string encoderPath = ResolvePath(BuiltinModels.MobileSamEncoderFilename);
        string decoderPath = ResolvePath(decoderFilename);
        if (!File.Exists(encoderPath) || !File.Exists(decoderPath)) return;

        const int CanvasW = 320;
        const int CanvasH = 240;
        const int RectX = 120;
        const int RectY = 80;
        const int RectW = 80;
        const int RectH = 80;
        const double PromptX = RectX + RectW / 2.0;
        const double PromptY = RectY + RectH / 2.0;

        using MobileSamModel model = new(modelName, encoderPath, decoderPath);
        byte[] png = MakeRectanglePng(CanvasW, CanvasH, RectX, RectY, RectW, RectH);

        DatumIngest.Functions.ValueRef[][] inputs =
        [
            [
                DatumIngest.Functions.ValueRef.FromBytes(DataKind.Image, png),
                DatumIngest.Functions.ValueRef.FromFloat64(PromptX),
                DatumIngest.Functions.ValueRef.FromFloat64(PromptY),
            ],
        ];
        DatumIngest.Functions.ValueRef[][] overrides = [[]];
        IReadOnlyList<DatumIngest.Functions.ValueRef> outputs = await model.InferBatchAsync(
            inputs, overrides, CancellationToken.None);

        SKBitmap mask = outputs[0].AsImage();
        Assert.Equal(CanvasW, mask.Width);
        Assert.Equal(CanvasH, mask.Height);

        // Count foreground pixels overall and inside the rectangle bounds.
        // SAM's training is centred on natural-image semantics; on a hard-
        // edged synthetic rectangle the model is unusually well-behaved,
        // so we can demand tight containment without flaking.
        int totalForeground = 0;
        int foregroundInsideRect = 0;
        for (int y = 0; y < CanvasH; y++)
        {
            for (int x = 0; x < CanvasW; x++)
            {
                if (mask.GetPixel(x, y).Red > 127)
                {
                    totalForeground++;
                    if (x >= RectX && x < RectX + RectW
                        && y >= RectY && y < RectY + RectH)
                    {
                        foregroundInsideRect++;
                    }
                }
            }
        }

        int rectArea = RectW * RectH;
        Assert.True(totalForeground > 0,
            "MobileSAM returned a fully-empty mask for a clearly-defined prompted object.");

        // ≥80 % of the rectangle's interior should be marked foreground.
        double rectCoverage = foregroundInsideRect / (double)rectArea;
        Assert.True(rectCoverage > 0.80,
            $"Mask covers only {rectCoverage:P0} of the prompted rectangle; expected >80 %.");

        // ≥90 % of the foreground pixels should fall inside the rectangle —
        // i.e. the mask isn't running away to other regions.
        double containment = foregroundInsideRect / (double)totalForeground;
        Assert.True(containment > 0.90,
            $"Only {containment:P0} of the mask lies inside the prompted rectangle; expected >90 %.");
    }

    /// <summary>
    /// Catalog round-trip: registering MobileSAM via <see cref="BuiltinModels"/>
    /// resolves to a usable <see cref="MobileSamModel"/>.
    /// </summary>
    [Fact]
    public void Catalog_RegisterAndResolve_YieldsMobileSamModel()
    {
        string encoderPath = ResolvePath(BuiltinModels.MobileSamEncoderFilename);
        string decoderPath = ResolvePath(BuiltinModels.MobileSamMaskDecoderMultiFilename);
        if (!File.Exists(encoderPath) || !File.Exists(decoderPath)) return;

        ModelCatalog catalog = new(modelDirectory: ModelCatalog.DefaultModelDirectory);
        BuiltinModels.RegisterMobileSamPrompted(catalog);

        ModelCatalogEntry? entry = catalog.TryGetEntry("mobilesam_prompted");
        Assert.NotNull(entry);
        Assert.Equal("onnx", entry!.Backend);
        Assert.Equal(BuiltinModels.MobileSamEncoderFilename, entry.RelativePath);
        Assert.Equal(DataKind.Image, entry.OutputKind);
        Assert.Equal(3, entry.InputKinds.Count);

        using ModelLease lease = catalog.ResolveLeaseSynchronously("mobilesam_prompted");
        IModel model = lease.Model;
        Assert.IsType<MobileSamModel>(model);
        Assert.Equal(DataKind.Image, model.OutputKind);
        Assert.Equal(3, model.InputKinds.Count);
        Assert.Equal(DataKind.Image, model.InputKinds[0]);
        Assert.Equal(DataKind.Float64, model.InputKinds[1]);
        Assert.Equal(DataKind.Float64, model.InputKinds[2]);
    }
}
