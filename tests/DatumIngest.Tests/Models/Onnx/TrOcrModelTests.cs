namespace DatumIngest.Tests.Models.Onnx;

using DatumIngest.Catalog;
using DatumIngest.Model;
using DatumIngest.Models;
using DatumIngest.Models.Onnx;
using DatumIngest.Pooling;

using SkiaSharp;

/// <summary>
/// Smoke tests for the TrOCR printed-text OCR model. Self-skip when the
/// model folder is absent so CI machines without the converted ONNX
/// artefacts don't fail.
/// </summary>
public sealed class TrOcrModelTests : ServiceTestBase
{
    private static string Fp32EncoderPath => Path.Combine(
        ModelCatalog.DefaultModelDirectory, BuiltinModels.TrOcrPrintedEncoderRelativePath);

    private static string Fp16EncoderPath => Path.Combine(
        ModelCatalog.DefaultModelDirectory, BuiltinModels.TrOcrPrintedFp16EncoderRelativePath);

    private static bool Fp32Available => File.Exists(Fp32EncoderPath);
    private static bool Fp16Available => File.Exists(Fp16EncoderPath);

    // Fp16 file-present is treated the same as fp32: tests self-skip
    // when the file isn't installed (CI machines), but if the file is
    // installed and ORT can't load it, that's a real test failure —
    // we WANT it visible. The earlier wrap-in-try/catch made bad-file
    // failures invisible (silent xUnit "passed" with no assertions
    // ever running), which masked an actual broken patcher for hours.

    /// <summary>
    /// Renders a single line of black text on a white background. TrOCR
    /// was trained on document line images, so this matches the expected
    /// input distribution well.
    /// </summary>
    private static byte[] MakeTextPng(string text, int width = 384, int height = 96)
    {
        using SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using SKCanvas canvas = new(bitmap);
        canvas.Clear(SKColors.White);

        using SKTypeface typeface = SKTypeface.FromFamilyName(
            "Arial",
            SKFontStyleWeight.Normal,
            SKFontStyleWidth.Normal,
            SKFontStyleSlant.Upright);
        using SKFont font = new(typeface, size: 48f);
        using SKPaint paint = new()
        {
            Color = SKColors.Black,
            IsAntialias = true,
        };

        SKRect bounds = new();
        font.MeasureText(text, out bounds);
        float x = (width - bounds.Width) / 2f - bounds.Left;
        float y = (height + bounds.Height) / 2f - bounds.Bottom;
        canvas.DrawText(text, x, y, SKTextAlign.Left, font, paint);

        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    /// <summary>
    /// Cheapest signal: encoder + merged decoder + tokenizer all load and
    /// the model exposes the expected catalog signature. Catches missing
    /// or malformed files without exercising generation.
    /// </summary>
    [Fact]
    public void Load_RealTrOcrFp32_ExposesExpectedSignature()
    {
        if (!Fp32Available) return;

        using TrOcrModel model = new(name: "trocr_printed", encoderModelFilePath: Fp32EncoderPath);

        Assert.Equal("trocr_printed", model.Name);
        Assert.True(model.IsDeterministic);
        Assert.Equal(DataKind.String, model.OutputKind);
        Assert.Single(model.InputKinds);
        Assert.Equal(DataKind.Image, model.InputKinds[0]);
        Assert.True(model.MaxTokens > 0);
    }

    /// <summary>
    /// fp16 variant loads the same way, just from a different decoder
    /// file name. Confirms the dtype-detect path doesn't reject fp16
    /// metadata.
    /// </summary>
    [Fact]
    public void Load_RealTrOcrFp16_ExposesExpectedSignature()
    {
        if (!Fp16Available) return;

        using TrOcrModel model = new(
            name: "trocr_printed_fp16",
            encoderModelFilePath: Fp16EncoderPath,
            decoderFileName: "decoder_model_merged_fp16.onnx");

        Assert.Equal("trocr_printed_fp16", model.Name);
        Assert.Equal(DataKind.String, model.OutputKind);
    }

    /// <summary>
    /// End-to-end on a synthetic text image: confirms encoder → KV-cache
    /// decoder loop → tokenizer → byte-level BPE inverse all run cleanly.
    /// We don't pin the exact transcription — TrOCR was trained on real
    /// document scans and SkiaSharp-rendered text is out-of-distribution,
    /// so it tends to produce plausible-looking but wrong words. The
    /// strong signal here is "non-empty, well-formed text, no raw BPE
    /// mojibake (Ġ/Ċ)" — that proves the pipeline is wired up.
    /// </summary>
    [Fact]
    public async Task InferBatch_RenderedText_ReturnsWellFormedTranscription()
    {
        if (!Fp32Available) return;

        Pool pool = GetService<Pool>();
        Arena inputArena = pool.Backing.RentArena();
        Arena targetArena = pool.Backing.RentArena();

        try
        {
            using TrOcrModel model = new(name: "trocr_printed", encoderModelFilePath: Fp32EncoderPath);

            byte[] png = MakeTextPng("HELLO");

            DatumIngest.Functions.ValueRef[][] inputs =
                [[DatumIngest.Functions.ValueRef.FromBytes(DataKind.Image, png)]];
            IReadOnlyList<DatumIngest.Functions.ValueRef> outputs = await model.InferBatchAsync(
                inputs,
                overrides: [],
                cancellationToken: CancellationToken.None);

            DatumIngest.Functions.ValueRef result = Assert.Single(outputs);
            Assert.False(result.IsNull);
            string text = result.AsString();
            Assert.False(string.IsNullOrWhiteSpace(text), "TrOCR returned empty output.");
            Assert.DoesNotContain('Ġ', text);  // Ġ — encoded space
            Assert.DoesNotContain('Ċ', text);  // Ċ — encoded newline
        }
        finally
        {
            pool.Backing.TryReturn(inputArena);
            pool.Backing.TryReturn(targetArena);
        }
    }

    /// <summary>
    /// Multi-image batch: confirms the encoder packs N images correctly
    /// and the per-row decoder loop preserves independence (no KV-cache
    /// bleed across rows).
    /// </summary>
    [Fact]
    public async Task InferBatch_MultipleImages_ReturnsOneTranscriptionPerRow()
    {
        if (!Fp32Available) return;

        Pool pool = GetService<Pool>();
        Arena inputArena = pool.Backing.RentArena();
        Arena targetArena = pool.Backing.RentArena();

        try
        {
            using TrOcrModel model = new(name: "trocr_printed", encoderModelFilePath: Fp32EncoderPath);

            DatumIngest.Functions.ValueRef[][] inputs =
            [
                [DatumIngest.Functions.ValueRef.FromBytes(DataKind.Image, MakeTextPng("HELLO"))],
                [DatumIngest.Functions.ValueRef.FromBytes(DataKind.Image, MakeTextPng("WORLD"))],
                [DatumIngest.Functions.ValueRef.FromBytes(DataKind.Image, MakeTextPng("123"))],
            ];

            IReadOnlyList<DatumIngest.Functions.ValueRef> outputs = await model.InferBatchAsync(
                inputs, overrides: [], cancellationToken: CancellationToken.None);

            Assert.Equal(3, outputs.Count);
            for (int i = 0; i < outputs.Count; i++)
            {
                string text = outputs[i].AsString();
                Assert.False(string.IsNullOrWhiteSpace(text), $"row {i} returned empty text");
                Assert.DoesNotContain('Ġ', text);
                Assert.DoesNotContain('Ċ', text);
            }
        }
        finally
        {
            pool.Backing.TryReturn(inputArena);
            pool.Backing.TryReturn(targetArena);
        }
    }

    /// <summary>
    /// Real-document smoke test: the bundled receipt fixture
    /// (<c>Fixtures/000.jpg</c>) is in TrOCR-printed's training
    /// distribution. A whole-receipt image is broader than the
    /// single-line crops the model was trained on, but at least one
    /// large-print word should still be recognised. We don't pin the
    /// exact transcription — the model is capped at 20 tokens so it
    /// only catches a fragment — but at least one expected word from
    /// the receipt should appear.
    /// </summary>
    [Fact]
    public async Task InferBatch_RealReceiptFixture_TranscribesRecognizableWord()
    {
        if (!Fp32Available) return;

        string fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "000.jpg");
        if (!File.Exists(fixturePath)) return;

        Pool pool = GetService<Pool>();
        Arena inputArena = pool.Backing.RentArena();
        Arena targetArena = pool.Backing.RentArena();

        try
        {
            using TrOcrModel model = new(name: "trocr_printed", encoderModelFilePath: Fp32EncoderPath);

            byte[] jpg = await File.ReadAllBytesAsync(fixturePath);
            DatumIngest.Functions.ValueRef[][] inputs =
                [[DatumIngest.Functions.ValueRef.FromBytes(DataKind.Image, jpg)]];

            IReadOnlyList<DatumIngest.Functions.ValueRef> outputs = await model.InferBatchAsync(
                inputs, overrides: [], cancellationToken: CancellationToken.None);

            string text = outputs[0].AsString();
            Assert.False(string.IsNullOrWhiteSpace(text), "TrOCR returned empty output for receipt fixture.");
            Assert.DoesNotContain('Ġ', text);
            Assert.DoesNotContain('Ċ', text);

            // Receipt 000.jpg is the SROIE-style "BOOK TA_K (TAMAN DAYA)
            // SDN BHD" receipt. Words present at sufficient print size
            // for TrOCR to plausibly latch onto. We accept any one match
            // — the exact word the model picks depends on which line
            // dominates the 384×384 squashed view.
            string[] candidates =
            [
                "tan", "woon", "yann", "book", "taman", "daya", "johor",
                "cash", "bill", "total", "change", "thank", "rm",
            ];
            bool matched = candidates.Any(c =>
                text.Contains(c, StringComparison.OrdinalIgnoreCase));
            Assert.True(
                matched,
                $"TrOCR output '{text}' did not contain any expected receipt word ({string.Join(", ", candidates)}).");
        }
        finally
        {
            pool.Backing.TryReturn(inputArena);
            pool.Backing.TryReturn(targetArena);
        }
    }

    /// <summary>
    /// Smoke test that the fp16 variant runs end-to-end when the file
    /// loads successfully. Skipped if the merged fp16 decoder hits the
    /// known optimum-cli "outer scope value" graph-validity issue.
    /// </summary>
    [Fact]
    public async Task InferBatch_Fp16_ReturnsWellFormedTranscription()
    {
        if (!Fp16Available) return;

        Pool pool = GetService<Pool>();
        Arena inputArena = pool.Backing.RentArena();
        Arena targetArena = pool.Backing.RentArena();

        try
        {
            using TrOcrModel model = new(
                name: "trocr_printed_fp16",
                encoderModelFilePath: Fp16EncoderPath,
                decoderFileName: "decoder_model_merged_fp16.onnx");

            byte[] png = MakeTextPng("HELLO");
            DatumIngest.Functions.ValueRef[][] inputs =
                [[DatumIngest.Functions.ValueRef.FromBytes(DataKind.Image, png)]];

            IReadOnlyList<DatumIngest.Functions.ValueRef> outputs = await model.InferBatchAsync(
                inputs, overrides: [], cancellationToken: CancellationToken.None);

            string text = outputs[0].AsString();
            Assert.False(string.IsNullOrWhiteSpace(text), "fp16 TrOCR returned empty output.");
            Assert.DoesNotContain('Ġ', text);
            Assert.DoesNotContain('Ċ', text);
        }
        finally
        {
            pool.Backing.TryReturn(inputArena);
            pool.Backing.TryReturn(targetArena);
        }
    }

    /// <summary>
    /// Catalog round-trip for the fp32 entry: registering via
    /// <see cref="BuiltinModels"/> resolves to a usable
    /// <see cref="TrOcrModel"/> with the expected metadata.
    /// </summary>
    [Fact]
    public void Catalog_RegisterAndResolveFp32_YieldsTrOcrModel()
    {
        if (!Fp32Available) return;

        ModelCatalog catalog = new(modelDirectory: ModelCatalog.DefaultModelDirectory);
        BuiltinModels.RegisterTrOcrPrinted(catalog);

        ModelCatalogEntry? entry = catalog.TryGetEntry("trocr_printed");
        Assert.NotNull(entry);
        Assert.Equal("onnx", entry!.Backend);
        Assert.Equal(BuiltinModels.TrOcrPrintedEncoderRelativePath, entry.RelativePath);
        Assert.Equal("ocr", entry.Category);
        Assert.NotNull(entry.Modalities);
        Assert.Contains("image", entry.Modalities!);
        Assert.Contains("text", entry.Modalities!);

        using ModelLease lease = catalog.ResolveLeaseSynchronously("trocr_printed");
        Assert.IsType<TrOcrModel>(lease.Model);
    }

    /// <summary>
    /// Catalog round-trip for the fp16 entry: same shape as fp32 but a
    /// different relative path + display name.
    /// </summary>
    [Fact]
    public void Catalog_RegisterAndResolveFp16_YieldsTrOcrModel()
    {
        if (!Fp16Available) return;

        ModelCatalog catalog = new(modelDirectory: ModelCatalog.DefaultModelDirectory);
        BuiltinModels.RegisterTrOcrPrintedFp16(catalog);

        ModelCatalogEntry? entry = catalog.TryGetEntry("trocr_printed_fp16");
        Assert.NotNull(entry);
        Assert.Equal(BuiltinModels.TrOcrPrintedFp16EncoderRelativePath, entry!.RelativePath);

        using ModelLease lease = catalog.ResolveLeaseSynchronously("trocr_printed_fp16");
        Assert.IsType<TrOcrModel>(lease.Model);
    }
}
