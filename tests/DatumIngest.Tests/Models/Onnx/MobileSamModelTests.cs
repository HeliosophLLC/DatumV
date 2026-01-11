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
[Trait("Category", "Gpu")]
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
    /// Synthetic round-trip for the <c>Array&lt;Image&gt;</c> output shape
    /// MobileSAM-everything produces: build the same managed-memory
    /// ValueRef the model emits (an array of <see cref="ValueRef.FromImage"/>
    /// elements), materialise it into an arena via the same path the
    /// operator's scatter step uses
    /// (<see cref="DatumIngest.Functions.ValueRef.ToDataValue"/>), then
    /// read it back via <see cref="DataValue.AsImageArray"/> and decode
    /// each PNG. This is the integration step that the model-level smoke
    /// tests bypass — they assert against the raw <c>ValueRef</c> without
    /// ever crossing the arena boundary, so a regression in
    /// <c>BuildImageArray</c> / <c>FromImageArray</c> wouldn't surface
    /// from the MobileSAM tests alone. No ONNX files required; runs on
    /// every CI machine.
    /// </summary>
    [Fact]
    public void ArrayOfImages_ToDataValue_AsImageArray_RoundTrips()
    {
        Pool pool = GetService<Pool>();
        Arena arena = pool.Backing.RentArena();
        try
        {
            // 3 distinct synthesised masks at different dimensions —
            // verifies the round-trip preserves each element's pixel data,
            // not just element count.
            (int W, int H, SKColor Fill)[] specs =
            [
                (10, 10, SKColors.White),
                (40, 25, SKColors.Red),
                (8, 50, SKColors.LimeGreen),
            ];

            DatumIngest.Functions.ValueRef[] elements = new DatumIngest.Functions.ValueRef[specs.Length];
            for (int i = 0; i < specs.Length; i++)
            {
                SKBitmap bmp = new(
                    specs[i].W, specs[i].H,
                    SKColorType.Rgba8888, SKAlphaType.Opaque);
                bmp.Erase(specs[i].Fill);
                elements[i] = DatumIngest.Functions.ValueRef.FromImage(bmp);
            }

            DatumIngest.Functions.ValueRef arrayRef =
                DatumIngest.Functions.ValueRef.FromArray(DataKind.Image, elements);
            Assert.True(arrayRef.IsArray);
            Assert.Equal(DataKind.Image, arrayRef.Kind);

            // Cross the arena boundary — same call the operator's scatter
            // step makes when materialising a model output into the
            // output batch.
            DataValue dv = arrayRef.ToDataValue(arena);
            Assert.True(dv.IsArray);
            Assert.Equal(DataKind.Image, dv.Kind);

            // Read each element's encoded bytes back out of the arena and
            // decode. PNG encode happens inside FromImageArray; decode
            // here verifies the bytes are well-formed and the dims match.
            byte[][] pngBytes = dv.AsImageArray(arena);
            Assert.Equal(specs.Length, pngBytes.Length);

            for (int i = 0; i < specs.Length; i++)
            {
                Assert.NotNull(pngBytes[i]);
                Assert.True(pngBytes[i].Length > 0,
                    $"element[{i}] arena bytes are empty — encode produced nothing.");

                using SKBitmap decoded = SKBitmap.Decode(pngBytes[i]);
                Assert.NotNull(decoded);
                Assert.Equal(specs[i].W, decoded.Width);
                Assert.Equal(specs[i].H, decoded.Height);

                // Sample one pixel to confirm we got the original content
                // back, not zeros or some other element's pixels.
                SKColor sampled = decoded.GetPixel(specs[i].W / 2, specs[i].H / 2);
                Assert.Equal(specs[i].Fill.Red, sampled.Red);
                Assert.Equal(specs[i].Fill.Green, sampled.Green);
                Assert.Equal(specs[i].Fill.Blue, sampled.Blue);
            }
        }
        finally
        {
            pool.Backing.TryReturn(arena);
        }
    }

    /// <summary>
    /// Renders a SAM-friendly multi-object scene: gradient sky background
    /// plus a red ellipse and a blue rectangle in different regions. Used
    /// to give the everything-mode segmenter at least two clearly distinct
    /// objects to find, which the post-NMS survivor count assertion relies
    /// on.
    /// </summary>
    private static byte[] MakeMultiObjectPng(int canvasW, int canvasH)
    {
        using SKBitmap bitmap = new(canvasW, canvasH, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using SKCanvas c = new(bitmap);
        using (SKPaint bg = new())
        {
            using SKShader shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(0, canvasH),
                [new SKColor(40, 80, 160), new SKColor(180, 200, 230)],
                SKShaderTileMode.Clamp);
            bg.Shader = shader;
            c.DrawRect(new SKRect(0, 0, canvasW, canvasH), bg);
        }
        using (SKPaint redOval = new() { Color = SKColors.Red, IsAntialias = true })
        {
            c.DrawOval(new SKRect(40, 40, 120, 120), redOval);
        }
        using (SKPaint blueRect = new() { Color = SKColors.Yellow, IsAntialias = true })
        {
            c.DrawRect(new SKRect(180, 130, 280, 200), blueRect);
        }
        c.Flush();
        using SKData encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    /// <summary>
    /// Everything-mode signature: declares only an Image input, returns
    /// Array&lt;Image&gt; (Kind=Image with IsArray set). Optional gridSize
    /// override lives in the catalog entry, not in <c>InputKinds</c>.
    /// </summary>
    [Fact]
    public void Load_EverythingMode_ExposesExpectedSignature()
    {
        string encoderPath = ResolvePath(BuiltinModels.MobileSamEncoderFilename);
        string decoderPath = ResolvePath(BuiltinModels.MobileSamMaskDecoderMultiFilename);
        if (!File.Exists(encoderPath) || !File.Exists(decoderPath)) return;

        using MobileSamModel model = new(
            "mobilesam_everything", encoderPath, decoderPath, MobileSamMode.Everything, defaultGridSize: 8);

        Assert.Equal(MobileSamMode.Everything, model.Mode);
        Assert.Equal(8, model.DefaultGridSize);
        Assert.True(model.IsDeterministic);
        Assert.Equal(DataKind.Image, model.OutputKind);
        Assert.Single(model.InputKinds);
        Assert.Equal(DataKind.Image, model.InputKinds[0]);
    }

    /// <summary>
    /// End-to-end on a multi-object scene: feed the model a small grid
    /// (8×8 = 64 prompts) and verify the result is an <c>Array&lt;Image&gt;</c>
    /// with at least two survivor masks (one per shape), each sized to the
    /// input. The grid is small to keep the test under a few seconds; the
    /// shapes are large enough relative to a 320×240 canvas that even a
    /// coarse grid lands prompts on each one.
    /// </summary>
    [Fact]
    public async Task InferBatch_EverythingMode_ReturnsArrayOfMasks()
    {
        string encoderPath = ResolvePath(BuiltinModels.MobileSamEncoderFilename);
        string decoderPath = ResolvePath(BuiltinModels.MobileSamMaskDecoderMultiFilename);
        if (!File.Exists(encoderPath) || !File.Exists(decoderPath)) return;

        const int CanvasW = 320;
        const int CanvasH = 240;

        using MobileSamModel model = new(
            "mobilesam_everything", encoderPath, decoderPath, MobileSamMode.Everything, defaultGridSize: 8);

        byte[] png = MakeMultiObjectPng(CanvasW, CanvasH);

        DatumIngest.Functions.ValueRef[][] inputs =
            [[DatumIngest.Functions.ValueRef.FromBytes(DataKind.Image, png)]];
        DatumIngest.Functions.ValueRef[][] overrides = [[]];
        IReadOnlyList<DatumIngest.Functions.ValueRef> outputs = await model.InferBatchAsync(
            inputs, overrides, CancellationToken.None);

        DatumIngest.Functions.ValueRef result = Assert.Single(outputs);
        Assert.False(result.IsNull);
        Assert.Equal(DataKind.Image, result.Kind);
        Assert.True(result.IsArray, "Everything mode must return an Array<Image>.");

        ReadOnlySpan<DatumIngest.Functions.ValueRef> masks = result.GetArrayElements();
        Assert.True(masks.Length >= 2,
            $"Expected ≥ 2 survivor masks for a two-shape scene, got {masks.Length}.");

        // Every survivor must be a non-null Image at the input's dims —
        // catches a regression where a future export drops the resize-back op.
        for (int i = 0; i < masks.Length; i++)
        {
            Assert.False(masks[i].IsNull, $"mask[{i}] is null");
            Assert.Equal(DataKind.Image, masks[i].Kind);
            SKBitmap mask = masks[i].AsImage();
            Assert.Equal(CanvasW, mask.Width);
            Assert.Equal(CanvasH, mask.Height);
        }
    }

    /// <summary>
    /// Per-call <c>gridSize</c> override flows through the catalog's
    /// optional-arg slot to <c>MobileSamModel.ResolveGridSize</c>. Using
    /// a tiny grid (4) here keeps the test fast — at most 16 decoder
    /// dispatches on a synthetic image — while still exercising the
    /// override-resolution path end-to-end.
    /// </summary>
    [Fact]
    public async Task InferBatch_EverythingMode_HonoursPerRowGridSizeOverride()
    {
        string encoderPath = ResolvePath(BuiltinModels.MobileSamEncoderFilename);
        string decoderPath = ResolvePath(BuiltinModels.MobileSamMaskDecoderMultiFilename);
        if (!File.Exists(encoderPath) || !File.Exists(decoderPath)) return;

        // defaultGridSize=32 (the registration default); the override
        // below shrinks it to 4 for this row only. If the override path
        // is broken, the test would hit 32×32 = 1024 dispatches and time
        // out / be very slow.
        using MobileSamModel model = new(
            "mobilesam_everything", encoderPath, decoderPath, MobileSamMode.Everything, defaultGridSize: 32);

        byte[] png = MakeMultiObjectPng(320, 240);

        DatumIngest.Functions.ValueRef[][] inputs =
            [[DatumIngest.Functions.ValueRef.FromBytes(DataKind.Image, png)]];
        DatumIngest.Functions.ValueRef[][] overrides =
            [[DatumIngest.Functions.ValueRef.FromInt32(4)]];

        DateTime start = DateTime.UtcNow;
        IReadOnlyList<DatumIngest.Functions.ValueRef> outputs = await model.InferBatchAsync(
            inputs, overrides, CancellationToken.None);
        TimeSpan elapsed = DateTime.UtcNow - start;

        Assert.Single(outputs);
        Assert.True(outputs[0].IsArray);
        // 4×4=16 decoder calls on a small image must finish well inside
        // the wall-clock window a 32×32 sweep would take. Anything under
        // ~5s is comfortably the 4-grid path; a 32-grid would take far
        // longer. (The cap is intentionally generous so first-load or
        // CPU-only environments don't false-positive.)
        Assert.True(elapsed < TimeSpan.FromSeconds(15),
            $"4×4 grid took {elapsed.TotalSeconds:F1}s — looks like the override didn't apply.");
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

    /// <summary>
    /// Catalog round-trip for the everything-mode registration: resolves
    /// to a <see cref="MobileSamModel"/> in <see cref="MobileSamMode.Everything"/>
    /// with one Image input + a single Int32 optional <c>gridSize</c>.
    /// </summary>
    [Fact]
    public void Catalog_RegisterAndResolve_EverythingMode_YieldsMobileSamModel()
    {
        string encoderPath = ResolvePath(BuiltinModels.MobileSamEncoderFilename);
        string decoderPath = ResolvePath(BuiltinModels.MobileSamMaskDecoderMultiFilename);
        if (!File.Exists(encoderPath) || !File.Exists(decoderPath)) return;

        ModelCatalog catalog = new(modelDirectory: ModelCatalog.DefaultModelDirectory);
        BuiltinModels.RegisterMobileSam(catalog);

        ModelCatalogEntry? entry = catalog.TryGetEntry("mobilesam");
        Assert.NotNull(entry);
        Assert.Equal("onnx", entry!.Backend);
        Assert.Equal(BuiltinModels.MobileSamEncoderFilename, entry.RelativePath);
        Assert.Equal(DataKind.Image, entry.OutputKind);
        Assert.Single(entry.InputKinds);
        Assert.Equal(DataKind.Image, entry.InputKinds[0]);
        Assert.NotNull(entry.OptionalArgKinds);
        Assert.Single(entry.OptionalArgKinds!);
        Assert.Equal(DataKind.Int32, entry.OptionalArgKinds![0]);

        using ModelLease lease = catalog.ResolveLeaseSynchronously("mobilesam");
        IModel model = lease.Model;
        MobileSamModel sam = Assert.IsType<MobileSamModel>(model);
        Assert.Equal(MobileSamMode.Everything, sam.Mode);
        Assert.Equal(32, sam.DefaultGridSize);
    }
}
